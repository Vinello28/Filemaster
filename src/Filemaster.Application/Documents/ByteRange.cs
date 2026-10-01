using System.Globalization;

namespace Filemaster.Application;

/// <summary>
/// Un intervallo di byte da leggere dal contenuto di un documento: l'header <c>Range</c> con una sola parte, in una delle
/// tre forme che il server serve con 206 (<c>bytes=a-b</c>, <c>bytes=a-</c>, <c>bytes=-n</c>). Si crea con
/// <see cref="Between"/>, <see cref="From"/> o <see cref="Suffix"/>; e' immutabile e valido per costruzione.
/// </summary>
/// <remarks>
/// <para>
/// Un intervallo multiplo (<c>bytes=0-1,4-5</c>) non esiste qui: il server lo ignora e risponde 200 con tutto il contenuto,
/// quindi non servirebbe a nulla. Per lo stesso motivo <see cref="Between"/> rifiuta un ultimo byte prima del primo
/// (<c>bytes=5-4</c> e' non valido per il server, che lo ignora e restituisce tutto).
/// </para>
/// <para>
/// Gli offset contano i byte del contenuto del documento, da zero; gli estremi sono inclusi (<c>bytes=0-9</c> sono i primi
/// dieci byte). Un intervallo che parte oltre la fine del contenuto non e' soddisfacibile: il server risponde 416 senza
/// corpo e il client lancia <see cref="Filemaster.Domain.UnexpectedResponseException"/>. Un ultimo byte oltre la fine e' invece
/// tagliato alla fine, senza errore. Il contenuto restituito dice quale parte e' arrivata: <see cref="DocumentContent.Range"/>.
/// </para>
/// </remarks>
public sealed record ByteRange
{
    private ByteRange(long? firstByte, long? lastByte, long? suffixLength)
    {
        FirstByte = firstByte;
        LastByte = lastByte;
        SuffixLength = suffixLength;
    }

    /// <summary>L'offset del primo byte (da zero); null per un intervallo <see cref="Suffix"/>.</summary>
    public long? FirstByte { get; }

    /// <summary>L'offset dell'ultimo byte, incluso; null se l'intervallo arriva fino alla fine del contenuto.</summary>
    public long? LastByte { get; }

    /// <summary>Quanti byte dalla fine del contenuto; null se l'intervallo non e' un <see cref="Suffix"/>.</summary>
    public long? SuffixLength { get; }

    /// <summary>Dal byte <paramref name="first"/> al byte <paramref name="last"/>, entrambi inclusi (<c>bytes=a-b</c>).</summary>
    /// <param name="first">L'offset del primo byte, da zero.</param>
    /// <param name="last">L'offset dell'ultimo byte, non minore di <paramref name="first"/>.</param>
    /// <returns>L'intervallo.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="first"/> o <paramref name="last"/> e' negativo, oppure <paramref name="last"/> e' minore di <paramref name="first"/>.
    /// </exception>
    public static ByteRange Between(long first, long last)
    {
        if (first < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(first), first, "L'offset del primo byte non puo' essere negativo.");
        }

        if (last < first)
        {
            throw new ArgumentOutOfRangeException(
                nameof(last),
                last,
                "L'ultimo byte non puo' essere negativo ne' precedere il primo (il server ignorerebbe l'intervallo e restituirebbe tutto).");
        }

        return new ByteRange(first, last, null);
    }

    /// <summary>Dal byte <paramref name="first"/> fino alla fine del contenuto (<c>bytes=a-</c>).</summary>
    /// <param name="first">L'offset del primo byte, da zero.</param>
    /// <returns>L'intervallo.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="first"/> e' negativo.</exception>
    public static ByteRange From(long first)
    {
        if (first < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(first), first, "L'offset del primo byte non puo' essere negativo.");
        }

        return new ByteRange(first, null, null);
    }

    /// <summary>Gli ultimi <paramref name="length"/> byte del contenuto (<c>bytes=-n</c>); se il contenuto e' piu' corto, tutto.</summary>
    /// <param name="length">Quanti byte dalla fine, almeno uno.</param>
    /// <returns>L'intervallo.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> e' minore di 1 (zero byte non e' soddisfacibile).</exception>
    public static ByteRange Suffix(long length)
    {
        if (length < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "Servono almeno un byte: un suffisso di zero byte non e' soddisfacibile.");
        }

        return new ByteRange(null, null, length);
    }

    /// <summary>Il valore dell'header <c>Range</c>, per esempio <c>bytes=0-9</c>, <c>bytes=5-</c> o <c>bytes=-10</c>.</summary>
    /// <returns>Il valore dell'header, con i numeri in cifre ASCII.</returns>
    public string ToHeaderValue() =>
        SuffixLength is { } suffix
            ? string.Format(CultureInfo.InvariantCulture, "bytes=-{0}", suffix)
            : LastByte is { } last
                ? string.Format(CultureInfo.InvariantCulture, "bytes={0}-{1}", FirstByte, last)
                : string.Format(CultureInfo.InvariantCulture, "bytes={0}-", FirstByte);
}
