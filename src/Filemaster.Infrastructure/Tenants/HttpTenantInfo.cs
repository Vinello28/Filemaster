using Filemaster.Application;
using Filemaster.Domain;

namespace Filemaster.Infrastructure;

/// <summary>
/// L'adapter HTTP di <see cref="ITenantInfo"/>: <c>GET tenant</c> con la chiave API (e' la sonda di autenticazione: un 401 dice che
/// la chiave e' sbagliata o revocata, un 403 che l'ente e' sospeso), letto con <see cref="TenantWire"/>. E' una lettura: il trasporto
/// la ritenta sugli errori transitori, mai su 401 o 403.
/// </summary>
internal sealed class HttpTenantInfo : ITenantInfo
{
    private readonly FilemasterTransport _transport;

    /// <summary>Crea l'adapter sul trasporto indicato (condivisibile con gli altri adapter).</summary>
    /// <param name="transport">Il trasporto.</param>
    /// <exception cref="ArgumentNullException"><paramref name="transport"/> e' null.</exception>
    internal HttpTenantInfo(FilemasterTransport transport)
    {
        Guard.NotNull(transport);
        _transport = transport;
    }

    /// <inheritdoc />
    public async Task<Tenant> GetAsync(CancellationToken cancellationToken = default)
    {
        var response = await JsonCalls.SendAsync(_transport, HttpMethod.Get, Routes.Tenant, body: null, cancellationToken).ConfigureAwait(false);
        return TenantWire.ReadTenant(response.Body, WireContext.Of(response));
    }
}
