namespace Filemaster.Domain;

/// <summary>
/// Il server ha rifiutato la richiesta perche' non valida (400 <c>validation-error</c>): campo mancante o fuori
/// limite, JSON non valido, proprieta' sconosciuta, cursore o filtro malformato. Il motivo e' in
/// <see cref="FilemasterException.Detail"/>. Una violazione che il client sa riconoscere da solo non arriva fin qui:
/// e' un <see cref="ArgumentException"/>. Ritentare la stessa richiesta non serve.
/// </summary>
public sealed class InvalidRequestException : FilemasterException
{
    private const string DefaultMessage = "Il server ha rifiutato la richiesta perche' non valida.";

    /// <summary>Crea l'eccezione con lo status 400 di default.</summary>
    /// <param name="message">Il messaggio; null per quello di default.</param>
    /// <param name="statusCode">Lo status HTTP della risposta.</param>
    /// <param name="problemType">Lo slug del problem+json, senza <c>/problems/</c>.</param>
    /// <param name="requestId">L'id di correlazione con i log del server.</param>
    /// <param name="detail">Il campo <c>detail</c> del problem+json.</param>
    /// <param name="innerException">La causa, se c'e'.</param>
    public InvalidRequestException(
        string? message = null,
        int statusCode = 400,
        string? problemType = null,
        string? requestId = null,
        string? detail = null,
        Exception? innerException = null)
        : base(message ?? DefaultMessage, statusCode, problemType, requestId, detail, innerException)
    {
    }
}
