using Filemaster.Domain;

namespace Filemaster.Infrastructure;

/// <summary>
/// L'ente sul filo: <c>{"id","slug","name","status","created_at"}</c> (<c>GET /tenant</c>). Lo stato e' un testo del server
/// (<c>active</c>, <c>suspended</c>): un valore che il client non conosce diventa <see cref="TenantStatus.Unknown"/>, non un errore.
/// </summary>
internal static class TenantWire
{
    /// <summary>Legge l'ente.</summary>
    /// <param name="body">I byte del corpo.</param>
    /// <param name="context">Lo status e l'id di correlazione della risposta.</param>
    /// <exception cref="UnexpectedResponseException">Il corpo non ha la forma di un ente, o un id non e' valido.</exception>
    internal static Tenant ReadTenant(byte[]? body, WireContext context) =>
        WireJson.ReadObject(
            body,
            context,
            "ente",
            tenant => new Tenant(
                tenant.RequiredId<TenantId>("id", TenantId.TryParse, "un id di ente"),
                tenant.RequiredString("slug"),
                tenant.RequiredString("name"),
                StatusOf(tenant.OptionalString("status")),
                tenant.RequiredDate("created_at")));

    // Confronto ordinale: "Active" non e' "active". Assente o non riconosciuto e' Unknown (e' il default dell'enum).
    private static TenantStatus StatusOf(string? status) =>
        status switch
        {
            "active" => TenantStatus.Active,
            "suspended" => TenantStatus.Suspended,
            _ => TenantStatus.Unknown,
        };
}
