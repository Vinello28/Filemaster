using System.Text.Json;
using Filemaster.Domain;

namespace Filemaster.UnitTests.Domain;

/// <summary>
/// Gli eventi webhook sono i tipi di una busta comune (<c>delivery_id</c> e <c>occurred_at</c>, le uniche informazioni
/// comuni che il server manda: nessun tenant, nessun altro id) con un payload per tipo. Il parser e il verificatore
/// della firma stanno in Application; qui solo la forma.
/// </summary>
public sealed class WebhookEventTests
{
    private const string DeliveryId = "5000000007"; // numero (bigint) sul server attuale, tenuto come stringa: chiave di idempotenza
    private const string Sha = "cc1ba284a9fe9cefa40d4bd9dfb8d9e7fb395431aaf79478efca4e04da6c9d7e";

    private static readonly DateTimeOffset Occurred = new(2026, 10, 1, 9, 59, 15, 205, TimeSpan.Zero);
    private static readonly DocumentId DocId = new("5000000042");

    [Fact]
    public void WebhookEvent_is_an_abstract_record_with_exactly_four_sealed_derived_types()
    {
        var root = typeof(WebhookEvent);

        Assert.True(root.IsPublic);
        Assert.True(root.IsAbstract);
        Assert.Equal(
            new[]
            {
                nameof(DocumentDeletedEvent),
                nameof(DocumentIntegrityFailedEvent),
                nameof(DocumentUploadedEvent),
                nameof(UnknownWebhookEvent),
            },
            root.Assembly.GetExportedTypes()
                .Where(t => t != root && root.IsAssignableFrom(t))
                .Select(t =>
                {
                    Assert.True(t.IsSealed, $"{t.Name} deve essere sealed");
                    return t.Name;
                })
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray());
    }

    [Fact]
    public void Known_events_carry_their_payload_and_the_common_envelope_through_the_base_type()
    {
        WebhookEvent[] events =
        {
            new DocumentUploadedEvent(DeliveryId, Occurred, DocId, "fattura.pdf", Sha, true),
            new DocumentDeletedEvent(DeliveryId, Occurred, DocId, Sha),
            new DocumentIntegrityFailedEvent(DeliveryId, Occurred, DocId, Sha, "hash mismatch"),
        };

        Assert.All(events, e =>
        {
            Assert.Equal(DeliveryId, e.DeliveryId);
            Assert.Equal(Occurred, e.OccurredAt);
        });

        var uploaded = Assert.IsType<DocumentUploadedEvent>(events[0]);
        Assert.Equal(DocId, uploaded.DocumentId);
        Assert.Equal("fattura.pdf", uploaded.OriginalFilename);
        Assert.Equal(Sha, uploaded.Sha256);
        Assert.True(uploaded.Deduplicated);

        var deleted = Assert.IsType<DocumentDeletedEvent>(events[1]);
        Assert.Equal(DocId, deleted.DocumentId);
        Assert.Equal(Sha, deleted.Sha256);

        var failed = Assert.IsType<DocumentIntegrityFailedEvent>(events[2]);
        Assert.Equal(DocId, failed.DocumentId);
        Assert.Equal(Sha, failed.Sha256);
        Assert.Equal("hash mismatch", failed.Detail);
    }

    [Fact]
    public void DocumentDeletedEvent_for_a_document_without_content_has_a_null_sha256()
    {
        // Un documento importato con i soli metadati si puo' cancellare: il server manda "sha256": null nel payload
        // (le DTO delle API omettono i null, il webhook no).
        var deleted = new DocumentDeletedEvent(DeliveryId, Occurred, DocId, null);

        Assert.Null(deleted.Sha256);
    }

    [Fact]
    public void Events_of_different_types_with_the_same_envelope_are_not_equal()
    {
        WebhookEvent deleted = new DocumentDeletedEvent(DeliveryId, Occurred, DocId, Sha);
        WebhookEvent failed = new DocumentIntegrityFailedEvent(DeliveryId, Occurred, DocId, Sha, null);

        Assert.NotEqual(deleted, failed);
        Assert.Equal(deleted, new DocumentDeletedEvent(DeliveryId, Occurred, DocId, Sha));
    }

    [Fact]
    public void UnknownWebhookEvent_keeps_the_raw_event_type_and_the_payload()
    {
        using var payload = JsonDocument.Parse("""{"document_id":"x","extra":[1,2,3],"nested":{"a":null}}""");

        WebhookEvent e = new UnknownWebhookEvent(DeliveryId, Occurred, "document.renamed", payload.RootElement);

        var unknown = Assert.IsType<UnknownWebhookEvent>(e);
        Assert.Equal(DeliveryId, unknown.DeliveryId);
        Assert.Equal(Occurred, unknown.OccurredAt);
        Assert.Equal("document.renamed", unknown.EventType); // la stringa del server, mai interpretata
        Assert.Equal(JsonValueKind.Object, unknown.Payload.ValueKind);
        Assert.Equal(3, unknown.Payload.GetProperty("extra").GetArrayLength());
        Assert.Equal("x", unknown.Payload.GetProperty("document_id").GetString());
    }

    [Fact]
    public void UnknownWebhookEvent_accepts_any_type_and_payload_without_throwing()
    {
        // Nessuna validazione: e' il tipo che riceve cio' che il client non sa interpretare, qualunque cosa sia.
        using var array = JsonDocument.Parse("[1,2]");

        var empty = new UnknownWebhookEvent(DeliveryId, Occurred, string.Empty, default);
        var odd = new UnknownWebhookEvent(DeliveryId, Occurred, "  Document.Uploaded\n", array.RootElement);

        Assert.Equal(string.Empty, empty.EventType);
        Assert.Equal(JsonValueKind.Undefined, empty.Payload.ValueKind);
        Assert.Equal("  Document.Uploaded\n", odd.EventType); // nessun trim, nessun cambio di maiuscole
        Assert.Equal(JsonValueKind.Array, odd.Payload.ValueKind);
    }

    [Fact]
    public void UnknownWebhookEvent_equality_compares_the_payload_by_element_identity_not_by_content()
    {
        using var first = JsonDocument.Parse("""{"a":1}""");
        using var second = JsonDocument.Parse("""{"a":1}""");

        var a = new UnknownWebhookEvent(DeliveryId, Occurred, "x.y", first.RootElement);

        Assert.Equal(a, new UnknownWebhookEvent(DeliveryId, Occurred, "x.y", first.RootElement));
        Assert.NotEqual(a, new UnknownWebhookEvent(DeliveryId, Occurred, "x.y", second.RootElement));
        Assert.NotEqual(a, new UnknownWebhookEvent(DeliveryId, Occurred, "x.z", first.RootElement));
    }
}
