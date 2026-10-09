using Filemaster.Domain;
using Filemaster.Infrastructure;

namespace Filemaster.UnitTests.Wire;

/// <summary>
/// L'ente letto dalla risposta catturata di <c>GET /tenant</c> (fixture 03, con slug e nome scrubbati) e le sue varianti: lo stato e' un testo
/// con confronto ordinale e <see cref="TenantStatus.Unknown"/> per un valore nuovo; l'id deve essere un id di ente valido.
/// </summary>
public sealed class TenantWireTests
{
    private static Tenant Read(byte[] body) => TenantWire.ReadTenant(body, WireTest.Context());

    private static byte[] Captured() => WireFixtures.Captured("03-tenant");

    [Fact]
    public void The_captured_tenant_is_read_field_by_field()
    {
        // 03-tenant: {"id":1,"slug":"acme-test","name":"Acme Test","status":"active","created_at":"2026-10-09T11:00:45.098964Z"}
        var tenant = Read(Captured());

        Assert.Equal(new TenantId("1"), tenant.Id);
        Assert.Equal("acme-test", tenant.Slug);
        Assert.Equal("Acme Test", tenant.Name);
        Assert.Equal(TenantStatus.Active, tenant.Status);
        Assert.Equal(WireTest.Utc(2026, 10, 9, 11, 0, 45, 989640), tenant.CreatedAt);
    }

    [Fact]
    public void Suspended_is_read_as_suspended()
    {
        Assert.Equal(TenantStatus.Suspended, Read(Variants.With(Captured(), "status", "\"suspended\"")).Status);
    }

    [Theory]
    [InlineData("\"frozen\"")]
    [InlineData("\"Active\"")]
    [InlineData("\"ACTIVE\"")]
    [InlineData("\"suspended \"")]
    [InlineData("\" active\"")]
    [InlineData("\"\"")]
    [InlineData("null")]
    public void A_status_that_the_client_does_not_know_is_unknown_and_never_an_error(string rawJson)
    {
        Assert.Equal(TenantStatus.Unknown, Read(Variants.With(Captured(), "status", rawJson)).Status);
    }

    [Fact]
    public void A_missing_status_is_unknown()
    {
        Assert.Equal(TenantStatus.Unknown, Read(Variants.Without(Captured(), "status")).Status);
    }

    [Theory]
    [InlineData("5")]
    [InlineData("true")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void A_status_of_the_wrong_type_is_not_interpretable(string rawJson)
    {
        WireTest.Unexpected(() => Read(Variants.With(Captured(), "status", rawJson)));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("slug")]
    [InlineData("name")]
    [InlineData("created_at")]
    public void A_missing_required_field_is_not_interpretable(string field)
    {
        var exception = WireTest.Unexpected(() => Read(Variants.Without(Captured(), field)));

        Assert.Contains(field, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("id", "\"5\"")]
    [InlineData("id", "true")]
    [InlineData("id", "[]")]
    [InlineData("slug", "5")]
    [InlineData("name", "[]")]
    [InlineData("created_at", "true")]
    public void A_field_of_the_wrong_type_is_not_interpretable(string field, string rawJson)
    {
        WireTest.Unexpected(() => Read(Variants.With(Captured(), field, rawJson)));
    }

    [Theory]
    [InlineData("\"1\"")] // l'id e' un numero JSON: lo stesso valore scritto come testo non e' valido
    [InlineData("\"\"")]
    [InlineData("\"ten_01M3VCWS5PAKMSC47JNJ1PTFJQ\"")] // il vecchio formato con prefisso e ULID
    [InlineData("1.5")]
    [InlineData("1.0")]
    [InlineData("1e0")]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("2147483648")] // oltre int.MaxValue: l'id di un ente e' un int
    [InlineData("99999999999999999999")]
    public void An_invalid_tenant_id_is_not_interpretable(string rawJson)
    {
        var exception = WireTest.Unexpected(() => Read(Variants.With(Captured(), "id", rawJson)));

        Assert.Contains("ente.id", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_largest_tenant_id_is_int_MaxValue()
    {
        Assert.Equal(new TenantId("2147483647"), Read(Variants.With(Captured(), "id", "2147483647")).Id);
    }

    [Fact]
    public void An_unknown_extra_property_is_ignored_and_empty_texts_are_valid()
    {
        var body = Variants.With(Variants.With(Captured(), "scope", "\"read\""), "slug", "\"\"");

        var tenant = Read(body);

        Assert.Equal(string.Empty, tenant.Slug);
        Assert.Equal("Acme Test", tenant.Name);
    }

    [Fact]
    public void A_date_without_an_offset_is_not_interpretable()
    {
        WireTest.Unexpected(() => Read(Variants.With(Captured(), "created_at", "\"2026-10-01T09:59:09.501341\"")));
    }
}
