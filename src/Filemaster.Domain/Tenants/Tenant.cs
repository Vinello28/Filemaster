namespace Filemaster.Domain;

/// <summary>
/// L'ente (tenant) a cui appartiene la chiave API: il confine di isolamento di documenti, cartelle, contatti, utenti e
/// chiavi. E' quello che restituisce la lettura dell'ente, e serve anche da sonda di connettivita' e di autenticazione.
/// Il record non dice nulla sullo scope della chiave: il server non lo espone qui.
/// </summary>
public sealed record Tenant(TenantId Id, string Slug, string Name, TenantStatus Status, DateTimeOffset CreatedAt)
{
    /// <summary>L'id dell'ente.</summary>
    public TenantId Id { get; } = Id;

    /// <summary>Il nome breve dell'ente, leggibile e unico sulla piattaforma.</summary>
    public string Slug { get; } = Slug;

    /// <summary>Il nome dell'ente.</summary>
    public string Name { get; } = Name;

    /// <summary>Lo stato dell'ente; <see cref="TenantStatus.Unknown"/> per un valore che il client non conosce.</summary>
    public TenantStatus Status { get; } = Status;

    /// <summary>Quando l'ente e' stato creato (UTC).</summary>
    public DateTimeOffset CreatedAt { get; } = CreatedAt;
}
