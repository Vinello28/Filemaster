namespace Filemaster.Domain;

/// <summary>
/// La chiave e' valida ma non puo' fare questa operazione (403 <c>forbidden</c>): lo scope e' insufficiente (per
/// esempio una chiave di sola lettura che scrive) oppure l'ente e' sospeso. Il motivo e' in
/// <see cref="FilemasterException.Detail"/>.
/// </summary>
public sealed class ForbiddenException : FilemasterException
{
    private const string DefaultMessage = "La chiave API non ha il permesso per questa operazione, oppure l'ente e' sospeso.";

    /// <summary>Crea l'eccezione con lo status 403 di default.</summary>
    /// <param name="message">Il messaggio; null per quello di default.</param>
    /// <param name="statusCode">Lo status HTTP della risposta.</param>
    /// <param name="problemType">Lo slug del problem+json, senza <c>/problems/</c>.</param>
    /// <param name="requestId">L'id di correlazione con i log del server.</param>
    /// <param name="detail">Il campo <c>detail</c> del problem+json.</param>
    /// <param name="innerException">La causa, se c'e'.</param>
    public ForbiddenException(
        string? message = null,
        int statusCode = 403,
        string? problemType = null,
        string? requestId = null,
        string? detail = null,
        Exception? innerException = null)
        : base(message ?? DefaultMessage, statusCode, problemType, requestId, detail, innerException)
    {
    }
}
