// Su net8/net10 CA1844 chiede l'override di ReadAsync(Memory<byte>); su net48 non esiste. La base lo inoltra a ReadAsync(byte[], ...), che e' quello che interessa.
#pragma warning disable CA1844
// Su net8/net10 CA1835 e CA1845 preferiscono gli overload con Memory/Span, che su net48 non esistono: i test provano proprio la forma con array.
#pragma warning disable CA1835

namespace Filemaster.UnitTests.Infrastructure;

/// <summary>Come si legge uno stream: le quattro firme di lettura (le ultime due esistono solo da net8; altrove ricadono sull'array).</summary>
public enum ReadApi
{
    /// <summary><c>Read(byte[], int, int)</c>.</summary>
    Sync,

    /// <summary><c>ReadAsync(byte[], int, int, CancellationToken)</c>.</summary>
    Async,

    /// <summary><c>ReadAsync(Memory&lt;byte&gt;, CancellationToken)</c>.</summary>
    AsyncMemory,

    /// <summary><c>Read(Span&lt;byte&gt;)</c>.</summary>
    SyncSpan,
}

/// <summary>Letture e smaltimenti di prova, uguali per ogni firma di <see cref="ReadApi"/>.</summary>
internal static class StreamReading
{
    internal static async Task<int> ReadOnceAsync(Stream stream, ReadApi api, byte[] buffer, int offset, int count, CancellationToken cancellationToken = default)
    {
        switch (api)
        {
            case ReadApi.Sync:
                return stream.Read(buffer, offset, count);
#if NET
            case ReadApi.AsyncMemory:
                return await stream.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
            case ReadApi.SyncSpan:
                return stream.Read(buffer.AsSpan(offset, count));
#endif
            default:
                return await stream.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Legge fino alla fine a pezzi di <paramref name="chunk"/> byte e restituisce tutti i byte letti.</summary>
    internal static async Task<byte[]> ReadToEndAsync(Stream stream, ReadApi api, int chunk, CancellationToken cancellationToken = default)
    {
        var all = new MemoryStream();
        var buffer = new byte[chunk];
        while (true)
        {
            var read = await ReadOnceAsync(stream, api, buffer, 0, chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return all.ToArray();
            }

            all.Write(buffer, 0, read);
        }
    }

    internal static byte[] Pattern(int length)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)((i * 31) + 7);
        }

        return data;
    }
}

/// <summary>Una risorsa che conta gli smaltimenti, puo' lanciare, e scrive in un registro (per provare l'ordine) e/o esegue un'azione.</summary>
internal sealed class DisposeProbe : IDisposable
{
    private readonly string _name;
    private readonly List<string>? _log;

    internal DisposeProbe(string name = "owner", List<string>? log = null)
    {
        _name = name;
        _log = log;
    }

    internal int Count { get; private set; }

    internal Exception? Throws { get; set; }

    internal Action? OnDispose { get; set; }

    public void Dispose()
    {
        Count++;
        _log?.Add(_name);
        OnDispose?.Invoke();
        if (Throws is not null)
        {
            throw Throws;
        }
    }
}

/// <summary>
/// Uno stream che serve i primi byte e poi si blocca, come una connessione lenta: la lettura resta in attesa finche' un test non la
/// fa fallire con <see cref="Fail"/> (per esempio perche' la risposta e' stata smaltita). <see cref="ReadStarted"/> si completa quando
/// una lettura e' entrata in attesa: i test aspettano quel segnale invece di un tempo.
/// </summary>
internal sealed class HangingStream : Stream
{
    // Nessuna lettura bloccata puo' fermare l'esecuzione: se nessuno la rilascia (un bug del codice sotto test) il test fallisce qui, dopo
    // pochi secondi, con un messaggio che lo dice. TimeoutException non e' un errore di rete: DownloadStream la lascia passare com'e'.
    private static readonly TimeSpan MaxStall = TimeSpan.FromSeconds(10);
    private const string StallMessage = "La lettura bloccata non e' stata rilasciata entro 10 secondi: la risposta HTTP non e' stata smaltita o annullata.";

    private readonly byte[] _data;
    private readonly TaskCompletionSource<int> _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _position;

    internal HangingStream(byte[] dataBeforeTheStall)
    {
        _data = dataBeforeTheStall;
    }

    internal Task ReadStarted => Waiting.Within(_started.Task);

    internal int DisposeCount { get; private set; }

    /// <summary>Fa fallire la lettura in attesa (e ogni lettura successiva dopo i byte iniziali) con questa eccezione.</summary>
    internal void Fail(Exception exception) => _gate.TrySetException(exception);

    public override bool CanRead => DisposeCount == 0;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (TryServe(buffer, offset, count, out var served))
        {
            return served;
        }

        _started.TrySetResult(true);
        // Non Task.Wait: su un task fallito lancerebbe un AggregateException, e il test vuole l'eccezione vera (GetResult sotto).
        if (!((IAsyncResult)_gate.Task).AsyncWaitHandle.WaitOne(MaxStall))
        {
            throw new TimeoutException(StallMessage);
        }

        return _gate.Task.GetAwaiter().GetResult();
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (TryServe(buffer, offset, count, out var served))
        {
            return served;
        }

        _started.TrySetResult(true);
        var finished = await Task.WhenAny(_gate.Task, Task.Delay(MaxStall, CancellationToken.None)).ConfigureAwait(false);
        if (finished != _gate.Task)
        {
            throw new TimeoutException(StallMessage);
        }

        return await _gate.Task.ConfigureAwait(false);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeCount++;
        }

        base.Dispose(disposing);
    }

    private bool TryServe(byte[] buffer, int offset, int count, out int served)
    {
        if (_position < _data.Length)
        {
            served = Math.Min(count, _data.Length - _position);
            Array.Copy(_data, _position, buffer, offset, served);
            _position += served;
            return true;
        }

        served = 0;
        return false;
    }
}
