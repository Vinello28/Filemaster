namespace Filemaster.Domain;

/// <summary>
/// La richiesta e' in conflitto con lo stato corrente (409 <c>conflict</c>): un codice o un'email gia' in uso, una
/// cartella non vuota, l'ultimo amministratore. Il 409 e' ambiguo per costruzione: un documento senza file ha lo
/// stesso status ma e' <see cref="ContentUnavailableException"/>. Ritentare la stessa richiesta non serve.
/// </summary>
public sealed class ConflictException : FilemasterException
{
    private const string DefaultMessage =
        "La richiesta e' in conflitto con lo stato corrente della risorsa (per esempio un codice gia' in uso o una cartella non vuota).";

    /// <summary>Crea l'eccezione con lo status 409 di default.</summary>
    /// <param name="message">Il messaggio; null per quello di default.</param>
    /// <param name="statusCode">Lo status HTTP della risposta.</param>
    /// <param name="problemType">Lo slug del problem+json, senza <c>/problems/</c>.</param>
    /// <param name="requestId">L'id di correlazione con i log del server.</param>
    /// <param name="detail">Il campo <c>detail</c> del problem+json.</param>
    /// <param name="innerException">La causa, se c'e'.</param>
    public ConflictException(
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
