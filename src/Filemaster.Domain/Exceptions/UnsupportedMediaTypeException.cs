namespace Filemaster.Domain;

/// <summary>
/// Il server non supporta il tipo di contenuto per questa operazione (415): l'anteprima di un documento che non e'
/// un PDF (<c>unsupported-media-type</c>), ma anche un corpo JSON inviato con un Content-Type sbagliato o mancante
/// (slug <c>error</c> o <c>validation-error</c>, sempre con status 415).
/// </summary>
public sealed class UnsupportedMediaTypeException : FilemasterException
{
    private const string DefaultMessage = "Il server non supporta il tipo di contenuto per questa operazione.";

    /// <summary>Crea l'eccezione con lo status 415 di default.</summary>
    /// <param name="message">Il messaggio; null per quello di default.</param>
    /// <param name="statusCode">Lo status HTTP della risposta.</param>
    /// <param name="problemType">Lo slug del problem+json, senza <c>/problems/</c>.</param>
    /// <param name="requestId">L'id di correlazione con i log del server.</param>
    /// <param name="detail">Il campo <c>detail</c> del problem+json (spesso assente).</param>
    /// <param name="innerException">La causa, se c'e'.</param>
    public UnsupportedMediaTypeException(
        string? message = null,
        int statusCode = 415,
        string? problemType = null,
        string? requestId = null,
        string? detail = null,
        Exception? innerException = null)
        : base(message ?? DefaultMessage, statusCode, problemType, requestId, detail, innerException)
    {
    }
}
