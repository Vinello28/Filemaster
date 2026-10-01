namespace Filemaster.Application;

/// <summary>
/// Quale parte del contenuto e' arrivata in una risposta 206: l'header <c>Content-Range</c> del server (per esempio
/// <c>bytes 0-9/590</c> e' <c>FirstByte</c> 0, <c>LastByte</c> 9, <c>TotalLength</c> 590). A differenza di
/// <see cref="ByteRange"/> (la richiesta, con forme aperte) e' sempre un intervallo risolto, con tutti gli estremi.
/// </summary>
public sealed record ContentRange(long FirstByte, long LastByte, long TotalLength)
{
    /// <summary>L'offset del primo byte arrivato, da zero.</summary>
    public long FirstByte { get; } = FirstByte;

    /// <summary>L'offset dell'ultimo byte arrivato, incluso.</summary>
    public long LastByte { get; } = LastByte;

    /// <summary>La dimensione totale del contenuto del documento, in byte (non quella della parte arrivata).</summary>
    public long TotalLength { get; } = TotalLength;
}
