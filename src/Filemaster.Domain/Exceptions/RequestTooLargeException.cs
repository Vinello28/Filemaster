namespace Filemaster.Domain;

/// <summary>
/// Il contenuto o la richiesta superano il limite del server (413 <c>request-too-large</c>). Due percorsi: il limite
/// dello store per un singolo file ("contenuto troppo grande") e il limite del server web sul corpo ("richiesta
/// troppo grande"), che chiude la connessione e puo' quindi arrivare come <see cref="ConnectionException"/>.
/// Ritentare con lo stesso contenuto non serve.
/// </summary>
public sealed class RequestTooLargeException : FilemasterException
{
    private const string DefaultMessage = "Il contenuto o la richiesta superano la dimensione massima accettata dal server.";

    /// <summary>Crea l'eccezione con lo status 413 di default.</summary>
    /// <param name="message">Il messaggio; null per quello di default.</param>
    /// <param name="statusCode">Lo status HTTP della risposta.</param>
    /// <param name="problemType">Lo slug del problem+json, senza <c>/problems/</c>.</param>
    /// <param name="requestId">L'id di correlazione con i log del server.</param>
    /// <param name="detail">Il campo <c>detail</c> del problem+json.</param>
    /// <param name="innerException">La causa, se c'e'.</param>
    public RequestTooLargeException(
        string? message = null,
        int statusCode = 413,
        string? problemType = null,
        string? requestId = null,
        string? detail = null,
        Exception? innerException = null)
        : base(message ?? DefaultMessage, statusCode, problemType, requestId, detail, innerException)
    {
    }
}
