using System.Globalization;
using System.Text;
using System.Text.Json;
using Filemaster.Application;
using Filemaster.Domain;
using Filemaster.Infrastructure;

namespace Filemaster.UnitTests.Wire;

/// <summary>
/// Le richieste che riguardano i documenti, costruite dai modelli dell'Application: la query dell'elenco (filtri piu' pagina), i corpi JSON di
/// spostamento e di verifica/spostamento in blocco, i campi di testo del caricamento. Ogni test confronta il testo ESATTO con un valore scritto a
/// mano o ricopiato da una richiesta catturata che il server ha accettato; l'ordine dei parametri e' stabile; le date escono UTC con la
/// <c>Z</c> con qualunque cultura corrente; <c>Validate()</c> dell'Application viene prima di tutto.
/// </summary>
public sealed class DocumentRequestWireTests
{
    private const string Cursor = "djF8MTc5MTU0NDI2OTA3NjQzMnwzMDAyOA";

    private static string List(DocumentQuery? query = null, PageRequest? page = null) => DocumentWire.ListPath(query, page);

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    // ----- nessun filtro -----

    [Fact]
    public void Without_filters_and_without_a_page_the_path_is_just_documents()
    {
        Assert.Equal("documents", List());
        Assert.Equal("documents", List(new DocumentQuery()));
        Assert.Equal("documents", List(new DocumentQuery(), new PageRequest()));
    }

    // ----- un filtro per volta, contro le richieste catturate -----

    [Fact]
    public void Each_captured_filter_request_is_reproduced_exactly()
    {
        // Richieste vere accettate dal server (la barra iniziale e' dell'indirizzo base, qui non c'e').
        Assert.Equal(Captured("79-docs-list-filter-folder"), List(new DocumentQuery { FolderId = new FolderCode("FATTURE") }));
        Assert.Equal(Captured("80-docs-list-filter-owner-tag"), List(new DocumentQuery { Owner = "maria", Tag = "fattura" }));
        Assert.Equal(Captured("81-docs-list-filter-sender"), List(new DocumentQuery { Sender = "Acme Srl" }));
        Assert.Equal(Captured("83-docs-list-filter-filename"), List(new DocumentQuery { FileName = "fattura" }));
        Assert.Equal(Captured("84-docs-list-filter-q"), List(new DocumentQuery { Text = "fattura" }));
        Assert.Equal(Captured("85-docs-list-filter-metadata-query"), List(new DocumentQuery { MetadataText = "X" }));
        Assert.Equal(Captured("77-docs-list-filter-metadata"), List(new DocumentQuery { Metadata = Json("{\"arxivar\":{\"docnumber\":12345}}") }));
    }

    [Fact]
    public void The_captured_contact_id_filters_are_reproduced_with_the_canonical_decimal_id()
    {
        // 265/266: sender_id=1 e recipient_id=1 (il contatto e' un intero: la query lo vuole in decimale canonico, mai 001 ne' +1).
        Assert.Equal(Captured("265-docs-list-filter-sender-id"), List(new DocumentQuery { SenderId = new ContactId("1") }));
        Assert.Equal(Captured("266-docs-list-filter-recipient-id"), List(new DocumentQuery { RecipientId = new ContactId("1") }));
        Assert.Equal(Captured("265-docs-list-filter-sender-id"), List(new DocumentQuery { SenderId = ContactId.From(1) }));
    }

    [Fact]
    public void The_captured_page_requests_are_reproduced_exactly()
    {
        Assert.Equal(Captured("71-docs-list-limit1-page1"), List(page: new PageRequest(limit: 1)));
        Assert.Equal(Captured("72-docs-list-limit1-page2"), List(page: new PageRequest(Cursor, 1)));
        Assert.Equal(Captured("73-docs-list-last-page-exact"), List(page: new PageRequest(limit: 13)));
        Assert.Equal(Captured("75-docs-list-last-page-cursor"), List(page: new PageRequest(CursorOf("75-docs-list-last-page-cursor"), 12)));
    }

    private static string Captured(string name) => WireFixtures.RequestPath(name).TrimStart('/');

    // Il valore del parametro cursor nel percorso della cattura (base64url: nulla da decodificare).
    private static string CursorOf(string name)
    {
        var path = WireFixtures.RequestPath(name);
        return path.Substring(path.IndexOf("cursor=", StringComparison.Ordinal) + "cursor=".Length);
    }

