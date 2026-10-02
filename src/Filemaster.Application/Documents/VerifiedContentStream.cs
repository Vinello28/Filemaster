using System.Globalization;
using System.Security.Cryptography;
using Filemaster.Domain;

namespace Filemaster.Application;

/// <summary>
/// Uno stream di sola lettura, solo in avanti, che passa i byte di un altro stream e ne calcola lo SHA-256 mentre li legge:
/// quando il flusso finisce confronta il risultato con l'impronta attesa (e, se indicata, la lunghezza) e, se non
/// corrispondono, lancia <see cref="ContentIntegrityException"/>. Per aprire il contenuto di un documento gia' verificato si
/// usa <see cref="DocumentStoreExtensions.OpenVerifiedContentAsync"/>; questo tipo serve a chi ha uno stream suo.
/// </summary>
/// <remarks>
/// <para>
/// <b>Il verdetto c'e' solo alla fine.</b> I byte arrivano al chiamante man mano, prima di sapere se sono giusti: non si puo'
/// verificare un hash senza aver letto tutto. Il verdetto si da' alla lettura che restituisce 0 con <c>count</c> maggiore di
/// zero, cioe' all'<b>EOF</b>: se l'hash (o la lunghezza attesa) non corrisponde quella lettura lancia
/// <see cref="ContentIntegrityException"/>; se corrisponde restituisce 0, e da quel momento ogni lettura restituisce 0. Una
/// lettura con <c>count</c> pari a zero restituisce 0 e non conclude nulla. Dopo un verdetto negativo <b>ogni lettura
/// successiva rilancia</b> l'eccezione: chi ha inghiottito la prima non vede mai un EOF pulito. Chi consuma lo stream deve
/// quindi leggere fino in fondo prima di fidarsi dei byte (<c>CopyTo</c> e <c>CopyToAsync</c> lo fanno).
/// </para>
/// <para>
/// <b>Smaltire in anticipo non da' nessun verdetto e non lancia.</b> Chi smaltisce lo stream prima dell'EOF non ha alcuna
/// garanzia sul contenuto letto: nessuna eccezione, nessun controllo. Lo smaltimento rilascia il calcolo dell'hash e, se
/// <c>leaveOpen</c> e' falso, lo stream interno; e' idempotente.
/// </para>
/// <para>
/// <b>Cosa non fa.</b> Non rileva da solo il troncamento del trasporto contro l'intestazione <c>Content-Length</c> (qui l'intestazione
/// non si conosce): lo fa lo stream che l'Infrastructure mette sotto di questo, che lancia <see cref="ContentIntegrityException"/>
/// con <see cref="ContentIntegrityException.IsTruncated"/> vero. Un'eccezione dello stream interno (questa compresa) passa
/// cosi' com'e': non e' un verdetto, non cambia lo stato e i byte restituiti prima restano contati. Se l'interno, dopo aver lanciato,
/// restituisce 0, il flusso e' comunque incompleto e l'hash non corrisponde: il verdetto negativo arriva da qui.
/// Se l'hash corrisponde ma la lunghezza indicata no, il verdetto e' comunque negativo (<c>IsTruncated</c> vero se i byte
/// sono meno degli attesi, falso se sono di piu').
/// </para>
/// <para>
/// Lo stream non e' thread-safe: una lettura alla volta, come ogni stream. Non e' riposizionabile: <c>Length</c>, <c>Position</c>,
/// <c>Seek</c>, <c>SetLength</c> e <c>Write</c> lanciano <see cref="NotSupportedException"/>.
/// </para>
/// </remarks>
public sealed class VerifiedContentStream : Stream
{
    private const int Sha256HexLength = 64;
    private const int SuccessStatusCode = 200;

    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private readonly byte[] _expectedHash;
    private readonly long? _expectedLength;
    private IncrementalHash? _hasher;
    private long _bytesRead;
    private bool _passed;
    private ContentIntegrityException? _failure;
    private int _disposed;

