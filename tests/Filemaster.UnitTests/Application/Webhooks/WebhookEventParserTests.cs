using System.Text;
using System.Text.Json;
using Filemaster.Application;
using Filemaster.Domain;
using Microsoft.Extensions.Time.Testing;

namespace Filemaster.UnitTests.Application.Webhooks;

/// <summary>
/// Il parser dei corpi di webhook. <b>Nessun corpo e' stato catturato dal server vero</b>: sono tutti costruiti a mano dalla forma
/// letta nel codice (Sharp-a-File, ramo dev, commit 8aec8bb): la busta e' quella di <c>WebhookDispatcher.BuildBody</c>
/// (<c>{"event","delivery_id","occurred_at","payload"}</c>) e i payload sono gli oggetti anonimi di <c>DocumentService</c>
/// (<c>new { document_id, filename, sha256, deduplicated }</c> per il caricamento, <c>new { document_id, sha256 }</c> per la
/// cancellazione, <c>new { document_id, sha256, detail }</c> per la verifica fallita), serializzati in snake_case senza omettere i
/// null. Tutti i corpi sono quindi "derivati dal codice del server, non catturati". Una consegna vera ha solo ASCII (i non-ASCII
/// e l'apostrofo escono come <c>\u00XX</c>): i casi con UTF-8 grezzo qui sono prove di robustezza.
/// </summary>
public sealed class WebhookEventParserTests
{
    private const string Backslash = "\U0000005C";
    private const string DeliveryId = "whd_01M3VEESG5KBYR5PYAJ0TDT4B2";
    private const string DocumentIdText = "doc_01M3VEESG5KBYR5PYAJ0TDT4B2";
    private const string Sha = "cc1ba284a9fe9cefa40d4bd9dfb8d9e7fb395431aaf79478efca4e04da6c9d7e";
    private const string OccurredAtText = "2026-10-01T09:59:15.205Z";

    private static readonly DateTimeOffset OccurredAt = new(2026, 10, 1, 9, 59, 15, 205, TimeSpan.Zero);
    private static readonly DocumentId DocId = new(DocumentIdText);

    // ------------------------------------------------------------------------------------------ aiuti

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    // La busta come la scrive il server (stesso ordine dei campi). Con payload null il campo "payload" manca del tutto.
    private static string Envelope(string eventName, string? payload, string deliveryId = DeliveryId, string occurredAt = OccurredAtText) =>
        "{\"event\":\"" + eventName + "\",\"delivery_id\":\"" + deliveryId + "\",\"occurred_at\":\"" + occurredAt + "\""
        + (payload is null ? string.Empty : ",\"payload\":" + payload) + "}";

    private static string UploadedPayload(
        string documentId = "\"" + DocumentIdText + "\"",
        string filename = "\"fattura.pdf\"",
        string sha256 = "\"" + Sha + "\"",
        string deduplicated = "false") =>
        "{\"document_id\":" + documentId + ",\"filename\":" + filename + ",\"sha256\":" + sha256 + ",\"deduplicated\":" + deduplicated + "}";

    private static string DeletedPayload(string documentId = "\"" + DocumentIdText + "\"", string sha256 = "\"" + Sha + "\"") =>
        "{\"document_id\":" + documentId + ",\"sha256\":" + sha256 + "}";

    private static string FailedPayload(
        string documentId = "\"" + DocumentIdText + "\"",
        string sha256 = "\"" + Sha + "\"",
        string detail = "\"hash diverso\"") =>
        "{\"document_id\":" + documentId + ",\"sha256\":" + sha256 + ",\"detail\":" + detail + "}";

    private static WebhookEvent Parse(string json) => WebhookEventParser.Parse(Utf8(json));

    private static UnknownWebhookEvent AssertUnknown(string json, string expectedEventType)
    {
        var parsed = Parse(json);
        var unknown = Assert.IsType<UnknownWebhookEvent>(parsed);
        Assert.Equal(expectedEventType, unknown.EventType);
        Assert.Equal(DeliveryId, unknown.DeliveryId);
        Assert.Equal(OccurredAt, unknown.OccurredAt);
        return unknown;
    }

    private static byte[] Concat(string head, byte[] middle, string tail) => Utf8(head).Concat(middle).Concat(Utf8(tail)).ToArray();

    // ------------------------------------------------------------------------------------------ eventi noti

