using System.Globalization;
using Filemaster.Domain;

namespace Filemaster.Infrastructure;

/// <summary>
/// Lo stream di un download: avvolge lo stream della risposta HTTP, di sola lettura e solo in avanti, e ne <b>conta i byte contro
/// <c>Content-Length</c></b> senza fidarsi del gestore HTTP (su .NET Framework e su .NET moderno un EOF prematuro non si comporta
/// allo stesso modo). Possiede la risposta HTTP e la scadenza del trasferimento: smaltirlo li rilascia.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cosa rileva.</b> La fine del flusso con meno byte di <c>Content-Length</c> e' <see cref="ContentIntegrityException"/> con
/// <see cref="ContentIntegrityException.IsTruncated"/> vero; piu' byte del dichiarato e' <see cref="ContentIntegrityException"/> con
/// <c>IsTruncated</c> falso (si rileva alla lettura che supera il dichiarato, senza consegnare i byte in eccesso). Un'eccezione di
/// rete dello stream sottostante durante la lettura (<see cref="IOException"/>, <see cref="System.Net.Http.HttpRequestException"/>,
/// <see cref="System.Net.WebException"/>, <see cref="System.Net.Sockets.SocketException"/>, <see cref="ObjectDisposedException"/> per
/// connessione chiusa) e' <see cref="ContentIntegrityException"/> con <c>IsTruncated</c> vero e la causa in
/// <see cref="Exception.InnerException"/>: se il corpo e' <c>chunked</c> e quindi senza <c>Content-Length</c> e' l'unico segnale
/// possibile. Senza <c>Content-Length</c> e senza eccezione la fine del flusso e' una fine normale (non si puo' dire altro).
/// </para>
/// <para>
/// <b>Annullamento e tempo.</b> L'annullamento del chiamante (il token della lettura o quello con cui si e' aperto il download) e'
/// sempre <see cref="OperationCanceledException"/>, mai un verdetto; lo scadere del tempo del trasferimento
/// (<see cref="FilemasterOptions.TransferTimeout"/>) e' <see cref="FilemasterTimeoutException"/>. Si distinguono dallo stato dei
/// token (prima quello del chiamante), non dal tipo dell'eccezione. Allo scattare della scadenza lo stream rilascia la risposta HTTP,
/// cosi' una lettura bloccata (anche su .NET Framework, che ignora il token delle letture) fallisce subito.
/// </para>
/// <para>
/// <b>Il verdetto e' sticky.</b> Dopo un <see cref="ContentIntegrityException"/> ogni lettura rilancia un'eccezione equivalente (nuova,
/// con la prima come causa): chi ha inghiottito la prima non vede mai un EOF pulito. L'annullamento e il tempo scaduto non sono
/// verdetti. Dopo la fine pulita ogni lettura restituisce 0. Una lettura con <c>count</c> zero restituisce 0 e non conclude nulla.
/// </para>
/// <para>
/// <b>Smaltimento.</b> Idempotente, non lancia per il contenuto non letto (un dispose anticipato non da' nessun verdetto) e rilascia
/// tutto: lo stream sottostante, la risposta HTTP e la scadenza del trasferimento. Dopo, ogni lettura lancia
/// <see cref="ObjectDisposedException"/>. Non e' thread-safe: una lettura alla volta, come ogni stream. La verifica dello SHA-256
/// non e' di questo tipo: e' <c>VerifiedContentStream</c> dell'Application, che si mette sopra.
/// </para>
/// </remarks>
internal sealed class DownloadStream : Stream
{
    private readonly Stream _inner;
    private readonly long? _expectedLength;
    private readonly int _statusCode;
    private readonly string? _requestId;
    private readonly IDisposable? _owner;
    private readonly Deadline? _deadline;
    private readonly CancellationToken _callerToken;
    private readonly CancellationTokenRegistration _abortRegistration;
    private long _bytesRead;
    private bool _ended;
    private ContentIntegrityException? _failure;
    private int _disposed;