    /// <summary>Crea lo stream verificato sopra <paramref name="inner"/>.</summary>
    /// <param name="inner">Lo stream da leggere, leggibile; si legge dalla posizione corrente alla fine.</param>
    /// <param name="expectedSha256">
    /// L'impronta SHA-256 attesa: 64 cifre esadecimali ASCII, maiuscole o minuscole (il server le scrive minuscole).
    /// </param>
    /// <param name="expectedLength">I byte attesi; null se non sono noti. Se e' indicata, una lunghezza diversa e' un verdetto negativo.</param>
    /// <param name="leaveOpen">Vero per non smaltire <paramref name="inner"/> insieme a questo stream; falso (default) per smaltirlo.</param>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> o <paramref name="expectedSha256"/> e' null.</exception>
    /// <exception cref="ArgumentException"><paramref name="inner"/> non e' leggibile, oppure <paramref name="expectedSha256"/> non e' di 64 cifre esadecimali ASCII.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="expectedLength"/> e' negativa.</exception>
    public VerifiedContentStream(Stream inner, string expectedSha256, long? expectedLength = null, bool leaveOpen = false)
    {
        Guard.NotNull(inner);
        Guard.NotNull(expectedSha256);
        if (!inner.CanRead)
        {
            throw new ArgumentException("Lo stream da verificare deve essere leggibile.", nameof(inner));
        }

        if (expectedLength is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedLength), expectedLength, "La lunghezza attesa non puo' essere negativa.");
        }

        _expectedHash = ParseSha256(expectedSha256);
        _inner = inner;
        _expectedLength = expectedLength;
        _leaveOpen = leaveOpen;
        _hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    }

    /// <summary>Vero finche' lo stream non e' smaltito.</summary>
    public override bool CanRead => Volatile.Read(ref _disposed) == 0;

    /// <summary>Sempre falso: lo stream va solo in avanti.</summary>
    public override bool CanSeek => false;

    /// <summary>Sempre falso: lo stream e' di sola lettura.</summary>
    public override bool CanWrite => false;

    /// <summary>Non supportata: la lunghezza puo' non essere nota (usare <see cref="DocumentContent.ContentLength"/>).</summary>
    /// <exception cref="NotSupportedException">Sempre.</exception>
    public override long Length => throw new NotSupportedException("Lo stream verificato non ha una lunghezza: si legge solo in avanti.");

    /// <summary>Non supportata: lo stream non si riposiziona.</summary>
    /// <exception cref="NotSupportedException">Sempre, in lettura e in scrittura.</exception>
    public override long Position
    {
        get => throw new NotSupportedException("Lo stream verificato non ha una posizione: si legge solo in avanti.");
        set => throw new NotSupportedException("Lo stream verificato non si riposiziona: si legge solo in avanti.");
    }

    /// <summary>Non fa nulla: lo stream e' di sola lettura.</summary>
    public override void Flush()
    {
    }

    /// <summary>Non fa nulla: lo stream e' di sola lettura.</summary>
    /// <param name="cancellationToken">Ignorato.</param>
    /// <returns>Un task gia' completato.</returns>
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Non supportato: lo stream non si riposiziona.</summary>
    /// <param name="offset">Ignorato.</param>
    /// <param name="origin">Ignorato.</param>
    /// <returns>Mai: lancia sempre.</returns>
    /// <exception cref="NotSupportedException">Sempre.</exception>
    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException("Lo stream verificato non si riposiziona: si legge solo in avanti.");

    /// <summary>Non supportato: lo stream e' di sola lettura.</summary>
    /// <param name="value">Ignorato.</param>
    /// <exception cref="NotSupportedException">Sempre.</exception>
    public override void SetLength(long value) =>
        throw new NotSupportedException("Lo stream verificato e' di sola lettura.");

    /// <summary>Non supportata: lo stream e' di sola lettura.</summary>
    /// <param name="buffer">Ignorato.</param>
    /// <param name="offset">Ignorato.</param>
    /// <param name="count">Ignorato.</param>
    /// <exception cref="NotSupportedException">Sempre.</exception>
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Lo stream verificato e' di sola lettura.");

    /// <summary>
    /// Legge fino a <paramref name="count"/> byte e li aggiunge all'hash. Restituisce 0 solo alla fine del flusso, dopo il verdetto
    /// (vedi le note del tipo).
    /// </summary>
    /// <param name="buffer">Dove scrivere i byte letti.</param>
    /// <param name="offset">Da quale posizione di <paramref name="buffer"/>.</param>
    /// <param name="count">Quanti byte al massimo; con zero restituisce 0 senza concludere nulla.</param>
    /// <returns>I byte letti; 0 alla fine del flusso (o con <paramref name="count"/> pari a zero).</returns>
    /// <exception cref="ObjectDisposedException">Lo stream e' stato smaltito.</exception>
    /// <exception cref="ContentIntegrityException">Alla fine del flusso l'hash (o la lunghezza) non corrisponde; e ogni lettura dopo il primo verdetto negativo.</exception>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBuffer(buffer, offset, count);
        if (!MustReadInner(count))
        {
            return 0;
        }

        var read = _inner.Read(buffer, offset, count);
        if (CheckRead(read, count) > 0)
        {
            _hasher!.AppendData(buffer, offset, read);
            _bytesRead += read;
            return read;
        }

        Conclude();
        return 0;
    }

    /// <summary>
    /// Come <see cref="Read(byte[], int, int)"/>, in modo asincrono. Il token si passa allo stream interno.
    /// </summary>
    /// <param name="buffer">Dove scrivere i byte letti.</param>
    /// <param name="offset">Da quale posizione di <paramref name="buffer"/>.</param>
    /// <param name="count">Quanti byte al massimo; con zero restituisce 0 senza concludere nulla.</param>
    /// <param name="cancellationToken">Per annullare la lettura; l'annullamento non e' un verdetto.</param>
    /// <returns>I byte letti; 0 alla fine del flusso (o con <paramref name="count"/> pari a zero).</returns>
    /// <exception cref="ObjectDisposedException">Lo stream e' stato smaltito.</exception>
    /// <exception cref="ContentIntegrityException">Alla fine del flusso l'hash (o la lunghezza) non corrisponde; e ogni lettura dopo il primo verdetto negativo.</exception>
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBuffer(buffer, offset, count);
        if (!MustReadInner(count))
        {
            return 0;
        }

