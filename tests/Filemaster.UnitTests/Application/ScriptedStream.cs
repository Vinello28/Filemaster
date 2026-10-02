// Su net8/net10 CA1844 chiede l'override di ReadAsync(Memory<byte>); su net48 non esiste. La base lo inoltra a ReadAsync(byte[], ...), che e' quello che interessa.
#pragma warning disable CA1844

namespace Filemaster.UnitTests.Application;

/// <summary>
/// Uno stream di prova sopra un array: restituisce i byte a pezzi di al massimo <c>maxChunk</c>, conta le letture e gli smaltimenti,
/// ricorda i token ricevuti e puo' lanciare (o restituire un numero assurdo) a una lettura precisa. Una lettura che lancia non
/// consuma byte, come un errore di rete che non ha consegnato nulla.
/// </summary>
internal sealed class ScriptedStream : Stream
{
    private readonly byte[] _data;
    private readonly int _maxChunk;
    private int _position;

    public ScriptedStream(byte[] data, int maxChunk = int.MaxValue)
    {
        _data = data;
        _maxChunk = maxChunk;
    }

    /// <summary>Quante volte e' stata chiamata una lettura (sincrona o asincrona), comprese quelle che lanciano.</summary>
    public int ReadCalls { get; private set; }

    /// <summary>Quante volte e' stato smaltito davvero (<c>Dispose(true)</c>).</summary>
    public int DisposeCount { get; private set; }

    /// <summary>Se impostato, ogni smaltimento vero aggiunge qui la parola "stream" (per provare l'ordine rispetto ad altri smaltimenti).</summary>
    public List<string>? Log { get; set; }

    /// <summary>I token ricevuti dalle letture asincrone, nell'ordine.</summary>
    public List<CancellationToken> Tokens { get; } = new();

    /// <summary>L'eccezione da lanciare alla lettura con questo indice (da zero, contando ogni chiamata).</summary>
    public Dictionary<int, Exception> FailAtRead { get; } = new();

    /// <summary>Se impostato, ogni lettura restituisce questo valore (in funzione di <c>count</c>) invece di leggere: per simulare un interno scorretto.</summary>
    public Func<int, int>? ReadResult { get; set; }

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
        var index = ReadCalls++;
        if (FailAtRead.TryGetValue(index, out var failure))
        {
            throw failure;
        }

        if (ReadResult is not null)
        {
            return ReadResult(count);
        }

        var length = Math.Min(Math.Min(count, _maxChunk), _data.Length - _position);
        Array.Copy(_data, _position, buffer, offset, length);
        _position += length;
        return length;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Tokens.Add(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Read(buffer, offset, count));
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeCount++;
            Log?.Add("stream");
        }

        base.Dispose(disposing);
    }
}
