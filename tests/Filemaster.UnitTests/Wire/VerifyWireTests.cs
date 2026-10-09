using Filemaster.Domain;
using Filemaster.Infrastructure;

namespace Filemaster.UnitTests.Wire;

/// <summary>
/// Gli esiti delle verifiche d'integrita': l'esito di un documento (fixture 126) e i conteggi di un lotto (fixture 129), con le varianti. Un
/// contenuto alterato e' <c>ok:false</c> con un <c>detail</c>, non un errore: quella forma non e' stata catturata (il server di prova non aveva
/// contenuti alterati) ed e' DERIVATA dal codice del server (<c>VerifyDto</c>, <c>detail</c> omesso quando e' null).
/// </summary>
public sealed class VerifyWireTests
{
    private const string FatturaId = "30017";
    private const string FatturaSha = "cc1ba284a9fe9cefa40d4bd9dfb8d9e7fb395431aaf79478efca4e04da6c9d7e";

    private static IntegrityCheck ReadCheck(byte[] body) => VerifyWire.ReadIntegrityCheck(body, WireTest.Context());

    private static BulkVerifyResult ReadBulk(byte[] body) => VerifyWire.ReadBulkVerify(body, WireTest.Context());

    private static byte[] Check() => WireFixtures.Captured("126-doc-verify");

    private static byte[] Bulk() => WireFixtures.Captured("129-docs-bulk-verify");

    // ----- un documento -----

    [Fact]
    public void The_captured_successful_verification_is_read_field_by_field()
    {
        // 126-doc-verify: {"document_id":30017,"sha256":"cc1b...","ok":true,"checked_at":"2026-10-09T11:11:14.773783Z"}
        var check = ReadCheck(Check());

        Assert.Equal(new DocumentId(FatturaId), check.DocumentId);
        Assert.Equal(FatturaSha, check.Sha256);
        Assert.True(check.Ok);
        Assert.Null(check.Detail);
        Assert.Equal(WireTest.Utc(2026, 10, 9, 11, 11, 14, 7737830), check.CheckedAt);
        Assert.Equal(TimeSpan.Zero, check.CheckedAt.Offset);
    }

    [Fact]
    public void A_failed_verification_is_a_result_with_its_detail_and_not_an_error()
    {
        var body = Variants.Edit(Check(), o =>
        {
            o["ok"] = false;
            o["detail"] = "hash diverso: contenuto alterato";
        });

        var check = ReadCheck(body);

        Assert.False(check.Ok);
        Assert.Equal("hash diverso: contenuto alterato", check.Detail);
        Assert.Equal(FatturaSha, check.Sha256);
    }

    [Fact]
    public void A_null_or_missing_detail_is_null()
    {
        Assert.Null(ReadCheck(Variants.With(Check(), "detail", "null")).Detail);
        Assert.Null(ReadCheck(Variants.Without(Check(), "detail")).Detail);
    }

    [Theory]
    [InlineData("document_id")]
    [InlineData("sha256")]
    [InlineData("ok")]
    [InlineData("checked_at")]
    public void A_missing_required_field_is_not_interpretable_and_names_the_field(string field)
    {
        var exception = WireTest.Unexpected(() => ReadCheck(Variants.Without(Check(), field)));

        Assert.Contains(field, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("document_id", "\"30017\"")] // l'id e' un numero JSON, non un testo di cifre
    [InlineData("document_id", "\"doc_abc\"")]
    [InlineData("document_id", "\"\"")]
    [InlineData("document_id", "1.5")]
    [InlineData("document_id", "-30017")]
    [InlineData("document_id", "0")]
    [InlineData("document_id", "9223372036854775808")]
    [InlineData("document_id", "true")]
    [InlineData("sha256", "\"abc\"")]
    [InlineData("sha256", "5")]
    [InlineData("sha256", "\"CC1BA284A9FE9CEFA40D4BD9DFB8D9E7FB395431AAF79478EFCA4E04DA6C9D7E\"")]
    [InlineData("ok", "\"true\"")]
    [InlineData("ok", "1")]
    [InlineData("ok", "null")]
    [InlineData("detail", "5")]
    [InlineData("detail", "[]")]
    [InlineData("checked_at", "\"2026-10-01T09:59:22.602054\"")]
    [InlineData("checked_at", "1790848762")]
    public void A_field_of_the_wrong_type_or_shape_is_not_interpretable(string field, string rawJson)
    {
        WireTest.Unexpected(() => ReadCheck(Variants.With(Check(), field, rawJson)));
    }

    [Fact]
    public void The_document_id_is_a_json_number_and_a_big_one_is_read_exactly()
    {
        Assert.Equal(30017L, ReadCheck(Check()).DocumentId.Number);
        Assert.Equal(9007199254740993L, ReadCheck(Variants.With(Check(), "document_id", "9007199254740993")).DocumentId.Number);
    }

    [Fact]
    public void An_unknown_extra_property_is_ignored()
    {
        var check = ReadCheck(Variants.With(Check(), "storico_id", "42"));

        Assert.True(check.Ok);
    }

    // ----- un lotto -----

    [Fact]
    public void The_captured_bulk_verification_is_read_as_four_counts()
    {
        // 129-docs-bulk-verify: {"total":2,"verified":2,"failed":0,"without_content":0}
        var result = ReadBulk(Bulk());

        Assert.Equal(2, result.Total);
        Assert.Equal(2, result.Verified);
        Assert.Equal(0, result.Failed);
        Assert.Equal(0, result.WithoutContent);
    }

    [Fact]
    public void The_four_counts_are_read_from_their_own_properties_and_not_swapped()
    {
        var result = ReadBulk(WireTest.Utf8("{\"total\":10,\"verified\":6,\"failed\":3,\"without_content\":1}"));

        Assert.Equal(10, result.Total);
        Assert.Equal(6, result.Verified);
        Assert.Equal(3, result.Failed);
        Assert.Equal(1, result.WithoutContent);
    }

    [Theory]
    [InlineData("total")]
    [InlineData("verified")]
    [InlineData("failed")]
    [InlineData("without_content")]
    public void A_missing_count_is_not_interpretable_and_does_not_become_zero(string field)
    {
        var exception = WireTest.Unexpected(() => ReadBulk(Variants.Without(Bulk(), field)));

        Assert.Contains(field, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("total", "null")]
    [InlineData("total", "-1")]
    [InlineData("verified", "1.5")]
    [InlineData("verified", "\"2\"")]
    [InlineData("failed", "2147483648")]
    [InlineData("failed", "true")]
    [InlineData("without_content", "[]")]
    [InlineData("without_content", "-5")]
    public void A_count_that_is_not_a_non_negative_32_bit_integer_is_not_interpretable(string field, string rawJson)
    {
        WireTest.Unexpected(() => ReadBulk(Variants.With(Bulk(), field, rawJson)));
    }

    [Fact]
    public void A_count_of_int_max_is_valid_and_unknown_properties_are_ignored()
    {
        var result = ReadBulk(Variants.With(Variants.With(Bulk(), "total", "2147483647"), "dettagli", "[]"));

        Assert.Equal(int.MaxValue, result.Total);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("2")]
    [InlineData("\"ok\"")]
    public void A_body_that_is_not_an_object_is_not_interpretable(string body)
    {
        WireTest.Unexpected(() => ReadCheck(WireTest.Utf8(body)));
        WireTest.Unexpected(() => ReadBulk(WireTest.Utf8(body)));
    }
}