    /// <summary>Avvolge <paramref name="inner"/>.</summary>
    /// <param name="inner">Lo stream della risposta, leggibile.</param>
    /// <param name="expectedLength">Il <c>Content-Length</c> della risposta; null se manca (corpo <c>chunked</c>).</param>
    /// <param name="statusCode">Lo status del download (200 o 206), per le eccezioni.</param>
    /// <param name="requestId">L'id di correlazione con i log del server, per le eccezioni.</param>
    /// <param name="owner">La risposta HTTP (o qualunque risorsa che alimenta lo stream), smaltita dopo lo stream e all'abort; null se non c'e'.</param>
    /// <param name="deadline">La scadenza del trasferimento, che questo stream possiede e smaltisce; null se non c'e' (nessun tempo).</param>
    /// <param name="callerToken">Il token di chi ha aperto il download: se e' scattato, una lettura fallita e' un annullamento.</param>
    internal DownloadStream(
        Stream inner,
        long? expectedLength,
        int statusCode,
        string? requestId,
        IDisposable? owner,
        Deadline? deadline,
        CancellationToken callerToken)
    {
        Guard.NotNull(inner);
        if (!inner.CanRead)
        {
            throw new ArgumentException("Lo stream da leggere deve essere leggibile.", nameof(inner));
        }

        if (expectedLength is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedLength), expectedLength, "La lunghezza attesa non puo' essere negativa.");
        }

        _inner = inner;
        _expectedLength = expectedLength;
        _statusCode = statusCode;
        _requestId = requestId;
        _owner = owner;
        _deadline = deadline;
        _callerToken = callerToken;

        // Per ultimo: se il token e' gia' scattato la registrazione esegue Abort subito, a campi tutti assegnati.
        if (deadline is not null)
        {
            _abortRegistration = deadline.Token.Register(static state => ((DownloadStream)state!).Abort(), this);
        }
    }

    /// <summary>Vero finche' lo stream non e' smaltito.</summary>
    public override bool CanRead => Volatile.Read(ref _disposed) == 0;

    /// <summary>Sempre falso: lo stream va solo in avanti.</summary>
    public override bool CanSeek => false;

    /// <summary>Sempre falso: lo stream e' di sola lettura.</summary>
    public override bool CanWrite => false;

    /// <summary>Non supportata: la lunghezza si legge dall'intestazione (<c>DocumentContent.ContentLength</c>).</summary>
    public override long Length => throw new NotSupportedException("Lo stream di un download non ha una lunghezza: si legge solo in avanti.");

    /// <summary>Non supportata: lo stream non si riposiziona.</summary>
    public override long Position
    {
        get => throw new NotSupportedException("Lo stream di un download non ha una posizione: si legge solo in avanti.");
        set => throw new NotSupportedException("Lo stream di un download non si riposiziona: si legge solo in avanti.");
    }

    /// <summary>Non fa nulla: lo stream e' di sola lettura.</summary>
    public override void Flush()
    {
    }

    /// <summary>Non fa nulla: lo stream e' di sola lettura.</summary>
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Non supportato.</summary>
    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException("Lo stream di un download non si riposiziona: si legge solo in avanti.");

    /// <summary>Non supportato.</summary>
    public override void SetLength(long value) =>
        throw new NotSupportedException("Lo stream di un download e' di sola lettura.");

    /// <summary>Non supportata.</summary>
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Lo stream di un download e' di sola lettura.");

    /// <summary>Legge fino a <paramref name="count"/> byte; restituisce 0 solo alla fine del flusso, dopo il controllo della lunghezza (vedi le note del tipo).</summary>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBuffer(buffer, offset, count);
        if (!MustReadInner(count))
        {
            return 0;
        }

        int read;
        try
        {
            read = _inner.Read(buffer, offset, count);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            throw Translate(exception, CancellationToken.None);
        }

        return Account(read, count);
    }

    /// <summary>Come <see cref="Read(byte[], int, int)"/>, in modo asincrono. Il token si passa allo stream sottostante.</summary>
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBuffer(buffer, offset, count);
        if (!MustReadInner(count))
        {
            return 0;
        }

        int read;
        try
        {
#pragma warning disable CA1835 // Su netstandard2.0 l'overload con Memory non esiste: qui si inoltra la stessa forma con array.
            read = await _inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
#pragma warning restore CA1835
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            throw Translate(exception, cancellationToken);
        }

        return Account(read, count);
    }

#if NET
    /// <summary>Come <see cref="Read(byte[], int, int)"/>, su uno span.</summary>
    public override int Read(Span<byte> buffer)
    {
        if (!MustReadInner(buffer.Length))
        {
            return 0;
        }

        int read;
        try
        {
            read = _inner.Read(buffer);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            throw Translate(exception, CancellationToken.None);
        }

        return Account(read, buffer.Length);
    }

    /// <summary>Come <see cref="ReadAsync(byte[], int, int, CancellationToken)"/>, su una memoria.</summary>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!MustReadInner(buffer.Length))
        {
            return 0;
        }

        int read;
        try
        {
            read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            throw Translate(exception, cancellationToken);
        }

        return Account(read, buffer.Length);
    }
