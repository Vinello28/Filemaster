using Filemaster.Application;
using Filemaster.Domain;

namespace Filemaster.UnitTests.Wire;

/// <summary>
/// Le tre buste webhook DERIVATE dal codice del server (<c>document.uploaded</c>, <c>document.deleted</c>, <c>document.integrity_failed</c>; nessuna
/// consegna e' stata catturata: serve un ricevitore) lette dal parser dell'Application. Servono come golden file: se il parser o la forma
/// cambiano, si vede qui. La busta e' <c>{event, delivery_id, occurred_at, payload}</c> con <c>delivery_id</c> NUMERO JSON; il payload usa
/// <c>filename</c> (non <c>original_filename</c> come l'API), <b>non omette i null</b> e ha <c>document_id</c> numero in <c>uploaded</c> e
/// <c>integrity_failed</c> ma STRINGA di cifre in <c>deleted</c> (<c>DocumentService.DeleteAsync</c> del server pubblica l'id della rotta, un testo).
/// Vanno sostituite da catture vere appena esiste un ricevitore.
/// </summary>
public sealed class WebhookFixtureTests
{
    private const string Sha = "cc1ba284a9fe9cefa40d4bd9dfb8d9e7fb395431aaf79478efca4e04da6c9d7e";

    [Fact]
    public void The_derived_uploaded_envelope_is_a_DocumentUploadedEvent()
    {
        var parsed = WebhookEventParser.Parse(WireFixtures.Derived("webhook-document-uploaded"));

        var uploaded = Assert.IsType<DocumentUploadedEvent>(parsed);
        Assert.Equal("7001", uploaded.DeliveryId);
        Assert.Equal(WireTest.Utc(2026, 10, 9, 11, 11, 7, 9000000), uploaded.OccurredAt);
        Assert.Equal(new DocumentId("30017"), uploaded.DocumentId);
        Assert.Equal("fattura.pdf", uploaded.OriginalFilename);
        Assert.Equal(Sha, uploaded.Sha256);
        Assert.False(uploaded.Deduplicated);
    }

    [Fact]
    public void The_derived_deleted_envelope_is_a_DocumentDeletedEvent()
    {
        var parsed = WebhookEventParser.Parse(WireFixtures.Derived("webhook-document-deleted"));

        var deleted = Assert.IsType<DocumentDeletedEvent>(parsed);
        Assert.Equal("7002", deleted.DeliveryId);
        Assert.Equal(WireTest.Utc(2026, 10, 9, 11, 12, 0, 2500000), deleted.OccurredAt);
        Assert.Equal(new DocumentId("30018"), deleted.DocumentId);
        Assert.Equal(Sha, deleted.Sha256);
    }

    [Fact]
    public void The_derived_integrity_failed_envelope_is_a_DocumentIntegrityFailedEvent()
    {
        var parsed = WebhookEventParser.Parse(WireFixtures.Derived("webhook-document-integrity-failed"));

        var failed = Assert.IsType<DocumentIntegrityFailedEvent>(parsed);
        Assert.Equal("7003", failed.DeliveryId);
        Assert.Equal(WireTest.Utc(2026, 10, 9, 11, 15, 0), failed.OccurredAt);
        Assert.Equal(new DocumentId("30017"), failed.DocumentId);
        Assert.Equal(Sha, failed.Sha256);
        Assert.Equal("hash diverso: contenuto alterato", failed.Detail);
    }

    [Theory]
    [InlineData("webhook-document-uploaded", "Number", "Number")]
    [InlineData("webhook-document-deleted", "Number", "String")] // la sola busta in cui il server scrive l'id del documento come testo
    [InlineData("webhook-document-integrity-failed", "Number", "Number")]
    public void The_fixtures_have_the_json_types_the_server_writes_for_the_two_ids(string name, string deliveryKind, string documentKind)
    {
        using var document = System.Text.Json.JsonDocument.Parse(WireFixtures.Derived(name));

        Assert.Equal(deliveryKind, document.RootElement.GetProperty("delivery_id").ValueKind.ToString());
        Assert.Equal(documentKind, document.RootElement.GetProperty("payload").GetProperty("document_id").ValueKind.ToString());
    }
}
