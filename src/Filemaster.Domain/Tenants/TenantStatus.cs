namespace Filemaster.Domain;

/// <summary>
/// Stato di un ente (<see cref="Tenant.Status"/>). Un ente non si cancella, si sospende. Un valore che questa versione
/// del client non conosce (il server ne ha aggiunto uno) diventa <see cref="Unknown"/>: non e' mai un errore.
/// </summary>
public enum TenantStatus
{
    /// <summary>Valore non riconosciuto o assente. E' anche il <c>default</c> dell'enum.</summary>
    Unknown = 0,

    /// <summary>L'ente e' operativo.</summary>
    Active = 1,

    /// <summary>
    /// L'ente e' sospeso e le sue chiavi API non sono operative: con la chiave di un ente sospeso il server risponde 403 a
    /// ogni richiesta, quindi chi legge l'ente con una chiave vede in pratica solo <see cref="Active"/>.
    /// </summary>
    Suspended = 2,
}
