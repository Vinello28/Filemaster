namespace Filemaster.Domain;

/// <summary>
/// Il server ha risposto, ma la risposta non e' interpretabile: corpo non JSON, campo obbligatorio mancante, valore
/// fuori formato, oppure uno status che il contratto non prevede (405, 416 senza corpo, slug <c>error</c>...).
/// <see cref="FilemasterException.StatusCode"/> e' sempre quello della risposta; l'eccezione di parsing, se c'e', e'
/// in <see cref="Exception.InnerException"/>.
/// </summary>
public sealed class UnexpectedResponseException : FilemasterException
{
    private const string DefaultMessage = "La risposta del server non e' interpretabile.";

    /// <summary>Crea l'eccezione. Lo status e' obbligatorio: una risposta c'e' sempre.</summary>
    /// <param name="message">Il messaggio; null per quello di default.</param>
    /// <param name="statusCode">Lo status HTTP della risposta.</param>
    /// <param name="problemType">Lo slug del problem+json, senza <c>/problems/</c>.</param>
    /// <param name="requestId">L'id di correlazione con i log del server.</param>
    /// <param name="detail">Il campo <c>detail</c> del problem+json.</param>
    /// <param name="innerException">La causa, per esempio l'eccezione del parser JSON.</param>
    public UnexpectedResponseException(
        string? message,
        int statusCode,
        string? problemType = null,
        string? requestId = null,
        string? detail = null,
        Exception? innerException = null)
        : base(message ?? DefaultMessage, statusCode, problemType, requestId, detail, innerException)
    {
    }
}