#pragma warning disable CA1835 // Su netstandard2.0 l'overload con Memory non esiste: qui si inoltra la stessa forma con array.
        var read = await _inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
#pragma warning restore CA1835
        if (CheckRead(read, count) > 0)
        {
            _hasher!.AppendData(buffer, offset, read);
            _bytesRead += read;
            return read;
        }

        Conclude();
        return 0;
    }

#if NET
    /// <summary>Come <see cref="Read(byte[], int, int)"/>, su uno span.</summary>
    /// <param name="buffer">Dove scrivere i byte letti; vuoto restituisce 0 senza concludere nulla.</param>
    /// <returns>I byte letti; 0 alla fine del flusso (o con <paramref name="buffer"/> vuoto).</returns>
    /// <exception cref="ObjectDisposedException">Lo stream e' stato smaltito.</exception>
    /// <exception cref="ContentIntegrityException">Alla fine del flusso l'hash (o la lunghezza) non corrisponde; e ogni lettura dopo il primo verdetto negativo.</exception>
    public override int Read(Span<byte> buffer)
    {
        if (!MustReadInner(buffer.Length))
        {
            return 0;
        }

        var read = _inner.Read(buffer);
        if (CheckRead(read, buffer.Length) > 0)
        {
            _hasher!.AppendData(buffer.Slice(0, read));
            _bytesRead += read;
            return read;
        }

        Conclude();
        return 0;
    }

    /// <summary>Come <see cref="ReadAsync(byte[], int, int, CancellationToken)"/>, su una memoria.</summary>
    /// <param name="buffer">Dove scrivere i byte letti; vuota restituisce 0 senza concludere nulla.</param>
    /// <param name="cancellationToken">Per annullare la lettura; l'annullamento non e' un verdetto.</param>
    /// <returns>I byte letti; 0 alla fine del flusso (o con <paramref name="buffer"/> vuota).</returns>
    /// <exception cref="ObjectDisposedException">Lo stream e' stato smaltito.</exception>
    /// <exception cref="ContentIntegrityException">Alla fine del flusso l'hash (o la lunghezza) non corrisponde; e ogni lettura dopo il primo verdetto negativo.</exception>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!MustReadInner(buffer.Length))
        {
            return 0;
        }

        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (CheckRead(read, buffer.Length) > 0)
        {
            _hasher!.AppendData(buffer.Span.Slice(0, read));
            _bytesRead += read;
            return read;
        }

        Conclude();
        return 0;
    }