#endif

    /// <summary>Rilascia lo stream sottostante, la risposta HTTP e la scadenza. Non da' nessun verdetto e non lancia per il contenuto; e' idempotente.</summary>
    protected override void Dispose(bool disposing)
    {
        try
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                // Prima la registrazione (cosi' un abort concorrente non parte durante lo smaltimento), poi lo stream, la risposta e
                // la scadenza: ognuno viene rilasciato anche se il precedente lancia.
                try
                {
                    _abortRegistration.Dispose();
                }
                finally
                {
                    try
                    {
                        _inner.Dispose();
                    }
                    finally
                    {
                        try
                        {
                            _owner?.Dispose();
                        }
                        finally
                        {
                            _deadline?.Dispose();
                        }
                    }
                }
            }
        }
        finally
        {
            base.Dispose(disposing);
        }
    }

    private static void ValidateBuffer(byte[] buffer, int offset, int count)
    {
        Guard.NotNull(buffer);
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "L'offset non puo' essere negativo.");
        }

        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "Il numero di byte non puo' essere negativo.");
        }

        if (buffer.Length - offset < count)
        {
            throw new ArgumentException("Offset e numero di byte escono dal buffer.", nameof(count));
        }
    }

    // Cio' che puo' uscire da una lettura di rete e va tradotto: gli errori di rete, la connessione chiusa e la cancellazione. Il resto
    // (un errore di programmazione dello stream sottostante) passa com'e'.
    private static bool IsReadFailure(Exception exception) =>
        exception is OperationCanceledException or ObjectDisposedException || NetworkFailures.IsNetworkFailure(exception);

    // Controlla lo stato e dice se serve davvero leggere dallo stream sottostante: no dopo la fine pulita, o senza byte richiesti.
    // Dopo un verdetto negativo rilancia sempre.
    private bool MustReadInner(int count)
    {
#if NET
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
#else
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(DownloadStream));
        }
#endif

        if (_failure is not null)
        {
            throw RepeatFailure(_failure);
        }

        return !_ended && count > 0;
    }

    // Conta i byte e li confronta con Content-Length. Restituisce quanti byte consegnare.
    private int Account(int read, int count)
    {
        if (read < 0 || read > count)
        {
            throw new InvalidOperationException("Lo stream interno ha restituito un numero di byte letti non valido.");
        }

        if (read > 0)
        {
            _bytesRead += read;
            if (_expectedLength is { } expectedLength && _bytesRead > expectedLength)
            {
                _failure = new ContentIntegrityException(
                    isTruncated: false,
                    _statusCode,
                    message: string.Format(CultureInfo.InvariantCulture, "Il contenuto e' piu' lungo del previsto: ricevuti almeno {0} byte su {1} attesi.", _bytesRead, expectedLength),
                    expectedLength: expectedLength,
                    actualLength: _bytesRead,
                    requestId: _requestId);
                throw _failure;
            }

            return read;
        }

        // EOF: la lettura ha restituito 0 con count maggiore di zero.
        _ended = true;
        if (_expectedLength is { } expected && _bytesRead != expected)
        {
            _failure = new ContentIntegrityException(
                isTruncated: _bytesRead < expected,
                _statusCode,
                expectedLength: expected,
                actualLength: _bytesRead,
                requestId: _requestId);
            throw _failure;
        }

        return 0;
    }

    // L'eccezione da lanciare per una lettura fallita. L'ordine conta: prima il token di chi chiama (annullamento), poi lo
    // smaltimento, poi il tempo scaduto, e solo se nessuno dei tre e' successo l'errore e' un download interrotto.
    private Exception Translate(Exception exception, CancellationToken readToken)
    {
        if (readToken.IsCancellationRequested)
        {
            return exception as OperationCanceledException ?? new OperationCanceledException(readToken);
        }

        if (_callerToken.IsCancellationRequested)
        {
            return exception as OperationCanceledException ?? new OperationCanceledException(_callerToken);
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            return new ObjectDisposedException("Lo stream e' stato smaltito durante la lettura.", exception);
        }

        if (_deadline is { Expired: true } || exception is OperationCanceledException)
        {
            return new FilemasterTimeoutException(
                "Il trasferimento del contenuto non e' finito entro il tempo previsto: la connessione e' stata interrotta.",
                exception,
                _requestId);
        }

        _failure = new ContentIntegrityException(
            isTruncated: true,
            _statusCode,
            expectedLength: _expectedLength,
            actualLength: _bytesRead,
            requestId: _requestId,
            innerException: exception);
        return _failure;
    }

    // Le letture dopo il verdetto negativo lanciano un'eccezione nuova (stack pulito) che ha l'originale come causa.
    private static ContentIntegrityException RepeatFailure(ContentIntegrityException failure) =>
        new(
            failure.IsTruncated,
            failure.StatusCode,
            failure.Message,
            failure.ExpectedLength,
            failure.ActualLength,
            failure.RequestId,
            failure);

    // Alla scadenza (o all'annullamento di chi ha aperto il download) si rilascia la risposta: la connessione si chiude e una lettura
    // bloccata fallisce subito. Gira dentro Cancel(): non deve mai lanciare.
    private void Abort()
    {
        try
        {
            _owner?.Dispose();
        }
#pragma warning disable CA1031 // Un callback di annullamento che lancia romperebbe il Cancel() di chi lo chiama: qui si inghiotte tutto.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}