    [Fact]
    public void document_uploaded_maps_every_field_of_the_server_payload()
    {
        // new { document_id, filename, sha256, deduplicated }: la chiave e' "filename", non "original_filename".
        var parsed = Parse(Envelope("document.uploaded", UploadedPayload(deduplicated: "true")));

        var uploaded = Assert.IsType<DocumentUploadedEvent>(parsed);
        Assert.Equal(DeliveryId, uploaded.DeliveryId);
        Assert.Equal(OccurredAt, uploaded.OccurredAt);
        Assert.Equal(DocId, uploaded.DocumentId);
        Assert.Equal("fattura.pdf", uploaded.OriginalFilename);
        Assert.Equal(Sha, uploaded.Sha256);
        Assert.True(uploaded.Deduplicated);
        Assert.Equal(new DocumentUploadedEvent(DeliveryId, OccurredAt, DocId, "fattura.pdf", Sha, true), uploaded);
    }

    [Fact]
    public void document_uploaded_deduplicated_false_is_false()
    {
        var uploaded = Assert.IsType<DocumentUploadedEvent>(Parse(Envelope("document.uploaded", UploadedPayload(deduplicated: "false"))));

        Assert.False(uploaded.Deduplicated);
    }

    [Fact]
    public void document_uploaded_filename_is_decoded_from_the_JSON_escapes_the_server_writes()
    {
        // Come lo scrive il server: apostrofo e lettera accentata come escape JSON con barra rovesciata e u (apostrofo 0027, e con accento 00e8).
        var filename = "\"perch" + Backslash + "u00e8 l" + Backslash + "u0027estate.pdf\"";
        var body = Envelope("document.uploaded", UploadedPayload(filename: filename));

        var uploaded = Assert.IsType<DocumentUploadedEvent>(Parse(body));

        Assert.Equal("perch\U000000E8 l'estate.pdf", uploaded.OriginalFilename);
    }

    [Fact]
    public void document_uploaded_filename_may_also_be_raw_UTF8_or_empty()
    {
        var raw = Assert.IsType<DocumentUploadedEvent>(Parse(Envelope("document.uploaded", UploadedPayload(filename: "\"perch\U000000E9 \U000065E5\U0000672C.pdf\""))));
        var empty = Assert.IsType<DocumentUploadedEvent>(Parse(Envelope("document.uploaded", UploadedPayload(filename: "\"\""))));

        Assert.Equal("perch\U000000E9 \U000065E5\U0000672C.pdf", raw.OriginalFilename);
        Assert.Equal(string.Empty, empty.OriginalFilename);
    }

    [Fact]
    public void document_deleted_maps_the_sha256_and_a_null_sha256_stays_a_known_event()
    {
        var withSha = Assert.IsType<DocumentDeletedEvent>(Parse(Envelope("document.deleted", DeletedPayload())));
        // Un documento senza contenuto: il server manda "sha256":null esplicito (il webhook non omette i null).
        var withoutContent = Assert.IsType<DocumentDeletedEvent>(Parse(Envelope("document.deleted", DeletedPayload(sha256: "null"))));

        Assert.Equal(new DocumentDeletedEvent(DeliveryId, OccurredAt, DocId, Sha), withSha);
        Assert.Equal(new DocumentDeletedEvent(DeliveryId, OccurredAt, DocId, null), withoutContent);
        Assert.Null(withoutContent.Sha256);
    }

    [Fact]
    public void document_integrity_failed_maps_the_fields_and_a_null_detail_stays_a_known_event()
    {
        var withDetail = Assert.IsType<DocumentIntegrityFailedEvent>(Parse(Envelope("document.integrity_failed", FailedPayload())));
        var withoutDetail = Assert.IsType<DocumentIntegrityFailedEvent>(Parse(Envelope("document.integrity_failed", FailedPayload(detail: "null"))));

        Assert.Equal(new DocumentIntegrityFailedEvent(DeliveryId, OccurredAt, DocId, Sha, "hash diverso"), withDetail);
        Assert.Equal(new DocumentIntegrityFailedEvent(DeliveryId, OccurredAt, DocId, Sha, null), withoutDetail);
    }

    [Fact]
    public void Each_known_event_name_maps_to_its_own_type_and_the_payload_fields_are_not_mixed_up()
    {
        // Lo stesso payload completo sotto tre nomi diversi: ogni nome sceglie il suo tipo e legge solo i suoi campi.
        var payload = "{\"document_id\":\"" + DocumentIdText + "\",\"filename\":\"a.pdf\",\"sha256\":\"" + Sha + "\",\"deduplicated\":true,\"detail\":\"d\"}";

        Assert.IsType<DocumentUploadedEvent>(Parse(Envelope("document.uploaded", payload)));
        Assert.IsType<DocumentDeletedEvent>(Parse(Envelope("document.deleted", payload)));
        Assert.IsType<DocumentIntegrityFailedEvent>(Parse(Envelope("document.integrity_failed", payload)));
    }