#endif

    /// <summary>Rilascia il calcolo dell'hash e, se non e' stato chiesto <c>leaveOpen</c>, lo stream interno. Non da' nessun verdetto e non lancia per il contenuto; e' idempotente.</summary>
    /// <param name="disposing">Vero se chiamato da <c>Dispose</c>, falso dal finalizzatore.</param>
    protected override void Dispose(bool disposing)
    {
        try
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _hasher?.Dispose();
                _hasher = null;
                if (!_leaveOpen)
                {
                    _inner.Dispose();
                }
            }
        }
        finally
        {
            base.Dispose(disposing);
        }
    }

    private static byte[] ParseSha256(string expectedSha256)
    {
        if (expectedSha256.Length != Sha256HexLength)
        {
            throw new ArgumentException(
                "L'impronta SHA-256 attesa deve avere " + Sha256HexLength.ToString(CultureInfo.InvariantCulture) + " cifre esadecimali.",
                nameof(expectedSha256));
        }

        var bytes = new byte[Sha256HexLength / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            var high = HexValue(expectedSha256[2 * i]);
            var low = HexValue(expectedSha256[(2 * i) + 1]);
            if (high < 0 || low < 0)
            {
                throw new ArgumentException("L'impronta SHA-256 attesa puo' contenere solo cifre esadecimali ASCII (0-9, a-f, A-F).", nameof(expectedSha256));
            }

            bytes[i] = (byte)((high << 4) | low);
        }

        return bytes;
    }

    // Una cifra esadecimale ASCII, maiuscola o minuscola; -1 per qualunque altro carattere (char.IsDigit accetterebbe cifre Unicode).
    private static int HexValue(char c) =>
        c >= '0' && c <= '9' ? c - '0'
        : c >= 'a' && c <= 'f' ? c - 'a' + 10
        : c >= 'A' && c <= 'F' ? c - 'A' + 10
        : -1;

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

    // Controlla lo stato e dice se serve davvero leggere dallo stream interno: no se il verdetto e' gia' positivo o se non si
    // chiedono byte (una lettura vuota non puo' concludere nulla). Dopo un verdetto negativo rilancia sempre.
    private bool MustReadInner(int count)
    {
#if NET
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
#else
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(VerifiedContentStream));
        }
#endif

        if (_failure is not null)
        {
            throw RepeatFailure(_failure);
        }

        return !_passed && count > 0;
    }

    // Uno stream interno che restituisce un numero fuori da 0..count romperebbe l'hash in silenzio.
    private static int CheckRead(int read, int count)
    {
        if (read < 0 || read > count)
        {
            throw new InvalidOperationException("Lo stream interno ha restituito un numero di byte letti non valido.");
        }

        return read;
    }

    // EOF: la lettura ha restituito 0 con count maggiore di zero.
    private void Conclude()
    {
        var hasher = _hasher!;
        var actualHash = hasher.GetHashAndReset();
        hasher.Dispose();
        _hasher = null;

        var hashMatches = SameBytes(actualHash, _expectedHash);
        var lengthMatches = _expectedLength is not { } expectedLength || expectedLength == _bytesRead;
        if (hashMatches && lengthMatches)
        {
            _passed = true;
            return;
        }

        var isTruncated = _expectedLength is { } expected && _bytesRead < expected;
        var tooLong = hashMatches && !lengthMatches && !isTruncated;
        _failure = new ContentIntegrityException(
            isTruncated,
            SuccessStatusCode,
            message: tooLong
                ? string.Format(CultureInfo.InvariantCulture, "Il contenuto e' piu' lungo del previsto: ricevuti {0} byte su {1} attesi.", _bytesRead, _expectedLength)
                : null,
            expectedLength: _expectedLength,
            actualLength: _bytesRead);
        throw _failure;
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

    private static bool SameBytes(byte[] left, byte[] right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        var difference = 0;
        for (var i = 0; i < left.Length; i++)
        {
            difference |= left[i] ^ right[i];
        }

        return difference == 0;
    }
}