    [Fact]
    public void The_captured_date_request_with_a_Z_instant_is_reproduced_up_to_the_encoding_of_the_colons()
    {
        // 89: created_from=2026-01-01T00:00:00Z (i due punti non codificati nella cattura: lo stesso valore per il server).
        var path = List(new DocumentQuery { CreatedFrom = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) });

        Assert.Equal("documents?created_from=2026-01-01T00%3A00%3A00Z", path);
        Assert.Equal(Captured("89-docs-list-created-from-z"), Uri.UnescapeDataString(path));
    }

    // ----- il testo esatto di una query con ogni filtro -----

    [Fact]
    public void Every_filter_and_the_page_have_a_stable_order_and_the_exact_text()
    {
        var query = new DocumentQuery
        {
            FolderId = new FolderCode("FATTURE"),
            Owner = "maria",
            Tag = "fattura",
            FileName = "fattura 2026.pdf",
            Sender = "Acme Srl",
            Recipient = "Beta Spa",
            SenderId = new ContactId("41"),
            RecipientId = new ContactId("2147483647"),
            Text = "perch\U000000E9 &",
            MetadataText = "X=1",
            Metadata = Json("{\"arxivar\":{\"docnumber\":12345}}"),
            CreatedFrom = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            CreatedBefore = new DateTimeOffset(2026, 12, 31, 12, 0, 0, TimeSpan.FromHours(2)),
        };

        var path = List(query, new PageRequest(Cursor, 50));

        Assert.Equal(
            "documents?folder_id=FATTURE&owner=maria&tag=fattura&filename=fattura%202026.pdf&sender=Acme%20Srl&recipient=Beta%20Spa" +
            "&sender_id=41&recipient_id=2147483647&q=perch%C3%A9%20%26&metadata_query=X%3D1" +
            "&metadata=%7B%22arxivar%22%3A%7B%22docnumber%22%3A12345%7D%7D&created_from=2026-01-01T00%3A00%3A00Z&created_to=2026-12-31T10%3A00%3A00Z" +
            "&limit=50&cursor=" + Cursor,
            path);
    }

    [Fact]
    public void The_order_of_the_parameters_does_not_depend_on_the_order_the_properties_were_set()
    {
        var forward = new DocumentQuery { Owner = "a", Tag = "b", Text = "c" };
        var backward = new DocumentQuery { Text = "c", Tag = "b", Owner = "a" };

        Assert.Equal("documents?owner=a&tag=b&q=c", List(forward));
        Assert.Equal(List(forward), List(backward));
    }

    public static TheoryData<string, string> SingleFilters() => new()
    {
        { "folder_id", "documents?folder_id=A.b-c_d" },
        { "owner", "documents?owner=Mario%20Rossi" },
        { "tag", "documents?tag=%C3%A8" },
        { "filename", "documents?filename=a%26b.pdf" },
        { "sender", "documents?sender=x%2By" },
        { "recipient", "documents?recipient=100%25" },
        { "sender_id", "documents?sender_id=41" },
        { "recipient_id", "documents?recipient_id=2147483647" },
        { "q", "documents?q=a%3Db" },
        { "metadata_query", "documents?metadata_query=%22x%22" },
    };

    [Theory]
    [MemberData(nameof(SingleFilters))]
    public void Each_filter_uses_its_canonical_server_name_and_nothing_else(string name, string expected)
    {
        var query = new DocumentQuery();
        switch (name)
        {
            case "folder_id": query.FolderId = new FolderCode("A.b-c_d"); break;
            case "owner": query.Owner = "Mario Rossi"; break;
            case "tag": query.Tag = "\U000000E8"; break;
            case "filename": query.FileName = "a&b.pdf"; break;
            case "sender": query.Sender = "x+y"; break;
            case "recipient": query.Recipient = "100%"; break;
            case "sender_id": query.SenderId = new ContactId("41"); break;
            case "recipient_id": query.RecipientId = new ContactId("2147483647"); break;
            case "q": query.Text = "a=b"; break;
            case "metadata_query": query.MetadataText = "\"x\""; break;
            default: throw new InvalidOperationException(name);
        }

        Assert.Equal(expected, List(query));
    }

    [Fact]
    public void The_Italian_aliases_of_the_server_are_never_used()
    {
        var path = List(new DocumentQuery { Owner = "a", FileName = "b", Sender = "c", Recipient = "d", MetadataText = "e" });

        var names = path.Remove(0, "documents?".Length).Split('&').Select(p => p.Substring(0, p.IndexOf('='))).ToArray();

        Assert.Equal(new[] { "owner", "filename", "sender", "recipient", "metadata_query" }, names);
        foreach (var alias in new[] { "autore", "nome_file", "mittente", "destinatario", "q_metadata", "metadata_q", "cartella", "contatto" })
        {
            Assert.DoesNotContain(alias, names);
        }
    }

    [Fact]
    public void A_null_filter_adds_no_parameter_and_an_empty_text_is_sent_as_it_is()
    {
        Assert.Equal("documents?owner=", List(new DocumentQuery { Owner = string.Empty }));
        Assert.Equal("documents?tag=%20%20", List(new DocumentQuery { Tag = "  " }));
        Assert.Equal("documents", List(new DocumentQuery { Owner = null, Tag = null }));
    }

    [Fact]
    public void The_text_is_never_trimmed_by_the_client()
    {
        Assert.Equal("documents?q=%20abc%20", List(new DocumentQuery { Text = " abc " }));
    }

    // ----- paginazione -----

    [Fact]
    public void The_limit_and_the_cursor_are_added_in_that_order_and_only_when_present()
    {
        Assert.Equal("documents?limit=200", List(page: new PageRequest(limit: 200)));
        Assert.Equal("documents?limit=1", List(page: new PageRequest(limit: 1)));
        Assert.Equal("documents?cursor=abc", List(page: new PageRequest("abc")));
        Assert.Equal("documents?limit=7&cursor=abc", List(page: new PageRequest("abc", 7)));
    }

    [Fact]
    public void The_cursor_is_opaque_and_is_percent_encoded_whatever_characters_it_has()
    {
        // I cursori veri sono base64url senza nulla da codificare: il test usa un cursore sintetico con + / = % e spazi.
        Assert.Equal("documents?cursor=a%2Bb%2Fc%3Dd%25e%20f", List(page: new PageRequest("a+b/c=d%e f")));
        Assert.Equal("documents?cursor=%C3%A8%F0%9F%98%80", List(page: new PageRequest("\U000000E8\U0001F600")));
        Assert.Equal("documents?cursor=" + Cursor, List(page: new PageRequest(Cursor)));
    }

    [Fact]
    public void A_cursor_of_4096_characters_is_encoded_whole()
    {
        var cursor = new string('c', 4096);

        Assert.Equal("documents?cursor=" + cursor, List(page: new PageRequest(cursor)));
    }

    // ----- metadati -----

    [Fact]
    public void The_metadata_filter_is_the_raw_JSON_text_of_the_element()
    {
        var path = List(new DocumentQuery { Metadata = Json("{\"a\": [1, 2.0, \"x y\"], \"b\":{ \"c\":null }}") });

        Assert.Equal("documents?metadata=" + PercentEncoding.Encode("{\"a\": [1, 2.0, \"x y\"], \"b\":{ \"c\":null }}", "x"), path);
        Assert.Contains("2.0", Uri.UnescapeDataString(path), StringComparison.Ordinal); // il testo non e' riscritto
    }

    [Fact]
    public void The_metadata_filter_survives_the_round_trip_through_the_query_string_with_special_characters()
    {
        const string Raw = "{\"k\":\"a b&c=d+e%f/g \U000000E8 \U0001F600\"}";

        var path = List(new DocumentQuery { Metadata = Json(Raw) });

        var decoded = Uri.UnescapeDataString(path.Remove(0, "documents?metadata=".Length));
        Assert.Equal(Raw, decoded);
        Assert.DoesNotContain("+", path, StringComparison.Ordinal);
    }

    // ----- date -----

    [Fact]
    public void A_date_is_always_written_as_a_UTC_instant_with_Z_and_never_as_local_time()
    {
        var plus = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.FromHours(2));
        var minus = new DateTimeOffset(2026, 10, 1, 4, 30, 0, TimeSpan.FromHours(-5.5));

        Assert.Equal("documents?created_from=2026-10-01T10%3A00%3A00Z", List(new DocumentQuery { CreatedFrom = plus }));
        Assert.Equal("documents?created_from=2026-10-01T10%3A00%3A00Z", List(new DocumentQuery { CreatedFrom = minus }));
        Assert.Equal("documents?created_to=2026-10-01T10%3A00%3A00Z", List(new DocumentQuery { CreatedBefore = plus }));
    }

    [Fact]
    public void Fractions_appear_only_when_they_are_not_zero_and_never_the_date_only_form()
    {
        Assert.Equal("documents?created_from=2026-10-01T10%3A00%3A00.5Z", List(new DocumentQuery { CreatedFrom = new DateTimeOffset(2026, 10, 1, 10, 0, 0, 500, TimeSpan.Zero) }));
        Assert.Equal(
            "documents?created_from=2026-10-01T10%3A00%3A00.0000001Z",
            List(new DocumentQuery { CreatedFrom = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero).AddTicks(1) }));
        Assert.DoesNotContain("created_from=2026-10-01&", List(new DocumentQuery { CreatedFrom = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), CreatedBefore = new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero) }), StringComparison.Ordinal);
    }

    [Fact]
    public void The_extreme_dates_are_written_in_full()
    {
        Assert.Equal("documents?created_from=0001-01-01T00%3A00%3A00Z", List(new DocumentQuery { CreatedFrom = DateTimeOffset.MinValue }));
        Assert.Equal("documents?created_to=9999-12-31T23%3A59%3A59.9999999Z", List(new DocumentQuery { CreatedBefore = DateTimeOffset.MaxValue }));
    }

    [Theory]
    [InlineData("ar-SA")]
    [InlineData("th-TH")]
    [InlineData("fa-IR")]
    [InlineData("it-IT")]
    [InlineData("he-IL")]
    public void Dates_do_not_depend_on_the_current_culture_which_for_ar_SA_uses_another_calendar(string culture)
    {
        WireTest.WithCulture(culture, () =>
        {
            var path = List(
                new DocumentQuery
                {
                    CreatedFrom = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.FromHours(2)),
                    CreatedBefore = new DateTimeOffset(2026, 10, 31, 23, 59, 59, TimeSpan.Zero),
                },
                new PageRequest(limit: 50));

            Assert.Equal("documents?created_from=2026-10-01T10%3A00%3A00Z&created_to=2026-10-31T23%3A59%3A59Z&limit=50", path);
        });
    }

    [Fact]
    public void The_written_date_is_accepted_by_the_server_own_parser_and_means_the_same_instant()
    {
        // Il parser e' una copia di QueryParsing.Bound di Sharp-a-File (due formati, AssumeUniversal): e' l'oracolo, non il codice sotto prova.
        var formats = new[] { "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz" };
        foreach (var instant in new[]
        {
            new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.FromHours(2)),
            new DateTimeOffset(2026, 3, 29, 1, 30, 0, 123, TimeSpan.FromHours(-7)).AddTicks(4567),
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        })
        {
            var text = Uri.UnescapeDataString(List(new DocumentQuery { CreatedFrom = instant }).Remove(0, "documents?created_from=".Length));

            Assert.True(DateTimeOffset.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed), text);
            Assert.Equal(instant.UtcTicks, parsed.UtcTicks);
        }
    }

    // ----- Validate() prima di tutto -----

    [Fact]
    public void An_invalid_query_is_refused_before_anything_is_built()
    {
        Assert.Throws<ArgumentException>(() => List(new DocumentQuery { FolderId = default(FolderCode) }));
        Assert.Throws<ArgumentException>(() => List(new DocumentQuery { SenderId = default(ContactId) }));
        Assert.Throws<ArgumentException>(() => List(new DocumentQuery { RecipientId = default(ContactId) }));
        Assert.Throws<ArgumentException>(() => List(new DocumentQuery { Metadata = Json("[1]") }));
        Assert.Throws<ArgumentException>(() => List(new DocumentQuery { Metadata = default(JsonElement) }));
        var inverted = Assert.Throws<ArgumentException>(
            () => List(new DocumentQuery { CreatedFrom = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero), CreatedBefore = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) }));
        Assert.Equal("CreatedBefore", inverted.ParamName);
    }

    [Fact]
    public void A_filter_that_is_too_complex_for_the_server_is_refused()
    {
        var nodes = "{" + string.Join(",", Enumerable.Range(0, 70).Select(i => "\"k" + i + "\":1")) + "}";

        Assert.Throws<ArgumentException>(() => List(new DocumentQuery { Metadata = Json(nodes) }));
    }

    [Fact]
    public void A_text_with_a_lone_surrogate_is_refused_with_the_property_name()
    {
        var bad = "a" + new string((char)0xD800, 1);

        Assert.Equal("Owner", Assert.Throws<ArgumentException>(() => List(new DocumentQuery { Owner = bad })).ParamName);
        Assert.Equal("Text", Assert.Throws<ArgumentException>(() => List(new DocumentQuery { Text = bad })).ParamName);
        Assert.Equal("FileName", Assert.Throws<ArgumentException>(() => List(new DocumentQuery { FileName = bad })).ParamName);
        Assert.Equal("Cursor", Assert.Throws<ArgumentException>(() => List(page: new PageRequest(bad))).ParamName);
    }

    // ----- corpi JSON -----

    private static string Text(byte[] body) => new UTF8Encoding(false, true).GetString(body);

    private static readonly DocumentId One = new("30019");
    private static readonly DocumentId Two = new("30020");

    [Fact]
    public void The_move_body_is_exactly_the_one_the_server_accepted_in_the_captures()
    {
        // 136: {"folder_id":"FATTURE.2027"}; 138: {"folder_id":null}
        Assert.Equal(WireFixtures.RequestBody("136-doc-move-to-folder"), Text(DocumentWire.MoveBody(new FolderCode("FATTURE.2027"))));
        Assert.Equal(WireFixtures.RequestBody("138-doc-move-to-null"), Text(DocumentWire.MoveBody(null)));
        Assert.Equal("{\"folder_id\":\"FATTURE.2027\"}", Text(DocumentWire.MoveBody(new FolderCode("FATTURE.2027"))));
        Assert.Equal("{\"folder_id\":null}", Text(DocumentWire.MoveBody(null)));
    }

    [Fact]
    public void The_move_to_root_is_an_explicit_null_and_never_the_empty_object_or_a_renamed_property()
    {
        // La 139 ({}) e' accettata dal server ma non e' il modo di dire "radice"; la 142 ({"folderId":null}) e' rifiutata (proprieta' sconosciuta).
        Assert.Equal("{}", WireFixtures.RequestBody("139-doc-move-empty-object"));
        Assert.NotEqual("{}", Text(DocumentWire.MoveBody(null)));
        Assert.DoesNotContain("folderId", Text(DocumentWire.MoveBody(null)), StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_destination_folder_is_refused_for_a_move()
    {
        var exception = Assert.Throws<ArgumentException>(() => DocumentWire.MoveBody(default(FolderCode)));

        Assert.Equal("folder", exception.ParamName);
    }

    [Fact]
    public void The_bulk_move_body_is_exactly_the_one_the_server_accepted_in_the_captures()
    {
        // 133: {"document_ids":[..,..],"folder_id":"FATTURE.2027"}; 134: {"document_ids":[..],"folder_id":null}
        Assert.Equal(WireFixtures.RequestBody("133-docs-bulk-move"), Text(DocumentWire.BulkMoveBody(new[] { One, Two }, new FolderCode("FATTURE.2027"))));
        Assert.Equal(WireFixtures.RequestBody("134-docs-bulk-move-to-root"), Text(DocumentWire.BulkMoveBody(new[] { One }, null)));
        Assert.Equal(
            "{\"document_ids\":[30019,30020],\"folder_id\":\"FATTURE.2027\"}",
            Text(DocumentWire.BulkMoveBody(new[] { One, Two }, new FolderCode("FATTURE.2027"))));
    }

    [Fact]
    public void The_bulk_verify_body_is_exactly_the_one_the_server_accepted_in_the_capture()
    {
        // 129: {"document_ids":[30017,30019]}
        var ids = new[] { new DocumentId("30017"), One };

        Assert.Equal(WireFixtures.RequestBody("129-docs-bulk-verify"), Text(DocumentWire.BulkVerifyBody(ids)));
        Assert.Equal("{\"document_ids\":[30017,30019]}", Text(DocumentWire.BulkVerifyBody(ids)));
    }

    [Fact]
    public void The_ids_keep_their_order_and_their_duplicates()
    {
        var body = Text(DocumentWire.BulkVerifyBody(new[] { Two, One, Two }));

        Assert.Equal("{\"document_ids\":[30020,30019,30020]}", body);
    }

    [Fact]
    public void The_ids_are_written_as_bare_json_numbers_never_as_strings()
    {
        var ids = new[] { DocumentId.From(1), DocumentId.From(2147483648L), DocumentId.From(9007199254740993L), DocumentId.From(long.MaxValue) };

        var verify = Text(DocumentWire.BulkVerifyBody(ids));
        var move = Text(DocumentWire.BulkMoveBody(ids, new FolderCode("FATTURE.2027")));

        Assert.Equal("{\"document_ids\":[1,2147483648,9007199254740993,9223372036854775807]}", verify);
        Assert.Equal("{\"document_ids\":[1,2147483648,9007199254740993,9223372036854775807],\"folder_id\":\"FATTURE.2027\"}", move);
        using var document = JsonDocument.Parse(verify);
        Assert.All(document.RootElement.GetProperty("document_ids").EnumerateArray(), id => Assert.Equal(JsonValueKind.Number, id.ValueKind));
        Assert.Equal(9007199254740993L, document.RootElement.GetProperty("document_ids")[2].GetInt64());
    }

    [Fact]
    public void A_very_large_batch_is_written_whole()
    {
        var ids = Enumerable.Range(0, 30_000).Select(i => DocumentId.From(i + 1L)).ToArray();

        var body = DocumentWire.BulkVerifyBody(ids);

        using var document = JsonDocument.Parse(body);
        Assert.Equal(30_000, document.RootElement.GetProperty("document_ids").GetArrayLength());
        Assert.True(body.Length < 1024 * 1024, "30.000 id stanno nel limite di 1 MiB del server");
    }

    [Fact]
    public void The_bulk_bodies_have_no_other_property_than_the_ones_the_server_knows()
    {
        // Il server rifiuta con 400 una proprieta' sconosciuta (UnmappedMemberHandling.Disallow): il corpo ha SOLO questi nomi.
        Assert.Equal(new[] { "document_ids" }, PropertyNames(DocumentWire.BulkVerifyBody(new[] { One })));
        Assert.Equal(new[] { "document_ids", "folder_id" }, PropertyNames(DocumentWire.BulkMoveBody(new[] { One }, null)));
        Assert.Equal(new[] { "folder_id" }, PropertyNames(DocumentWire.MoveBody(null)));
    }

    private static string[] PropertyNames(byte[] body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
    }

    [Fact]
    public void Bulk_ids_are_validated_before_anything_is_written()
    {
        Assert.Throws<ArgumentNullException>(() => DocumentWire.BulkVerifyBody(null!));
        Assert.Throws<ArgumentNullException>(() => DocumentWire.BulkMoveBody(null!, null));
        Assert.Equal("ids", Assert.Throws<ArgumentException>(() => DocumentWire.BulkVerifyBody(Array.Empty<DocumentId>())).ParamName);
        Assert.Equal("ids", Assert.Throws<ArgumentException>(() => DocumentWire.BulkMoveBody(Array.Empty<DocumentId>(), null)).ParamName);
        Assert.Equal("ids", Assert.Throws<ArgumentException>(() => DocumentWire.BulkVerifyBody(new[] { One, default })).ParamName);
        Assert.Equal("ids", Assert.Throws<ArgumentException>(() => DocumentWire.BulkMoveBody(new[] { default(DocumentId) }, null)).ParamName);
        Assert.Equal("folder", Assert.Throws<ArgumentException>(() => DocumentWire.BulkMoveBody(new[] { One }, default(FolderCode))).ParamName);
    }

    // ----- campi di testo del caricamento -----

    private static UploadDocumentRequest Upload(Stream? content = null) => new(content ?? new MemoryStream(new byte[] { 1, 2, 3 }), "fattura.pdf");

    private static string Fields(UploadDocumentRequest request) =>
        string.Join("|", DocumentWire.UploadFields(request).Select(f => f.Key + "=" + f.Value));

    [Fact]
    public void An_upload_without_optional_fields_has_no_text_fields()
    {
        Assert.Empty(DocumentWire.UploadFields(Upload()));
    }

    [Fact]
    public void The_upload_text_fields_have_the_server_names_in_a_stable_order_and_are_sent_as_they_are()
    {
        var request = Upload();
        request.Metadata = Json("{\"arxivar\":{\"docnumber\":12345,\"categoria\":\"X\"}}");
        request.Recipient = "Beta Spa";
        request.Sender = "Acme Srl";
        request.Tag = "fattura";
        request.Owner = "maria";
        request.FolderId = new FolderCode("FATTURE");

        // Come la cattura 47 (curl -F folder_id owner tag sender recipient metadata): stessi nomi, stesso ordine, stessi valori.
        Assert.Equal(
            "folder_id=FATTURE|owner=maria|tag=fattura|sender=Acme Srl|recipient=Beta Spa|metadata={\"arxivar\":{\"docnumber\":12345,\"categoria\":\"X\"}}",
            Fields(request));
        var captured = WireFixtures.CapturedText("47-doc-upload", "request");
        foreach (var field in DocumentWire.UploadFields(request))
        {
            Assert.Contains("# curl: " + field.Key + "=" + field.Value, captured, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_upload_fields_are_not_trimmed_and_an_empty_value_is_sent_but_a_null_is_not()
    {
        var request = Upload();
        request.Owner = "  maria  ";
        request.Tag = string.Empty;
        request.Sender = null;

        Assert.Equal("owner=  maria  |tag=", Fields(request));
    }

    [Fact]
    public void The_upload_fields_keep_unicode_and_the_metadata_text_as_written()
    {
        var request = Upload();
        request.Owner = "Societ\U000000E0 \U0001F600";
        request.Metadata = Json("{ \"a\" : 1.0 }");

        Assert.Equal("owner=Societ\U000000E0 \U0001F600|metadata={ \"a\" : 1.0 }", Fields(request));
    }

    [Fact]
    public void The_upload_request_is_validated_first_without_reading_the_stream()
    {
        using var closed = new MemoryStream();
        closed.Dispose();
        var request = new UploadDocumentRequest(closed, "a.pdf");

        Assert.Equal("Content", Assert.Throws<ArgumentException>(() => DocumentWire.UploadFields(request)).ParamName);
        Assert.Throws<ArgumentNullException>(() => DocumentWire.UploadFields(null!));
        Assert.Equal("FolderId", Assert.Throws<ArgumentException>(() => DocumentWire.UploadFields(WithFolder(default(FolderCode)))).ParamName);
        Assert.Equal("Metadata", Assert.Throws<ArgumentException>(() => DocumentWire.UploadFields(WithMetadata(Json("[1]")))).ParamName);
        Assert.Equal("Owner", Assert.Throws<ArgumentException>(() => DocumentWire.UploadFields(WithOwner(new string('o', 256)))).ParamName);
    }

    private static UploadDocumentRequest WithFolder(FolderCode folder)
    {
        var request = Upload();
        request.FolderId = folder;
        return request;
    }

    private static UploadDocumentRequest WithMetadata(JsonElement metadata)
    {
        var request = Upload();
        request.Metadata = metadata;
        return request;
    }

    private static UploadDocumentRequest WithOwner(string owner)
    {
        var request = Upload();
        request.Owner = owner;
        return request;
    }

    [Fact]
    public void The_upload_fields_do_not_read_or_move_the_content_stream()
    {
        var stream = new MemoryStream(new byte[] { 1, 2, 3, 4 });
        stream.Position = 2;
        var request = new UploadDocumentRequest(stream, "a.pdf") { Owner = "x" };

        DocumentWire.UploadFields(request);

        Assert.Equal(2, stream.Position);
    }

    [Fact]
    public void A_lone_surrogate_in_an_upload_text_is_refused_with_the_property_name()
    {
        var bad = "a" + new string((char)0xDC00, 1);

        Assert.Equal("Owner", Assert.Throws<ArgumentException>(() => DocumentWire.UploadFields(WithOwner(bad))).ParamName);
        var tag = Upload();
        tag.Tag = bad;
        Assert.Equal("Tag", Assert.Throws<ArgumentException>(() => DocumentWire.UploadFields(tag)).ParamName);
        var sender = Upload();
        sender.Sender = bad;
        Assert.Equal("Sender", Assert.Throws<ArgumentException>(() => DocumentWire.UploadFields(sender)).ParamName);
        var recipient = Upload();
        recipient.Recipient = bad;
        Assert.Equal("Recipient", Assert.Throws<ArgumentException>(() => DocumentWire.UploadFields(recipient)).ParamName);
    }
}
