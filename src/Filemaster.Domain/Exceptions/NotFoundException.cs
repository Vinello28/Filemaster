namespace Filemaster.Domain;

/// <summary>
/// La risorsa non esiste o non e' visibile a questa chiave (404). Il server risponde cosi' anche a un id
/// malformato o di un altro ente, per non rivelare che l'id esiste.
/// </summary>
public sealed class NotFoundException : FilemasterException
{
    private const string DefaultMessage =
        "La risorsa richiesta non esiste o non e' accessibile con questa chiave (vale anche per un id malformato o di un altro ente).";

    /// <summary>Crea l'eccezione con lo status 404 di default.</summary>
    /// <param name="message">Il messaggio; null per quello di default.</param>
    /// <param name="statusCode">Lo status HTTP della risposta.</param>
    /// <param name="problemType">Lo slug del problem+json, senza <c>/problems/</c>.</param>
    /// <param name="requestId">L'id di correlazione con i log del server.</param>
    /// <param name="detail">Il campo <c>detail</c> del problem+json.</param>
    /// <param name="innerException">La causa, se c'e'.</param>
    public NotFoundException(
        string? message = null,
        int statusCode = 404,
        string? problemType = null,
        string? requestId = null,
        string? detail = null,
        Exception? innerException = null)
        : base(message ?? DefaultMessage, statusCode, problemType, requestId, detail, innerException)
    {
    }
}
