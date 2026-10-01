namespace Filemaster.Domain;

/// <summary>
/// La chiave API manca o non e' valida (401 <c>unauthorized</c>): assente, sbagliata o revocata. Non e' un problema
/// di permessi (<see cref="ForbiddenException"/>): con questa chiave il server non sa chi sei.
/// </summary>
public sealed class UnauthorizedException : FilemasterException
{
    private const string DefaultMessage = "Chiave API assente o non valida.";

    /// <summary>Crea l'eccezione con lo status 401 di default.</summary>
    /// <param name="message">Il messaggio; null per quello di default.</param>
    /// <param name="statusCode">Lo status HTTP della risposta.</param>
    /// <param name="problemType">Lo slug del problem+json, senza <c>/problems/</c>.</param>
    /// <param name="requestId">L'id di correlazione con i log del server.</param>
    /// <param name="detail">Il campo <c>detail</c> del problem+json (assente con una chiave sbagliata).</param>
    /// <param name="innerException">La causa, se c'e'.</param>
    public UnauthorizedException(
        string? message = null,
        int statusCode = 401,
        string? problemType = null,
        string? requestId = null,
        string? detail = null,
        Exception? innerException = null)
        : base(message ?? DefaultMessage, statusCode, problemType, requestId, detail, innerException)
    {
    }
}