    [Fact]
    public void Extra_fields_in_the_envelope_and_in_the_payload_are_ignored_and_the_event_stays_known()
    {
        var body = "{\"event\":\"document.deleted\",\"future\":[1,2],\"delivery_id\":\"" + DeliveryId + "\",\"occurred_at\":\"" + OccurredAtText + "\","
            + "\"tenant_id\":\"ten_x\",\"payload\":{\"extra\":{\"a\":null},\"document_id\":\"" + DocumentIdText + "\",\"sha256\":\"" + Sha + "\"}}";

        var deleted = Assert.IsType<DocumentDeletedEvent>(Parse(body));

        Assert.Equal(new DocumentDeletedEvent(DeliveryId, OccurredAt, DocId, Sha), deleted);
    }

    [Fact]
    public void The_field_order_of_the_envelope_does_not_matter()
    {
        var body = "{\"payload\":" + DeletedPayload() + ",\"occurred_at\":\"" + OccurredAtText + "\",\"delivery_id\":\"" + DeliveryId + "\",\"event\":\"document.deleted\"}";

        Assert.Equal(new DocumentDeletedEvent(DeliveryId, OccurredAt, DocId, Sha), Parse(body));
    }

    [Fact]
    public void Whitespace_and_newlines_around_the_JSON_values_are_fine()
    {
        var body = "\r\n{ \"event\" : \"document.deleted\" ,\n \"delivery_id\" : \"" + DeliveryId + "\" , \"occurred_at\" : \"" + OccurredAtText + "\" , \"payload\" : " + DeletedPayload() + " }\r\n";

        Assert.Equal(new DocumentDeletedEvent(DeliveryId, OccurredAt, DocId, Sha), Parse(body));
    }

    [Fact]
    public void A_body_signed_and_verified_with_the_openssl_vector_parses_into_the_uploaded_event()
    {
        // Il corpo A del verificatore (derivato dal codice del server, non catturato) con la firma calcolata da openssl:
        // verifica, poi interpretazione. L'apostrofo arriva come escape '.
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(WebhookSignatureVerifierTests.SignedAt));
        var verifier = new WebhookSignatureVerifier(WebhookSignatureVerifierTests.Secret, timeProvider: clock);
        var body = WebhookSignatureVerifierTests.BodyA();

        var verdict = verifier.Verify("t=1700000000,v1=" + WebhookSignatureVerifierTests.SigA, body);
        Assert.True(verdict.IsValid);

