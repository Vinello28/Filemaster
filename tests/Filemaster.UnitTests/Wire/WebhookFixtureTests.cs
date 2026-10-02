using Filemaster.Application;
using Filemaster.Domain;

namespace Filemaster.UnitTests.Wire;

/// <summary>
/// Le tre buste webhook DERIVATE dal codice del server (<c>document.uploaded</c>, <c>document.deleted</c>, <c>document.integrity_failed</c>; nessuna
/// consegna e' stata catturata: il server di prova non aveva abbonamenti) lette dal parser dell'Application. Servono come golden file: se il
/// parser o la forma cambiano, si vede qui. La busta e' <c>{event, delivery_id, occurred_at, payload}</c>; il payload usa <c>filename</c> (non
/// <c>original_filename</c> come l'API) e <b>non omette i null</b>. Vanno sostituite da catture vere (T6.3).
/// </summary>
public sealed class WebhookFixtureTests
{
    private const string Sha = "cc1ba284a9fe9cefa40d4bd9dfb8d9e7fb395431aaf79478efca4e04da6c9d7e";

    [Fact]
    public void The_derived_uploaded_envelope_is_a_DocumentUploadedEvent()
    {
        var parsed = WebhookEventParser.Parse(WireFixtures.Derived("webhook-document-uploaded"));

        var uploaded = Assert.IsType<DocumentUploadedEvent>(parsed);
        Assert.Equal("whd_01M3VEF0W1X2Y3Z4A5B6C7D8E9", uploaded.DeliveryId);
        Assert.Equal(WireTest.Utc(2026, 10, 1, 9, 59, 15, 3000000), uploaded.OccurredAt);
        Assert.Equal(new DocumentId("doc_01M3VEESG5KBYR5PYAJ0TDT4B2"), uploaded.DocumentId);
        Assert.Equal("fattura.pdf", uploaded.OriginalFilename);
        Assert.Equal(Sha, uploaded.Sha256);
        Assert.False(uploaded.Deduplicated);
    }

    [Fact]
    public void The_derived_deleted_envelope_is_a_DocumentDeletedEvent()
    {
        var parsed = WebhookEventParser.Parse(WireFixtures.Derived("webhook-document-deleted"));

        var deleted = Assert.IsType<DocumentDeletedEvent>(parsed);
        Assert.Equal("whd_01M3VEF0W1X2Y3Z4A5B6C7D8EA", deleted.DeliveryId);
        Assert.Equal(WireTest.Utc(2026, 10, 1, 10, 5, 0, 2500000), deleted.OccurredAt);
        Assert.Equal(new DocumentId("doc_01M3VEESM5WDTJ38JB384KV56M"), deleted.DocumentId);
        Assert.Equal(Sha, deleted.Sha256);
    }

    [Fact]
    public void The_derived_integrity_failed_envelope_is_a_DocumentIntegrityFailedEvent()
    {
        var parsed = WebhookEventParser.Parse(WireFixtures.Derived("webhook-document-integrity-failed"));

        var failed = Assert.IsType<DocumentIntegrityFailedEvent>(parsed);
        Assert.Equal("whd_01M3VEF0W1X2Y3Z4A5B6C7D8EB", failed.DeliveryId);
        Assert.Equal(WireTest.Utc(2026, 10, 1, 10, 10, 0), failed.OccurredAt);
        Assert.Equal(new DocumentId("doc_01M3VEESG5KBYR5PYAJ0TDT4B2"), failed.DocumentId);
        Assert.Equal(Sha, failed.Sha256);
        Assert.Equal("hash diverso: contenuto alterato", failed.Detail);
    }
}
