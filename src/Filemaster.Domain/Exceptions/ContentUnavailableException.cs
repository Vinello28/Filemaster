namespace Filemaster.Domain;

/// <summary>
/// Il documento esiste ma non ha un file (409 <c>content-unavailable</c>): e' stato importato con i soli metadati,
/// quindi contenuto, anteprima e verifica non sono possibili. E' lo stato del documento a impedire l'operazione, non
/// la sua assenza; ha lo stesso status di <see cref="ConflictException"/> e si distingue solo per slug.
/// </summary>
public sealed class ContentUnavailableException : FilemasterException
{
    private const string DefaultMessage =
        "Il documento non ha un file: e' stato importato con i soli metadati, quindi contenuto e anteprima non sono disponibili.";

    /// <summary>Crea l'eccezione con lo status 409 di default.</summary>
    /// <param name="message">Il messaggio; null per quello di default.</param>
    /// <param name="statusCode">Lo status HTTP della risposta.</param>
    /// <param name="problemType">Lo slug del problem+json, senza <c>/problems/</c>.</param>
    /// <param name="requestId">L'id di correlazione con i log del server.</param>
    /// <param name="detail">Il campo <c>detail</c> del problem+json.</param>
    /// <param name="innerException">La causa, se c'e'.</param>
    public ContentUnavailableException(
        string? message = null,
        int statusCode = 409,
        string? problemType = null,
        string? requestId = null,
        string? detail = null,
        Exception? innerException = null)
        : base(message ?? DefaultMessage, statusCode, problemType, requestId, detail, innerException)
    {
    }
}
