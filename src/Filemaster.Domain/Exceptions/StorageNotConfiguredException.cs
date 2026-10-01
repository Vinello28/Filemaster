namespace Filemaster.Domain;

/// <summary>
/// Il server non ha la password dell'archivio impostata (503 <c>storage-not-configured</c>): i documenti si salvano
/// cifrati, quindi finche' un amministratore di piattaforma non la imposta nessun upload riesce. E' un problema di
/// configurazione del server, non transitorio: ritentare non serve.
/// </summary>
public sealed class StorageNotConfiguredException : FilemasterException
{
    private const string DefaultMessage =
        "Il server non ha la password dell'archivio impostata: finche' un amministratore di piattaforma non la imposta i documenti non si possono salvare.";

    /// <summary>Crea l'eccezione con lo status 503 di default.</summary>
    /// <param name="message">Il messaggio; null per quello di default.</param>
    /// <param name="statusCode">Lo status HTTP della risposta.</param>
    /// <param name="problemType">Lo slug del problem+json, senza <c>/problems/</c>.</param>
    /// <param name="requestId">L'id di correlazione con i log del server.</param>
    /// <param name="detail">Il campo <c>detail</c> del problem+json.</param>
    /// <param name="innerException">La causa, se c'e'.</param>
    public StorageNotConfiguredException(
        string? message = null,
        int statusCode = 503,
        string? problemType = null,
        string? requestId = null,
        string? detail = null,
        Exception? innerException = null)
        : base(message ?? DefaultMessage, statusCode, problemType, requestId, detail, innerException)
    {
    }
}
