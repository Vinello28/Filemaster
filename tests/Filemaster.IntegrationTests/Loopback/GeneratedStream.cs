namespace Filemaster.IntegrationTests.Loopback;

/// <summary>
/// Uno stream di sola lettura di lunghezza data, generato al volo (nessun array grande): serve agli upload grandi. Puo' essere
/// riposizionabile o no (per <c>Content-Length</c> o <c>chunked</c>) e dice se qualcuno l'ha chiuso.
/// </summary>
internal sealed class GeneratedStream : Stream
{
    private readonly long _length;
    private readonly bool _seekable;
    private long _position;

    internal GeneratedStream(long length, bool seekable)
    {
        _length = length;
        _seekable = seekable;
    }

    internal bool Disposed { get; private set; }

    public override bool CanRead => !Disposed;

    public override bool CanSeek => _seekable && !Disposed;

    public override bool CanWrite => false;

    public override long Length => _seekable ? _length : throw new NotSupportedException();

    public override long Position
    {
        get => _seekable ? _position : throw new NotSupportedException();
        set => _position = _seekable ? value : throw new NotSupportedException();
    }

    /// <summary>Quanti byte sono stati letti finora (anche se lo stream non e' riposizionabile).</summary>
    internal long Consumed => _position;

    /// <summary>Il byte in posizione <paramref name="index"/>: un motivo che non si ripete ogni 256 byte (un errore di offset si vede).</summary>
    internal static byte ByteAt(long index) => unchecked((byte)((index * 31) + (index >> 9)));

    public override int Read(byte[] buffer, int offset, int count)
    {
#if NET
        ObjectDisposedException.ThrowIf(Disposed, this);
#else
        if (Disposed)
        {
            throw new ObjectDisposedException(nameof(GeneratedStream));
        }
#endif

        var n = (int)Math.Min(count, _length - _position);
        for (var i = 0; i < n; i++)
        {
            buffer[offset + i] = ByteAt(_position + i);
        }

        _position += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin) =>
        _seekable
            ? _position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                _ => _length + offset,
            }
            : throw new NotSupportedException();

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}