        var uploaded = Assert.IsType<DocumentUploadedEvent>(WebhookEventParser.Parse(body));
        Assert.Equal(DeliveryId, uploaded.DeliveryId);
        Assert.Equal(DocId, uploaded.DocumentId);
        Assert.Equal("perch' e'.pdf", uploaded.OriginalFilename);
        Assert.Equal(Sha, uploaded.Sha256);
        Assert.False(uploaded.Deduplicated);
        Assert.Equal(new DateTimeOffset(2023, 11, 14, 22, 13, 20, 123, TimeSpan.Zero), uploaded.OccurredAt);
    }

    // ------------------------------------------------------------------------------------------ eventi sconosciuti

    [Fact]
    public void An_unknown_event_name_becomes_UnknownWebhookEvent_with_the_raw_name_and_payload()
    {
        var unknown = AssertUnknown(Envelope("document.renamed", "{\"document_id\":\"" + DocumentIdText + "\",\"new_name\":\"b.pdf\",\"n\":[1,2,3],\"x\":null}"), "document.renamed");

        Assert.Equal(JsonValueKind.Object, unknown.Payload.ValueKind);
        Assert.Equal("b.pdf", unknown.Payload.GetProperty("new_name").GetString());
        Assert.Equal(3, unknown.Payload.GetProperty("n").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, unknown.Payload.GetProperty("x").ValueKind);
    }

    [Theory]
    [InlineData("Document.Uploaded")]
    [InlineData("DOCUMENT.UPLOADED")]
    [InlineData(" document.uploaded")]
    [InlineData("document.uploaded ")]
    [InlineData("document.uploaded\\n")]
    [InlineData("document.uploade")]
    [InlineData("document.uploaded.v2")]
    [InlineData("document_uploaded")]
    [InlineData("")]
    [InlineData("x")]
    public void The_event_name_is_compared_exactly_and_anything_else_is_unknown_with_the_name_untouched(string name)
    {
        var jsonName = name.Replace("\\n", Backslash + "n");
        var expected = name.Replace("\\n", "\n");

        var unknown = AssertUnknown(Envelope(jsonName, UploadedPayload()), expected);

        Assert.Equal(JsonValueKind.Object, unknown.Payload.ValueKind);
    }

    [Theory]
    [InlineData("[1,2]", JsonValueKind.Array)]
    [InlineData("\"text\"", JsonValueKind.String)]
    [InlineData("42", JsonValueKind.Number)]
    [InlineData("true", JsonValueKind.True)]
    [InlineData("null", JsonValueKind.Null)]
    [InlineData("{}", JsonValueKind.Object)]
    public void An_unknown_event_keeps_a_payload_of_any_JSON_kind(string payloadJson, JsonValueKind kind)
    {
        var unknown = AssertUnknown(Envelope("something.new", payloadJson), "something.new");

        Assert.Equal(kind, unknown.Payload.ValueKind);
        Assert.Equal(payloadJson, unknown.Payload.GetRawText());
    }

    [Fact]
    public void An_unknown_event_without_a_payload_has_an_undefined_payload()
    {
        var unknown = AssertUnknown(Envelope("something.new", null), "something.new");

        Assert.Equal(JsonValueKind.Undefined, unknown.Payload.ValueKind);
    }

    [Fact]
    public void The_payload_of_an_unknown_event_is_a_clone_that_survives_the_parser_document()
    {
        var payload = "{\"deep\":{\"list\":[{\"a\":1},{\"b\":\"" + new string('x', 5000) + "\"}],\"flag\":true},\"text\":\"ciao\"}";
        var unknown = AssertUnknown(Envelope("something.new", payload), "something.new");

        // Il JsonDocument del parser e' gia' smaltito. Altre analisi riusano i buffer del pool: se l'elemento puntasse ancora
        // ai buffer del parser, qui leggerebbe spazzatura o lancerebbe.
        for (var i = 0; i < 50; i++)
        {
            Parse(Envelope("other.event", "{\"n\":" + i + ",\"pad\":\"" + new string('y', 6000) + "\"}"));
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        Assert.Equal(payload, unknown.Payload.GetRawText());
        Assert.Equal("ciao", unknown.Payload.GetProperty("text").GetString());
        Assert.True(unknown.Payload.GetProperty("deep").GetProperty("flag").GetBoolean());
        Assert.Equal(5000, unknown.Payload.GetProperty("deep").GetProperty("list")[1].GetProperty("b").GetString()!.Length);
    }

    [Fact]
    public void The_payload_clone_is_not_equal_by_value_to_another_parse_of_the_same_body()
    {
        // JsonElement non e' uguale per valore (identita' del documento): due parse dello stesso corpo danno eventi diversi.
        var body = Envelope("something.new", "{\"a\":1}");

        Assert.NotEqual(Parse(body), Parse(body));
    }

    // ------------------------------------------------------------------------------------------ evento noto con payload malformato

    public static TheoryData<string, string> MalformedKnownPayloads()
    {
        var id = "\"" + DocumentIdText + "\"";
        var sha = "\"" + Sha + "\"";
        return new TheoryData<string, string>
        {
            // document.uploaded: il payload non e' un oggetto
            { "document.uploaded", "[]" },
            { "document.uploaded", "\"x\"" },
            { "document.uploaded", "1" },
            { "document.uploaded", "true" },
            { "document.uploaded", "null" },
            { "document.uploaded", "{}" },
            // document.uploaded: un campo manca
            { "document.uploaded", "{\"filename\":\"a.pdf\",\"sha256\":" + sha + ",\"deduplicated\":false}" },
            { "document.uploaded", "{\"document_id\":" + id + ",\"sha256\":" + sha + ",\"deduplicated\":false}" },
            { "document.uploaded", "{\"document_id\":" + id + ",\"filename\":\"a.pdf\",\"deduplicated\":false}" },
            { "document.uploaded", "{\"document_id\":" + id + ",\"filename\":\"a.pdf\",\"sha256\":" + sha + "}" },
            // document.uploaded: un campo ha il tipo sbagliato
            { "document.uploaded", UploadedPayload(documentId: "12") },
            { "document.uploaded", UploadedPayload(documentId: "null") },
            { "document.uploaded", UploadedPayload(documentId: "[]") },
            { "document.uploaded", UploadedPayload(filename: "5") },
            { "document.uploaded", UploadedPayload(filename: "null") },
            { "document.uploaded", UploadedPayload(filename: "{}") },
            { "document.uploaded", UploadedPayload(sha256: "5") },
            { "document.uploaded", UploadedPayload(sha256: "null") },
            { "document.uploaded", UploadedPayload(deduplicated: "\"false\"") },
            { "document.uploaded", UploadedPayload(deduplicated: "\"true\"") },
            { "document.uploaded", UploadedPayload(deduplicated: "0") },
            { "document.uploaded", UploadedPayload(deduplicated: "1") },
            { "document.uploaded", UploadedPayload(deduplicated: "null") },
            // un document_id che non e' un id di documento canonico
            { "document.uploaded", UploadedPayload(documentId: "\"\"") },
            { "document.uploaded", UploadedPayload(documentId: "\"doc_\"") },
            { "document.uploaded", UploadedPayload(documentId: "\"doc_abc\"") },
            { "document.uploaded", UploadedPayload(documentId: "\"DOC_01M3VEESG5KBYR5PYAJ0TDT4B2\"") },
            { "document.uploaded", UploadedPayload(documentId: "\"doc_01m3veesg5kbyr5pyaj0tdt4b2\"") },
            { "document.uploaded", UploadedPayload(documentId: "\"" + DocumentIdText + " \"") },
            { "document.uploaded", UploadedPayload(documentId: "\"fld_01M3VEESG5KBYR5PYAJ0TDT4B2\"") },
            { "document.uploaded", UploadedPayload(documentId: "\"doc_81M3VEESG5KBYR5PYAJ0TDT4B2\"") },
            // document.deleted
            { "document.deleted", "[]" },
            { "document.deleted", "null" },
            { "document.deleted", "{\"sha256\":" + sha + "}" },
            { "document.deleted", "{\"document_id\":" + id + "}" }, // sha256 assente (il server lo manda sempre, anche null)
            { "document.deleted", DeletedPayload(documentId: "7") },
            { "document.deleted", DeletedPayload(documentId: "\"doc_abc\"") },
            { "document.deleted", DeletedPayload(sha256: "5") },
            { "document.deleted", DeletedPayload(sha256: "true") },
            { "document.deleted", DeletedPayload(sha256: "{}") },
            { "document.deleted", DeletedPayload(sha256: "[]") },
            // document.integrity_failed
            { "document.integrity_failed", "[]" },
            { "document.integrity_failed", "\"x\"" },
            { "document.integrity_failed", "{\"sha256\":" + sha + ",\"detail\":\"d\"}" },
            { "document.integrity_failed", "{\"document_id\":" + id + ",\"detail\":\"d\"}" },
            { "document.integrity_failed", "{\"document_id\":" + id + ",\"sha256\":" + sha + "}" }, // detail assente
            { "document.integrity_failed", FailedPayload(documentId: "\"doc_abc\"") },
            { "document.integrity_failed", FailedPayload(sha256: "null") }, // lo sha256 di una verifica fallita c'e' sempre
            { "document.integrity_failed", FailedPayload(sha256: "5") },
            { "document.integrity_failed", FailedPayload(detail: "5") },
            { "document.integrity_failed", FailedPayload(detail: "{}") },
            { "document.integrity_failed", FailedPayload(detail: "false") },
        };
    }

    [Theory]
    [MemberData(nameof(MalformedKnownPayloads))]
    public void A_known_event_with_a_malformed_payload_becomes_UnknownWebhookEvent_with_the_raw_payload(string eventName, string payloadJson)
    {
        var unknown = AssertUnknown(Envelope(eventName, payloadJson), eventName);

        Assert.Equal(payloadJson, unknown.Payload.GetRawText()); // il payload grezzo, intatto
    }

    [Theory]
    [InlineData("document.uploaded")]
    [InlineData("document.deleted")]
    [InlineData("document.integrity_failed")]
    public void A_known_event_without_a_payload_becomes_UnknownWebhookEvent_with_an_undefined_payload(string eventName)
    {
        var unknown = AssertUnknown(Envelope(eventName, null), eventName);

        Assert.Equal(JsonValueKind.Undefined, unknown.Payload.ValueKind);
    }

    [Fact]
    public void A_known_event_whose_payload_text_is_not_valid_UTF8_becomes_UnknownWebhookEvent_and_does_not_throw()
    {
        // 0xFF dentro "filename": System.Text.Json lo accetta nel parse e lancia InvalidOperationException alla lettura del testo.
        var body = Concat(
            "{\"event\":\"document.uploaded\",\"delivery_id\":\"" + DeliveryId + "\",\"occurred_at\":\"" + OccurredAtText + "\",\"payload\":{\"document_id\":\"" + DocumentIdText + "\",\"filename\":\"a",
            new byte[] { 0xFF, 0xFE },
            ".pdf\",\"sha256\":\"" + Sha + "\",\"deduplicated\":false}}");

        Assert.True(WebhookEventParser.TryParse(body, out var result));
        var unknown = Assert.IsType<UnknownWebhookEvent>(result);
        Assert.Equal("document.uploaded", unknown.EventType);
        Assert.Equal(JsonValueKind.Object, unknown.Payload.ValueKind);
    }

    [Fact]
    public void A_known_event_with_an_isolated_surrogate_escape_in_the_payload_becomes_UnknownWebhookEvent()
    {
        var payload = UploadedPayload(filename: "\"a" + Backslash + "ud800b.pdf\"");

        var unknown = AssertUnknown(Envelope("document.uploaded", payload), "document.uploaded");

        Assert.Equal(payload, unknown.Payload.GetRawText());
    }

    // ------------------------------------------------------------------------------------------ occurred_at

    [Theory]
    [InlineData("2026-10-01T09:59:15Z", 0)]
    [InlineData("2026-10-01T09:59:15.2Z", 2000000)]
    [InlineData("2026-10-01T09:59:15.205Z", 2050000)]
    [InlineData("2026-10-01T09:59:15.205123Z", 2051230)]
    [InlineData("2026-10-01T09:59:15.2051234Z", 2051234)]
    public void occurred_at_with_Z_is_read_as_UTC_with_the_fractions_the_server_writes(string text, long fractionTicks)
    {
        var expected = new DateTimeOffset(2026, 10, 1, 9, 59, 15, TimeSpan.Zero).AddTicks(fractionTicks);

        var parsed = Parse(Envelope("document.deleted", DeletedPayload(), occurredAt: text));

        Assert.Equal(expected, parsed.OccurredAt);
        Assert.Equal(TimeSpan.Zero, parsed.OccurredAt.Offset);
        Assert.Equal(expected.Ticks, parsed.OccurredAt.Ticks);
    }

    [Theory]
    [InlineData("2026-10-01T11:59:15.205+02:00", 2)]
    [InlineData("2026-10-01T04:59:15.205-05:00", -5)]
    [InlineData("2026-10-01T09:59:15.205+00:00", 0)]
    public void occurred_at_with_an_offset_keeps_the_offset_and_the_same_instant(string text, int offsetHours)
    {
        var parsed = Parse(Envelope("document.deleted", DeletedPayload(), occurredAt: text));

        Assert.Equal(OccurredAt, parsed.OccurredAt); // lo stesso istante
        Assert.Equal(TimeSpan.FromHours(offsetHours), parsed.OccurredAt.Offset);
    }

    [Theory]
    [InlineData("2026-10-01T09:59:15")] // senza fuso: System.Text.Json lo leggerebbe come ora locale
    [InlineData("2026-10-01T09:59:15.205")]
    [InlineData("2026-10-01")]
    [InlineData("2026-10-01T09:59:15z")]
    [InlineData("2026-10-01T09:59:15+0200")]
    [InlineData("2026-10-01T09:59:15+02")]
    [InlineData("2026-13-45T99:99:99Z")]
    [InlineData("")]
    [InlineData("ieri")]
    [InlineData("1759312755")]
    [InlineData("Z")]
    public void occurred_at_that_is_not_an_ISO_8601_instant_with_an_explicit_offset_is_a_non_conforming_envelope(string text)
    {
        var body = Utf8(Envelope("document.deleted", DeletedPayload(), occurredAt: text));

        Assert.False(WebhookEventParser.TryParse(body, out var result));
        Assert.Null(result);
        Assert.Throws<FormatException>(() => WebhookEventParser.Parse(body));
    }

    // ------------------------------------------------------------------------------------------ busta non conforme

    public static TheoryData<string> NonConformingEnvelopes()
    {
        var sha = "\"" + Sha + "\"";
        return new TheoryData<string>
        {
            string.Empty,
            " ",
            "\r\n",
            "not json",
            "{",
            "}",
            "{\"event\":",
            "{\"event\":\"document.deleted\"", // troncato
            "{} {}", // piu' valori
            "{} x", // spazzatura dopo il JSON
            "{\"event\":\"x\",}", // virgola finale
            "{'event':'x'}", // apici singoli
            "{/* c */}", // commento
            "{\"event\":\"x\"} // c",
            "null",
            "true",
            "123",
            "\"document.deleted\"",
            "[]",
            "[{\"event\":\"document.deleted\"}]",
            "{}",
            // event
            "{\"delivery_id\":\"" + DeliveryId + "\",\"occurred_at\":\"" + OccurredAtText + "\"}",
            "{\"event\":null,\"delivery_id\":\"" + DeliveryId + "\",\"occurred_at\":\"" + OccurredAtText + "\"}",
            "{\"event\":5,\"delivery_id\":\"" + DeliveryId + "\",\"occurred_at\":\"" + OccurredAtText + "\"}",
            "{\"event\":{},\"delivery_id\":\"" + DeliveryId + "\",\"occurred_at\":\"" + OccurredAtText + "\"}",
            "{\"event\":[\"document.deleted\"],\"delivery_id\":\"" + DeliveryId + "\",\"occurred_at\":\"" + OccurredAtText + "\"}",
            // delivery_id
            "{\"event\":\"document.deleted\",\"occurred_at\":\"" + OccurredAtText + "\",\"payload\":" + DeletedPayload() + "}",
            "{\"event\":\"document.deleted\",\"delivery_id\":null,\"occurred_at\":\"" + OccurredAtText + "\"}",
            "{\"event\":\"document.deleted\",\"delivery_id\":12,\"occurred_at\":\"" + OccurredAtText + "\"}",
            "{\"event\":\"document.deleted\",\"delivery_id\":\"\",\"occurred_at\":\"" + OccurredAtText + "\"}",
            "{\"event\":\"document.deleted\",\"delivery_id\":\"   \",\"occurred_at\":\"" + OccurredAtText + "\"}",
            "{\"event\":\"document.deleted\",\"delivery_id\":[],\"occurred_at\":\"" + OccurredAtText + "\"}",
            // occurred_at
            "{\"event\":\"document.deleted\",\"delivery_id\":\"" + DeliveryId + "\",\"payload\":" + DeletedPayload() + "}",
            "{\"event\":\"document.deleted\",\"delivery_id\":\"" + DeliveryId + "\",\"occurred_at\":null}",
            "{\"event\":\"document.deleted\",\"delivery_id\":\"" + DeliveryId + "\",\"occurred_at\":1759312755}",
            "{\"event\":\"document.deleted\",\"delivery_id\":\"" + DeliveryId + "\",\"occurred_at\":{}}",
            "{\"event\":\"document.deleted\",\"delivery_id\":\"" + DeliveryId + "\",\"occurred_at\":\"not a date\"}",
            // tutto tranne la busta
            "{\"payload\":{\"document_id\":\"" + DocumentIdText + "\",\"sha256\":" + sha + "}}",
            new string('[', 200) + new string(']', 200), // oltre la profondita' massima di System.Text.Json
            "{\"event\":\"x\",\"delivery_id\":\"d\",\"occurred_at\":\"" + OccurredAtText + "\",\"payload\":" + new string('[', 200) + new string(']', 200) + "}", // payload troppo profondo
        };
    }

    [Theory]
    [MemberData(nameof(NonConformingEnvelopes))]
    public void A_non_conforming_envelope_makes_TryParse_false_and_Parse_throw_FormatException(string json)
    {
        var body = Utf8(json);

        Assert.False(WebhookEventParser.TryParse(body, out var result));
        Assert.Null(result);
        var error = Assert.Throws<FormatException>(() => WebhookEventParser.Parse(body));
        Assert.Contains("webhook", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_byte_array_is_a_non_conforming_envelope_not_an_argument_error()
    {
        Assert.False(WebhookEventParser.TryParse(Array.Empty<byte>(), out var result));
        Assert.Null(result);
        Assert.Throws<FormatException>(() => WebhookEventParser.Parse(Array.Empty<byte>()));
    }

    [Fact]
    public void The_FormatException_message_says_what_is_wrong_and_does_not_echo_the_body()
    {
        var secretLookingValue = "do-not-echo-this-payload-value";
        Assert.Contains("JSON", Assert.Throws<FormatException>(() => Parse("{ not json " + secretLookingValue)).Message, StringComparison.Ordinal);
        Assert.Contains("oggetto", Assert.Throws<FormatException>(() => Parse("[\"" + secretLookingValue + "\"]")).Message, StringComparison.Ordinal);
        Assert.Contains("'event'", Assert.Throws<FormatException>(() => Parse("{\"x\":\"" + secretLookingValue + "\"}")).Message, StringComparison.Ordinal);
        Assert.Contains("'delivery_id'", Assert.Throws<FormatException>(() => Parse("{\"event\":\"a\"}")).Message, StringComparison.Ordinal);
        Assert.Contains("'occurred_at'", Assert.Throws<FormatException>(() => Parse("{\"event\":\"a\",\"delivery_id\":\"d\"}")).Message, StringComparison.Ordinal);

        foreach (var json in new[] { "{ not json " + secretLookingValue, "[\"" + secretLookingValue + "\"]", "{\"x\":\"" + secretLookingValue + "\"}" })
        {
            Assert.DoesNotContain(secretLookingValue, Assert.Throws<FormatException>(() => Parse(json)).Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void No_System_Text_Json_exception_escapes_for_text_that_is_not_valid_UTF8_or_has_isolated_surrogates()
    {
        var okTail = "\",\"delivery_id\":\"" + DeliveryId + "\",\"occurred_at\":\"" + OccurredAtText + "\"}";

        // 0xFF nei campi della busta
        var inEvent = Concat("{\"event\":\"a", new byte[] { 0xFF }, okTail);
        var inDelivery = Concat("{\"event\":\"a\",\"delivery_id\":\"w", new byte[] { 0xC0, 0xAF }, "\",\"occurred_at\":\"" + OccurredAtText + "\"}");
        var inOccurred = Concat("{\"event\":\"a\",\"delivery_id\":\"" + DeliveryId + "\",\"occurred_at\":\"2026-10-01T09:59:15", new byte[] { 0xFF }, "Z\"}");
        // surrogato isolato come escape JSON nella busta
        var surrogateInEvent = Utf8("{\"event\":\"a" + Backslash + "ud800" + okTail);
        var surrogateInDelivery = Utf8("{\"event\":\"a\",\"delivery_id\":\"w" + Backslash + "udc00\",\"occurred_at\":\"" + OccurredAtText + "\"}");

        foreach (var body in new[] { inEvent, inDelivery, inOccurred, surrogateInEvent, surrogateInDelivery })
        {
            Assert.False(WebhookEventParser.TryParse(body, out var result));
            Assert.Null(result);
            Assert.Throws<FormatException>(() => WebhookEventParser.Parse(body));
        }
    }

    [Fact]
    public void An_unknown_event_whose_payload_has_invalid_UTF8_keeps_the_payload_without_throwing()
    {
        var body = Concat(
            "{\"event\":\"something.new\",\"delivery_id\":\"" + DeliveryId + "\",\"occurred_at\":\"" + OccurredAtText + "\",\"payload\":{\"a\":\"x",
            new byte[] { 0xFF },
            "y\"}}");

        var parsed = WebhookEventParser.Parse(body);

        var unknown = Assert.IsType<UnknownWebhookEvent>(parsed);
        Assert.Equal(JsonValueKind.Object, unknown.Payload.ValueKind);
    }

    // ------------------------------------------------------------------------------------------ BOM

    [Fact]
    public void A_leading_UTF8_BOM_is_tolerated()
    {
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var json = Utf8(Envelope("document.deleted", DeletedPayload()));
        var body = bom.Concat(json).ToArray();

        Assert.True(WebhookEventParser.TryParse(body, out var result));
        Assert.Equal(new DocumentDeletedEvent(DeliveryId, OccurredAt, DocId, Sha), result);
        Assert.Equal(new DocumentDeletedEvent(DeliveryId, OccurredAt, DocId, Sha), WebhookEventParser.Parse(body));
        Assert.Equal(WebhookEventParser.Parse(json), WebhookEventParser.Parse(body)); // lo stesso evento con e senza BOM

        // Anche per un evento sconosciuto, il cui payload e' copiato dopo il BOM.
        var unknown = Assert.IsType<UnknownWebhookEvent>(WebhookEventParser.Parse(bom.Concat(Utf8(Envelope("x.y", "{\"a\":1}"))).ToArray()));
        Assert.Equal("{\"a\":1}", unknown.Payload.GetRawText());
    }

    [Fact]
    public void A_BOM_alone_two_BOMs_or_a_BOM_inside_are_not_a_valid_body()
    {
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var json = Utf8(Envelope("document.deleted", DeletedPayload()));

        Assert.False(WebhookEventParser.TryParse(bom, out _));
        Assert.False(WebhookEventParser.TryParse(bom.Concat(bom).Concat(json).ToArray(), out _));
        Assert.False(WebhookEventParser.TryParse(Utf8(" ").Concat(bom).Concat(json).ToArray(), out _)); // il BOM va in testa
        Assert.False(WebhookEventParser.TryParse(new byte[] { 0xEF, 0xBB }, out _));
        Assert.False(WebhookEventParser.TryParse(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'x' }, out _));
    }

    // ------------------------------------------------------------------------------------------ argomenti

    [Fact]
    public void A_null_body_throws_ArgumentNullException_from_both_entry_points()
    {
        Assert.Equal("body", Assert.Throws<ArgumentNullException>(() => WebhookEventParser.Parse(null!)).ParamName);
        Assert.Equal("body", Assert.Throws<ArgumentNullException>(() => WebhookEventParser.TryParse(null!, out _)).ParamName);
    }

    [Fact]
    public void TryParse_and_Parse_agree_on_every_conforming_body()
    {
        foreach (var json in new[]
        {
            Envelope("document.uploaded", UploadedPayload()),
            Envelope("document.deleted", DeletedPayload(sha256: "null")),
            Envelope("document.integrity_failed", FailedPayload()),
            Envelope("document.deleted", "[1]"),
        })
        {
            var body = Utf8(json);

            Assert.True(WebhookEventParser.TryParse(body, out var viaTry));
            var viaParse = WebhookEventParser.Parse(body);

            Assert.Equal(viaTry!.GetType(), viaParse.GetType());
            Assert.Equal(viaTry.DeliveryId, viaParse.DeliveryId);
            Assert.Equal(viaTry.OccurredAt, viaParse.OccurredAt);
        }
    }

    [Fact]
    public void The_parser_is_a_static_class_with_exactly_TryParse_and_Parse()
    {
        var type = typeof(WebhookEventParser);

        Assert.True(type.IsAbstract && type.IsSealed); // static
        Assert.Equal(
            new[] { "Parse", "TryParse" },
            type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly)
                .Select(m => m.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray());
    }
}
