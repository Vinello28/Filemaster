using System.Globalization;

namespace Filemaster.Domain;

/// <summary>
/// Il contenuto scaricato non e' quello atteso: il flusso si e' interrotto prima della fine (download troncato,
/// <see cref="IsTruncated"/> vero) oppure e' arrivato tutto ma l'hash SHA-256 non corrisponde
/// (<see cref="IsTruncated"/> falso). La risposta HTTP era riuscita: <see cref="FilemasterException.StatusCode"/> e' quello del download (200 o 206), non
/// un errore del server.
/// </summary>
public sealed class ContentIntegrityException : FilemasterException
{
    /// <summary>Crea l'eccezione.</summary>
    /// <param name="isTruncated">Vero se il flusso e' finito prima del previsto, falso se l'hash e' diverso.</param>
    /// <param name="statusCode">Lo status del download, 200 o 206.</param>
    /// <param name="message">Il messaggio; null per quello di default (che riporta le lunghezze note).</param>
    /// <param name="expectedLength">I byte attesi (Content-Length o dimensione del documento), se noti.</param>
    /// <param name="actualLength">I byte effettivamente ricevuti, se noti.</param>
    /// <param name="requestId">L'id di correlazione con i log del server (l'header <c>X-Request-ID</c> del download).</param>
    /// <param name="innerException">La causa, per esempio l'errore di rete che ha interrotto il flusso.</param>
    public ContentIntegrityException(
        bool isTruncated,
        int statusCode,
        string? message = null,
        long? expectedLength = null,
        long? actualLength = null,
        string? requestId = null,
        Exception? innerException = null)
        : base(message ?? DefaultMessage(isTruncated, expectedLength, actualLength), statusCode, null, requestId, null, innerException)
    {
        IsTruncated = isTruncated;
        ExpectedLength = expectedLength;
        ActualLength = actualLength;
    }

    /// <summary>
    /// Vero se il download si e' interrotto prima della fine (<see cref="ActualLength"/> minore di
    /// <see cref="ExpectedLength"/>); falso se i byte sono arrivati tutti ma l'hash SHA-256 e' diverso.
    /// </summary>
    public bool IsTruncated { get; }

    /// <summary>I byte attesi (Content-Length o dimensione del documento); null se non noti.</summary>
    public long? ExpectedLength { get; }

    /// <summary>I byte effettivamente ricevuti; null se non noti.</summary>
    public long? ActualLength { get; }

    private static string DefaultMessage(bool isTruncated, long? expectedLength, long? actualLength)
    {
        if (!isTruncated)
        {
            return "Il contenuto scaricato non corrisponde all'hash SHA-256 atteso: il file e' stato alterato o corrotto.";
        }

        return expectedLength is { } expected && actualLength is { } actual
            ? string.Format(CultureInfo.InvariantCulture, "Download troncato: ricevuti {0} byte su {1} attesi.", actual, expected)
            : "Download troncato: il flusso e' terminato prima della fine del contenuto.";
    }
}
