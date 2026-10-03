using Filemaster.Domain;
using Filemaster.UnitTests.Infrastructure.Documents;
using Filemaster.UnitTests.Wire;

namespace Filemaster.UnitTests.Infrastructure.Resources;

/// <summary>
/// L'adapter dell'ente: <c>GET /tenant</c> con la chiave API (cattura 03), lettura della risposta, e i due esiti tipici della sonda di
/// autenticazione (401 chiave sbagliata, 403 ente sospeso), che non si ritentano.
/// </summary>
public sealed class HttpTenantInfoTests
{
    [Fact]
    public async Task Get_is_a_GET_of_tenant_with_the_key_as_in_capture_03_and_reads_the_tenant()
    {
        using var rig = new ResourceRig();
        rig.Then(() => FixtureReply.Json("03-tenant"));

        var tenant = await rig.Tenant.GetAsync();

        var sent = rig.Single;
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal("https://filemaster.example.test/tenant", sent.Uri.AbsoluteUri);
        Assert.Equal(WireFixtures.RequestPath("03-tenant"), sent.PathAndQuery);
        Assert.Equal("application/json", sent.Header("Accept"));
        Assert.Equal(TransportRig.Key, sent.Header("X-API-Key"));
        Assert.Null(sent.Body);
        Assert.Equal("ten_01M3VCWS5PAKMSC47JNJ1PTFJQ", tenant.Id.Value);
        Assert.Equal("acme-test", tenant.Slug);
        Assert.Equal("Acme Test", tenant.Name);
        Assert.Equal(TenantStatus.Active, tenant.Status);
        Assert.Equal(WireTest.Utc(2026, 10, 1, 9, 59, 9, 5013410), tenant.CreatedAt);
    }

    [Fact]
    public async Task A_wrong_or_revoked_key_is_UnauthorizedException_and_is_not_retried()
    {
        using var rig = new ResourceRig();
        rig.Then(() => Reply.Problem(401, "unauthorized"));
        rig.Then(() => FixtureReply.Json("03-tenant"));

        var exception = await Assert.ThrowsAsync<UnauthorizedException>(() => rig.Tenant.GetAsync());

        Assert.Equal(401, exception.StatusCode);
        Assert.Single(rig.Sent);
    }

    [Fact]
    public async Task A_suspended_tenant_is_ForbiddenException_with_the_reason()
    {
        using var rig = new ResourceRig();
        rig.Then(() => Reply.Problem(403, "forbidden", "questo ente e' sospeso"));

        var exception = await Assert.ThrowsAsync<ForbiddenException>(() => rig.Tenant.GetAsync());

        Assert.Equal("questo ente e' sospeso", exception.Detail);
        Assert.Single(rig.Sent);
    }

    [Fact]
    public async Task An_unknown_status_is_Unknown_not_an_error()
    {
        using var rig = new ResourceRig();
        rig.Then(() => Reply.Bytes(200, Variants.With(WireFixtures.Captured("03-tenant"), "status", "\"archived\""), null, "application/json"));

        var tenant = await rig.Tenant.GetAsync();

        Assert.Equal(TenantStatus.Unknown, tenant.Status);
    }

    [Fact]
    public async Task An_unreadable_tenant_is_UnexpectedResponseException_with_the_real_status()
    {
        using var rig = new ResourceRig();
        rig.Then(() => Reply.Bytes(200, Variants.Without(WireFixtures.Captured("03-tenant"), "id"), null, "application/json"));

        var exception = await Assert.ThrowsAsync<UnexpectedResponseException>(() => rig.Tenant.GetAsync());

        Assert.Equal(200, exception.StatusCode);
    }
}
