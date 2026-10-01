namespace Filemaster.Domain;

/// <summary>
/// Errore del server (500 <c>internal-error</c> e ogni altro 5xx, compresi i 502/503/504 di proxy e bilanciatori che
/// non sono problem+json). Il <see cref="FilemasterException.RequestId"/> identifica l'evento nei log del server.
/// </summary>
public sealed class ServerErrorException : FilemasterException
{
    private const string DefaultMessage = "Il server ha risposto con un errore interno.";

    /// <summary>Crea l'eccezione con lo status 500 di default.</summary>
    /// <param name="message">Il messaggio; null per quello di default.</param>
    /// <param name="statusCode">Lo status HTTP della risposta (un 5xx).</param>
    /// <param name="problemType">Lo slug del problem+json, senza <c>/problems/</c>.</param>
    /// <param name="requestId">L'id di correlazione con i log del server.</param>
    /// <param name="detail">Il campo <c>detail</c> del problem+json.</param>
    /// <param name="innerException">La causa, se c'e'.</param>
    public ServerErrorException(
        string? message = null,
        int statusCode = 500,
        string? problemType = null,
        string? requestId = null,
        string? detail = null,
        Exception? innerException = null)
        : base(message ?? DefaultMessage, statusCode, problemType, requestId, detail, innerException)
    {
    }
}
