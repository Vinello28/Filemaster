using System.Globalization;
using System.Text.Json;
using Filemaster.Application;
using Filemaster.Domain;
using Filemaster.Infrastructure;

namespace Filemaster.UnitTests.Wire;

/// <summary>
/// I documenti letti dalle risposte catturate dal server <c>master</c> 541f378 (capture t64: caricamenti 47-54, dettagli 98-101, elenchi 70-75) e
/// dalle due risposte DERIVATE dal codice del server (documento con contatti, documento senza contenuto con id oltre <c>int.MaxValue</c>).
/// L'id di un documento e' un NUMERO JSON (<c>bigint</c> sul server). I valori attesi sono ricopiati dai file. Poi gli obblighi che il Domain impone a chi legge: <c>metadata</c> copiato e mai <c>default</c>, <c>has_content</c>
/// ignorato, <c>contacts</c> assente = lista vuota, ogni id con il <c>TryParse</c> del suo tipo, <c>sha256</c> e <c>size_bytes</c> validati.
/// </summary>
public sealed class DocumentWireTests
{
    private const string FatturaId = "30017";
    private const string FatturaSha = "cc1ba284a9fe9cefa40d4bd9dfb8d9e7fb395431aaf79478efca4e04da6c9d7e";

    private static Document Read(byte[] body, int status = 200) => DocumentWire.ReadDocument(body, WireTest.Context(status));

    private static UploadResult ReadUpload(byte[] body) => DocumentWire.ReadUploadResult(body, WireTest.Context(201));

    private static Page<Document> ReadPage(byte[] body) => DocumentWire.ReadPage(body, WireTest.Context());

    // Un istante scritto nel file, letto con un parser DEL TEST (non quello sotto prova).
    private static DateTimeOffset At(string text) =>
        DateTimeOffset.ParseExact(text, "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", CultureInfo.InvariantCulture, DateTimeStyles.None);

    // ----- caricamenti catturati -----

    [Fact]
    public void The_captured_upload_with_every_field_is_read_field_by_field()
    {
        // 47-doc-upload (curl -F: file, folder_id=FATTURE, owner, tag, sender, recipient, metadata) -> 201. In t64 i byte erano gia' nel
        // magazzino di una corsa precedente: anche il primo caricamento della corsa ha "deduplicated":true (vedi il README delle fixture).
        var result = ReadUpload(WireFixtures.Captured("47-doc-upload"));
        var document = result.Document;

        Assert.True(result.Deduplicated);
        Assert.Equal(30017L, document.Id.Number);
        Assert.Equal(new DocumentId(FatturaId), document.Id);
        Assert.Equal(new FolderCode("FATTURE"), document.FolderId);
        Assert.Equal("fattura.pdf", document.OriginalFilename);
        Assert.Equal("application/pdf", document.MimeType);
        Assert.Equal(FatturaSha, document.Sha256);
        Assert.Equal(590L, document.SizeBytes);
        Assert.Equal("maria", document.Owner);
        Assert.Equal("fattura", document.Tag);
        Assert.Equal("Acme Srl", document.Sender);
        Assert.Equal("Beta Spa", document.Recipient);
        Assert.Equal("{\"arxivar\":{\"docnumber\":12345,\"categoria\":\"X\"}}", document.Metadata.GetRawText());
        Assert.Equal(At("2026-10-09T11:11:07.949452Z"), document.CreatedAt);
        Assert.True(document.HasContent);
        Assert.Empty(document.Contacts);
    }

    [Fact]
    public void The_captured_deduplicated_upload_is_a_new_document_with_the_same_hash_and_Deduplicated_true()
    {
        // 48-doc-upload-dedup: stessi byte della 47, id nuovo (il documento nuovo c'e' sempre), "deduplicated":true
        var first = ReadUpload(WireFixtures.Captured("47-doc-upload"));
        var again = ReadUpload(WireFixtures.Captured("48-doc-upload-dedup"));

        Assert.True(again.Deduplicated);
        Assert.Equal(new DocumentId("30018"), again.Document.Id);
        Assert.NotEqual(first.Document.Id, again.Document.Id);
        Assert.Equal(first.Document.Sha256, again.Document.Sha256);
        Assert.Equal(At("2026-10-09T11:11:08.046669Z"), again.Document.CreatedAt);
    }

    [Fact]
    public void A_fresh_upload_has_deduplicated_false()
    {
        // Le catture t64 hanno tutte "deduplicated":true (vedi sopra): il caso "contenuto nuovo" si prova con una variante di una cattura.
        var result = ReadUpload(Variants.With(WireFixtures.Captured("47-doc-upload"), "deduplicated", "false"));

        Assert.False(result.Deduplicated);
        Assert.Equal(new DocumentId(FatturaId), result.Document.Id);
    }

    [Fact]
    public void The_captured_upload_without_metadata_has_an_empty_object_and_no_optional_field()
    {
        // 49-doc-upload-no-metadata: nessuna cartella, owner, tag, mittente, destinatario; "metadata":{}
        var result = ReadUpload(WireFixtures.Captured("49-doc-upload-no-metadata"));
        var document = result.Document;

        Assert.Equal(30019L, document.Id.Number);
        Assert.Null(document.FolderId);
        Assert.Null(document.Owner);
        Assert.Null(document.Tag);
        Assert.Null(document.Sender);
        Assert.Null(document.Recipient);
        Assert.Equal(JsonValueKind.Object, document.Metadata.ValueKind);
        Assert.Equal("{}", document.Metadata.GetRawText());
        Assert.Equal("altro.pdf", document.OriginalFilename);
        Assert.Equal(589L, document.SizeBytes);
        Assert.Equal("420a60ce8926908ea6e4c8fae82830fe434e278a6d8b0a6453dfc89affb16d4c", document.Sha256);
    }

    [Fact]
    public void The_captured_text_upload_keeps_the_declared_type_owner_and_tag()
    {
        // 50-doc-upload-text
        var document = ReadUpload(WireFixtures.Captured("50-doc-upload-text")).Document;

        Assert.Equal("note.txt", document.OriginalFilename);
        Assert.Equal("text/plain", document.MimeType);
        Assert.Equal(17L, document.SizeBytes);
        Assert.Equal("maria", document.Owner);
        Assert.Equal("nota", document.Tag);
        Assert.Equal("cf33f6169449c73f86f96c3b6ca248e68d4ebcf6b9b9075e2b1b44df975cad36", document.Sha256);
    }

    [Fact]
    public void The_captured_non_ASCII_file_names_are_read_as_UTF8()
    {
        // 52 (filename raw UTF-8), 54 (filename e filename*), 100 (dettaglio), 101 (dettaglio, "star")
        Assert.Equal("perch\U000000E9 \U000000E8.pdf", ReadUpload(WireFixtures.Captured("52-doc-upload-nonascii-raw-filename")).Document.OriginalFilename);
        Assert.Equal("perch\U000000E9 \U000000E8 both.pdf", ReadUpload(WireFixtures.Captured("54-doc-upload-filename-and-star")).Document.OriginalFilename);
        Assert.Equal("perch\U000000E9 \U000000E8.pdf", Read(WireFixtures.Captured("100-doc-get-nonascii-name")).OriginalFilename);
        Assert.Equal("perch\U000000E9 \U000000E8 star.pdf", Read(WireFixtures.Captured("101-doc-get-star-filename")).OriginalFilename);
    }

    // ----- dettagli catturati -----

    [Fact]
    public void The_captured_detail_with_a_folder_has_an_empty_contact_list()
    {
        // 98-doc-get: ... "has_content":true,"contacts":[]
        var document = Read(WireFixtures.Captured("98-doc-get"));

        Assert.Equal(new DocumentId(FatturaId), document.Id);
        Assert.Equal(new FolderCode("FATTURE"), document.FolderId);
        Assert.Empty(document.Contacts);
        Assert.NotNull(document.Contacts);
        Assert.Equal("{\"arxivar\":{\"docnumber\":12345,\"categoria\":\"X\"}}", document.Metadata.GetRawText());
    }

    [Fact]
    public void The_captured_detail_without_a_folder_has_a_null_folder_and_empty_metadata()
    {
        // 99-doc-get-no-folder: nessun folder_id; "metadata":{}
        var document = Read(WireFixtures.Captured("99-doc-get-no-folder"));

        Assert.Equal(new DocumentId("30020"), document.Id);
        Assert.Null(document.FolderId);
        Assert.Equal("{}", document.Metadata.GetRawText());
        Assert.Equal("note.txt", document.OriginalFilename);
    }

    [Fact]
    public void The_metadata_of_a_read_document_survives_the_disposal_of_the_response_and_feeds_the_Domain_reader()
    {
        // Senza Clone() l'elemento punterebbe a un JsonDocument smaltito: ogni lettura lancerebbe ObjectDisposedException.
        var document = Read(WireFixtures.Captured("98-doc-get"));
        GC.Collect();

        var arxivar = ArxivarMetadata.From(document.Metadata);

        Assert.NotNull(arxivar);
        Assert.Equal(12345, arxivar!.Docnumber);
        Assert.Equal("X", arxivar.Category);
        Assert.Contains("12345", document.ToString(), StringComparison.Ordinal);
    }

    // ----- elenchi catturati -----

    private static readonly (string Id, string? Folder, string Name, string Mime, string? Sha, long Size, string? Owner, string? Tag, string? Sender, string? Recipient, string Created)[] DefaultList =
    {
        ("30028", null, "random5m.bin", "application/octet-stream", "e92dc1199624a1dc46ac306604aa5abedc5f17754e9019426cf973532f5329d8", 5242880, null, "grande", null, null, "2026-10-09T11:11:09.076432Z"),
        ("30027", null, "liar.pdf", "text/plain", "2642a9f1a864c2ac7c40ca37a5c39e3b1b23ac0055ecd27e88df3f2494690daa", 591, null, null, null, null, "2026-10-09T11:11:08.92895Z"),
        ("30026", null, "sniff2.pdf", "application/pdf", "f81091e6daca5756241638b89ade5305e5907ba5a7a60b8628b78218fbddfb96", 591, null, null, null, null, "2026-10-09T11:11:08.843855Z"),
        ("30025", null, "sniff1.pdf", "application/pdf", "6fcf3405e0f5487879d69582c72c60bca7f58108d76c16616dd9602b1ae895d6", 591, null, null, null, null, "2026-10-09T11:11:08.728948Z"),
        ("30024", null, "perch\U000000E9 \U000000E8 both.pdf", "application/pdf", "cc38423f17e44c4c4627046fff07e50d2d7a3c6ad43d1d53470b19c6811d5e0e", 592, null, null, null, null, "2026-10-09T11:11:08.636571Z"),
        ("30023", null, "perch\U000000E9 \U000000E8 star.pdf", "application/pdf", "3364de3cd740b830c8ec1f3d5d5992522e213f2c978645aa82d91dae6a4de59e", 591, "maria", null, null, null, "2026-10-09T11:11:08.523468Z"),
        ("30022", null, "perch\U000000E9 \U000000E8.pdf", "application/pdf", "6244a7b9a08bbf16367cffda96c73afcf99edf5392d9e3d6359a3d5eee70ad54", 590, null, null, null, null, "2026-10-09T11:11:08.408999Z"),
        ("30021", null, "alias.txt", "text/plain", "cf33f6169449c73f86f96c3b6ca248e68d4ebcf6b9b9075e2b1b44df975cad36", 17, null, null, "Acme", "Beta", "2026-10-09T11:11:08.318237Z"),
        ("30020", null, "note.txt", "text/plain", "cf33f6169449c73f86f96c3b6ca248e68d4ebcf6b9b9075e2b1b44df975cad36", 17, "maria", "nota", null, null, "2026-10-09T11:11:08.226216Z"),
        ("30019", null, "altro.pdf", "application/pdf", "420a60ce8926908ea6e4c8fae82830fe434e278a6d8b0a6453dfc89affb16d4c", 589, null, null, null, null, "2026-10-09T11:11:08.134616Z"),
        ("30018", "FATTURE", "fattura.pdf", "application/pdf", FatturaSha, 590, "maria", "fattura", "Acme Srl", "Beta Spa", "2026-10-09T11:11:08.046669Z"),
        (FatturaId, "FATTURE", "fattura.pdf", "application/pdf", FatturaSha, 590, "maria", "fattura", "Acme Srl", "Beta Spa", "2026-10-09T11:11:07.949452Z"),
        ("1", null, "e2e-seed-senza-contenuto.pdf", "application/pdf", null, 0, "e2e-seed", "e2e-seed", "Fornitore E2E", "Utente E2E", "2026-10-09T11:00:45.111024Z"),
    };

    [Fact]
    public void The_captured_default_listing_has_thirteen_documents_in_the_server_order_and_none_has_contacts()
    {
        // 70-docs-list-default: dal piu' recente; nessun next_cursor (13 documenti, il limite di default e' 50); l'ultimo e' il documento seminato senza contenuto
        var page = ReadPage(WireFixtures.Captured("70-docs-list-default"));

        Assert.Null(page.NextCursor);
        Assert.Equal(DefaultList.Length, page.Items.Count);
        for (var i = 0; i < DefaultList.Length; i++)
        {
            var expected = DefaultList[i];
            var actual = page.Items[i];
            Assert.Equal(new DocumentId(expected.Id), actual.Id);
            Assert.Equal(expected.Folder is null ? (FolderCode?)null : new FolderCode(expected.Folder), actual.FolderId);
            Assert.Equal(expected.Name, actual.OriginalFilename);
            Assert.Equal(expected.Mime, actual.MimeType);
            Assert.Equal(expected.Sha, actual.Sha256);
            Assert.Equal(expected.Size, actual.SizeBytes);
            Assert.Equal(expected.Owner, actual.Owner);
            Assert.Equal(expected.Tag, actual.Tag);
            Assert.Equal(expected.Sender, actual.Sender);
            Assert.Equal(expected.Recipient, actual.Recipient);
            Assert.Equal(At(expected.Created), actual.CreatedAt);
            Assert.Equal(expected.Sha is not null, actual.HasContent);
            Assert.Empty(actual.Contacts);
        }

        Assert.Equal(DefaultList.Select(d => long.Parse(d.Id, CultureInfo.InvariantCulture)).ToArray(), page.Items.Select(d => d.Id.Number).ToArray());
    }

    [Fact]
    public void Every_document_of_a_listing_has_its_own_valid_metadata_element()
    {
        var page = ReadPage(WireFixtures.Captured("70-docs-list-default"));

        var raw = page.Items.Select(d => d.Metadata.GetRawText()).ToArray();

        Assert.Equal(10, raw.Count(r => r == "{}"));
        Assert.Equal(2, raw.Count(r => r == "{\"arxivar\":{\"docnumber\":12345,\"categoria\":\"X\"}}"));
        Assert.Equal(1, raw.Count(r => r == "{\"e2e\":{\"seed\":true}}"));
    }

    [Fact]
    public void The_captured_pages_chain_with_an_opaque_cursor_and_the_last_page_has_none()
    {
        // 71 (limit=1): 1 documento + cursore; 72 (limit=1&cursor=...): il documento dopo + un altro cursore; 75: ultima pagina senza cursore (il documento seminato)
        var first = ReadPage(WireFixtures.Captured("71-docs-list-limit1-page1"));
        var second = ReadPage(WireFixtures.Captured("72-docs-list-limit1-page2"));
        var last = ReadPage(WireFixtures.Captured("75-docs-list-last-page-cursor"));

        Assert.Equal(new DocumentId("30028"), Assert.Single(first.Items).Id);
        Assert.Equal("djF8MTc5MTU0NDI2OTA3NjQzMnwzMDAyOA", first.NextCursor);
        Assert.Equal(new DocumentId("30027"), Assert.Single(second.Items).Id);
        Assert.Equal("djF8MTc5MTU0NDI2ODkyODk1MHwzMDAyNw", second.NextCursor);
        Assert.Equal(new DocumentId("1"), Assert.Single(last.Items).Id);
        Assert.Null(last.NextCursor);
    }

    [Fact]
    public void A_full_last_page_has_no_cursor()
    {
        // 73 (limit=13 con 13 documenti): una pagina piena puo' essere l'ultima; l'unico segnale e' l'assenza del cursore
        var page = ReadPage(WireFixtures.Captured("73-docs-list-last-page-exact"));

        Assert.Equal(13, page.Items.Count);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public void The_cursor_of_the_captured_page_is_the_one_the_next_request_sent()
    {
        // La richiesta 72 ha spedito proprio il cursore della risposta 71: il cursore e' opaco e va restituito tale e quale.
        var first = ReadPage(WireFixtures.Captured("71-docs-list-limit1-page1"));

        Assert.Contains("cursor=" + first.NextCursor, WireFixtures.RequestPath("72-docs-list-limit1-page2"), StringComparison.Ordinal);
    }

    // ----- varianti di una fixture vera -----

    private static byte[] Detail() => WireFixtures.Captured("98-doc-get");

    [Theory]
    [InlineData("id")]
    [InlineData("original_filename")]
    [InlineData("mime_type")]
    [InlineData("size_bytes")]
    [InlineData("created_at")]
    public void A_missing_required_field_is_not_interpretable_and_names_the_field(string field)
    {
        var exception = WireTest.Unexpected(() => Read(Variants.Without(Detail(), field)));

        Assert.Contains(field, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("original_filename")]
    [InlineData("mime_type")]
    [InlineData("size_bytes")]
    [InlineData("created_at")]
    public void A_null_required_field_is_not_interpretable(string field)
    {
        WireTest.Unexpected(() => Read(Variants.With(Detail(), field, "null")));
    }

    [Theory]
    [InlineData("id", "\"30017\"")] // l'id e' un numero JSON: una stringa di cifre non lo e'
    [InlineData("id", "true")]
    [InlineData("id", "[30017]")]
    [InlineData("original_filename", "5")]
    [InlineData("original_filename", "[\"a.pdf\"]")]
    [InlineData("mime_type", "{}")]
    [InlineData("size_bytes", "\"590\"")]
    [InlineData("size_bytes", "590.5")]
    [InlineData("size_bytes", "1e3")]
    [InlineData("size_bytes", "-1")]
    [InlineData("size_bytes", "9223372036854775808")]
    [InlineData("created_at", "1790848755")]
    [InlineData("folder_id", "7")]
    [InlineData("owner", "7")]
    [InlineData("tag", "true")]
    [InlineData("sender", "[]")]
    [InlineData("recipient", "{}")]
    [InlineData("sha256", "7")]
    [InlineData("metadata", "[1]")]
    [InlineData("metadata", "\"{}\"")]
    [InlineData("metadata", "5")]
    [InlineData("metadata", "true")]
    [InlineData("contacts", "{}")]
    [InlineData("contacts", "\"x\"")]
    public void A_field_of_the_wrong_type_or_shape_is_not_interpretable(string field, string rawJson)
    {
        WireTest.Unexpected(() => Read(Variants.With(Detail(), field, rawJson)));
    }

    [Fact]
    public void A_size_larger_than_int_is_read_as_a_64_bit_value()
    {
        Assert.Equal(3_000_000_000L, Read(Variants.With(Detail(), "size_bytes", "3000000000")).SizeBytes);
        Assert.Equal(long.MaxValue, Read(Variants.With(Detail(), "size_bytes", "9223372036854775807")).SizeBytes);
        Assert.Equal(0L, Read(Variants.With(Detail(), "size_bytes", "0")).SizeBytes);
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"doc_abc\"")]
    [InlineData("\"doc_01M3VEESG5KBYR5PYAJ0TDT4B2\"")] // il vecchio formato con prefisso e ULID
    [InlineData("1.5")]
    [InlineData("30017.0")]
    [InlineData("3.0017e4")]
    [InlineData("-30017")]
    [InlineData("-0")]
    [InlineData("0")]
    [InlineData("9223372036854775808")] // long.MaxValue + 1
    [InlineData("100000000000000000000")]
    [InlineData("null")] // null equivale ad assente: l'id e' obbligatorio
    public void An_invalid_or_empty_document_id_is_not_interpretable_and_the_empty_id_is_never_produced(string rawJson)
    {
        var exception = WireTest.Unexpected(() => Read(Variants.With(Detail(), "id", rawJson)));

        Assert.Contains("documento.id", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1", 1L)]
    [InlineData("2147483647", 2147483647L)]
    [InlineData("2147483648", 2147483648L)] // un documento e' un bigint: oltre int.MaxValue e' un id valido
    [InlineData("9007199254740992", 9007199254740992L)] // 2^53
    [InlineData("9007199254740993", 9007199254740993L)] // 2^53 + 1: passando da double diventerebbe ...992
    [InlineData("9223372036854775807", long.MaxValue)]
    public void A_document_id_is_a_64_bit_integer_read_without_going_through_double(string rawJson, long expected)
    {
        var document = Read(Variants.With(Detail(), "id", rawJson));

        Assert.Equal(expected, document.Id.Number);
        Assert.Equal(rawJson, document.Id.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-x")]
    [InlineData("con spazio")]
    public void An_invalid_folder_code_is_not_interpretable(string code)
    {
        WireTest.Unexpected(() => Read(Variants.With(Detail(), "folder_id", "\"" + code + "\"")));
    }

    [Fact]
    public void A_null_or_missing_optional_field_is_null()
    {
        var bare = Variants.Edit(Detail(), o =>
        {
            foreach (var name in new[] { "folder_id", "owner", "tag", "sender", "recipient" })
            {
                o[name] = null;
            }
        });
        var missing = Variants.Edit(Detail(), o =>
        {
            foreach (var name in new[] { "folder_id", "owner", "tag", "sender", "recipient", "sha256" })
            {
                o.Remove(name);
            }
        });

        foreach (var document in new[] { Read(bare), Read(missing) })
        {
            Assert.Null(document.FolderId);
            Assert.Null(document.Owner);
            Assert.Null(document.Tag);
            Assert.Null(document.Sender);
            Assert.Null(document.Recipient);
        }

        Assert.Null(Read(missing).Sha256);
    }

    [Fact]
    public void Texts_can_be_empty_non_ASCII_emoji_and_4096_bytes_long()
    {
        var document = Read(Variants.Edit(Detail(), o =>
        {
            o["original_filename"] = "";
            o["owner"] = "Societ\U000000E0 \U0001F600";
            o["tag"] = new string('t', 4096);
        }));

        Assert.Equal(string.Empty, document.OriginalFilename);
        Assert.Equal("Societ\U000000E0 \U0001F600", document.Owner);
        Assert.Equal(4096, document.Tag!.Length);
    }

    [Fact]
    public void An_unknown_extra_property_is_ignored()
    {
        var document = Read(Variants.With(Detail(), "campo_nuovo", "{\"a\":[1,2,3]}"));

        Assert.Equal(new DocumentId(FatturaId), document.Id);
    }

    // ----- has_content: ignorato -----

    [Fact]
    public void has_content_is_ignored_even_when_it_contradicts_the_hash_or_has_the_wrong_type()
    {
        // HasContent e' derivato da Sha256 (Domain): il campo del server si ignora e non si valida nemmeno.
        var falseWithHash = Read(Variants.With(Detail(), "has_content", "false"));
        var weird = Read(Variants.With(Detail(), "has_content", "\"x\""));
        var absent = Read(Variants.Without(Detail(), "has_content"));
        var trueWithoutHash = Read(Variants.Edit(Detail(), o =>
        {
            o["has_content"] = true;
            o.Remove("sha256");
        }));

        Assert.True(falseWithHash.HasContent);
        Assert.True(weird.HasContent);
        Assert.True(absent.HasContent);
        Assert.False(trueWithoutHash.HasContent);
        Assert.Null(trueWithoutHash.Sha256);
    }

    // ----- metadata: assente = {} -----

    [Fact]
    public void Missing_or_null_metadata_is_an_empty_object_and_never_default()
    {
        var missing = Read(Variants.Without(Detail(), "metadata"));
        var nulled = Read(Variants.With(Detail(), "metadata", "null"));

        foreach (var document in new[] { missing, nulled })
        {
            Assert.Equal(JsonValueKind.Object, document.Metadata.ValueKind);
            Assert.Equal("{}", document.Metadata.GetRawText());
            Assert.Null(ArxivarMetadata.From(document.Metadata));
        }
    }

    [Fact]
    public void The_empty_metadata_of_two_documents_stays_valid_after_everything_else_is_collected()
    {
        var first = Read(Variants.Without(Detail(), "metadata"));
        var second = Read(Variants.Without(Detail(), "metadata"));
        GC.Collect();

        Assert.Equal("{}", first.Metadata.GetRawText());
        Assert.Equal("{}", second.Metadata.GetRawText());
    }

    [Fact]
    public void Metadata_nested_up_to_the_depth_the_server_stores_is_read_and_copied()
    {
        var deep = string.Concat(Enumerable.Repeat("{\"a\":", 63)) + "1" + new string('}', 63);

        var document = Read(Variants.With(Detail(), "metadata", deep));

        Assert.Equal(deep, document.Metadata.GetRawText());
    }

    [Fact]
    public void Large_metadata_of_60_KiB_is_read_whole()
    {
        var big = "{\"k\":\"" + new string('m', 60 * 1024) + "\"}";

        var document = Read(Variants.With(Detail(), "metadata", big));

        Assert.Equal(big.Length, document.Metadata.GetRawText().Length);
    }

    [Fact]
    public void Metadata_with_unicode_and_numbers_keeps_its_text()
    {
        var document = Read(Variants.With(Detail(), "metadata", "{\"arxivar\":{\"docnumber\":61617,\"oggetto\":\"Verbale \U0001F600\",\"revisione\":3}}"));

        var arxivar = ArxivarMetadata.From(document.Metadata);

        Assert.Equal(61617, arxivar!.Docnumber);
        Assert.Equal("Verbale \U0001F600", arxivar.Subject);
        Assert.Equal(3, arxivar.Revision);
    }

    [Fact]
    public void Metadata_with_an_isolated_surrogate_escape_is_read_and_kept_as_the_server_stores_it()
    {
        // Il server conserva qualunque JSON valido per il suo parser, compreso "\ud800" in una stringa: il documento NON si rifiuta (una pagina con un
        // solo documento cosi' diventerebbe illeggibile, un "veleno" per tutto l'elenco) e ToString() del record non lancia.
        // LACUNA DICHIARATA (Domain, fuori da T4.2): ArxivarMetadata.From su questi metadati lancia InvalidOperationException (il suo GetString), contro
        // la sua promessa "non lancia mai"; il rimedio e' un try/catch li'. Qui si prova solo cio' che e' del livello wire.
        const string Backslash = "\\";
        var body = ReplaceMetadata(
            Variants.With(Detail(), "metadata", "{}"),
            WireTest.Utf8("{\"arxivar\":{\"docnumber\":61617,\"categoria\":\"a" + Backslash + "ud800\",\"oggetto\":\"ok\"}}"));

        var document = Read(body);

        Assert.Equal(JsonValueKind.Object, document.Metadata.ValueKind);
        Assert.Equal("{\"arxivar\":{\"docnumber\":61617,\"categoria\":\"a" + Backslash + "ud800\",\"oggetto\":\"ok\"}}", document.Metadata.GetRawText());
        _ = document.ToString();
        Assert.Equal(new DocumentId(FatturaId), document.Id);
    }

    [Fact]
    public void Metadata_with_invalid_UTF8_bytes_makes_the_whole_response_not_interpretable()
    {
        // Un JSON invalido non si consegna: GetRawText() e ToString() del documento lancerebbero piu' tardi (misurato).
        var invalid = System.Text.Encoding.ASCII.GetBytes("{\"k\":\"a").Concat(new byte[] { 0xFF, 0xFE }).Concat(System.Text.Encoding.ASCII.GetBytes("\"}")).ToArray();

        WireTest.Unexpected(() => Read(ReplaceMetadata(Variants.With(Detail(), "metadata", "{}"), invalid)));
    }

    // Sostituisce {} dei metadati con i byte indicati (senza passare da un parser: i byte non validi devono restare tali).
    private static byte[] ReplaceMetadata(byte[] body, byte[] metadata)
    {
        var marker = System.Text.Encoding.ASCII.GetBytes("\"metadata\":{}");
        var at = IndexOf(body, marker);
        Assert.True(at >= 0);
        var prefix = body.Take(at).Concat(System.Text.Encoding.ASCII.GetBytes("\"metadata\":"));
        return prefix.Concat(metadata).Concat(body.Skip(at + marker.Length)).ToArray();
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.Skip(i).Take(needle.Length).SequenceEqual(needle))
            {
                return i;
            }
        }

        return -1;
    }

    // ----- sha256 e documento senza contenuto -----

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("CC1BA284A9FE9CEFA40D4BD9DFB8D9E7FB395431AAF79478EFCA4E04DA6C9D7E")]
    [InlineData("cc1ba284a9fe9cefa40d4bd9dfb8d9e7fb395431aaf79478efca4e04da6c9d7")]
    [InlineData("cc1ba284a9fe9cefa40d4bd9dfb8d9e7fb395431aaf79478efca4e04da6c9d7ef")]
    [InlineData("zc1ba284a9fe9cefa40d4bd9dfb8d9e7fb395431aaf79478efca4e04da6c9d7e")]
    public void A_sha256_that_is_not_64_lowercase_hex_digits_is_not_interpretable(string hash)
    {
        // Senza questo controllo VerifiedContentStream lancerebbe piu' tardi un ArgumentException per un difetto del server.
        WireTest.Unexpected(() => Read(Variants.With(Detail(), "sha256", "\"" + hash + "\"")));
    }

    [Fact]
    public void The_derived_document_without_content_has_no_hash_size_zero_and_HasContent_false()
    {
        // DERIVATA (nessuna cattura): un documento importato da ARXivar con i soli metadati, come lo emette DocumentDto (null omessi).
        var document = Read(WireFixtures.Derived("doc-detail-without-content"));

        Assert.Equal(5000000001L, document.Id.Number); // oltre int.MaxValue: un documento e' un bigint
        Assert.Null(document.Sha256);
        Assert.Equal(0L, document.SizeBytes);
        Assert.False(document.HasContent);
        Assert.Equal(61617, ArxivarMetadata.From(document.Metadata)!.Docnumber);
        Assert.Equal(At("2026-09-21T08:30:00Z"), document.CreatedAt);
        Assert.Empty(document.Contacts);
    }

    // ----- contatti collegati (derivati) -----

    private static readonly ContactId Acme = new("7");
    private static readonly ContactId Beta = new("12");

    [Fact]
    public void The_derived_detail_with_contacts_has_them_in_order_with_their_roles_and_names()
    {
        // DERIVATA (nessuna cattura): contacts = [{role,id,name}] come DocumentContactDto del server.
        var document = Read(WireFixtures.Derived("doc-detail-with-contacts"));

        Assert.Equal(2, document.Contacts.Count);
        Assert.Equal(new DocumentContact(ContactRole.Sender, Acme, "Acme Srl"), document.Contacts[0]);
        Assert.Equal(new DocumentContact(ContactRole.Recipient, Beta, "Beta Spa"), document.Contacts[1]);
    }

    private static byte[] WithContacts() => WireFixtures.Derived("doc-detail-with-contacts");

    [Theory]
    [InlineData("\"sender\"", ContactRole.Sender)]
    [InlineData("\"recipient\"", ContactRole.Recipient)]
    [InlineData("\"cc\"", ContactRole.Unknown)]
    [InlineData("\"Sender\"", ContactRole.Unknown)]
    [InlineData("\"RECIPIENT\"", ContactRole.Unknown)]
    [InlineData("\"sender \"", ContactRole.Unknown)]
    [InlineData("\"\"", ContactRole.Unknown)]
    [InlineData("null", ContactRole.Unknown)]
    public void A_contact_role_is_compared_exactly_and_a_new_one_is_Unknown_never_an_error(string rawRole, ContactRole expected)
    {
        var body = Variants.Edit(WithContacts(), o => o["contacts"]!.AsArray()[0]!.AsObject()["role"] = System.Text.Json.Nodes.JsonNode.Parse(rawRole));

        Assert.Equal(expected, Read(body).Contacts[0].Role);
    }

    [Fact]
    public void A_missing_contact_role_is_Unknown()
    {
        var body = Variants.Edit(WithContacts(), o => o["contacts"]!.AsArray()[0]!.AsObject().Remove("role"));

        Assert.Equal(ContactRole.Unknown, Read(body).Contacts[0].Role);
    }

    [Theory]
    [InlineData("role", "5")]
    [InlineData("role", "true")]
    [InlineData("id", "\"7\"")] // l'id di un contatto e' un numero JSON
    [InlineData("id", "\"con_abc\"")]
    [InlineData("id", "\"\"")]
    [InlineData("id", "\"con_01M3VEF0K9Z8X7Y6W5V4T3S2R1\"")] // il vecchio formato con prefisso e ULID
    [InlineData("id", "7.5")]
    [InlineData("id", "-7")]
    [InlineData("id", "0")]
    [InlineData("id", "2147483648")] // oltre int.MaxValue: l'id di un contatto e' un int
    [InlineData("name", "5")]
    [InlineData("name", "null")]
    public void A_contact_with_a_wrong_field_is_not_interpretable(string field, string rawJson)
    {
        var body = Variants.Edit(WithContacts(), o => o["contacts"]!.AsArray()[1]!.AsObject()[field] = System.Text.Json.Nodes.JsonNode.Parse(rawJson));

        var exception = WireTest.Unexpected(() => Read(body));

        Assert.Contains("contacts[1]." + field, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("name")]
    public void A_contact_with_a_missing_required_field_is_not_interpretable(string field)
    {
        var body = Variants.Edit(WithContacts(), o => o["contacts"]!.AsArray()[0]!.AsObject().Remove(field));

        WireTest.Unexpected(() => Read(body));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("5")]
    [InlineData("\"x\"")]
    [InlineData("[]")]
    public void A_contacts_element_that_is_not_an_object_is_not_interpretable(string element)
    {
        var body = Variants.With(Detail(), "contacts", "[" + element + "]");

        WireTest.Unexpected(() => Read(body));
    }

    [Fact]
    public void Missing_null_or_empty_contacts_are_an_empty_list_and_never_null()
    {
        foreach (var body in new[] { Variants.Without(Detail(), "contacts"), Variants.With(Detail(), "contacts", "null"), Variants.With(Detail(), "contacts", "[]") })
        {
            var document = Read(body);

            Assert.NotNull(document.Contacts);
            Assert.Empty(document.Contacts);
        }
    }

    [Fact]
    public void A_contact_name_can_be_empty_and_unknown_contact_properties_are_ignored()
    {
        var body = Variants.Edit(WithContacts(), o =>
        {
            var first = o["contacts"]!.AsArray()[0]!.AsObject();
            first["name"] = "";
            first["ruolo_esteso"] = "x";
        });

        Assert.Equal(string.Empty, Read(body).Contacts[0].Name);
    }

    // ----- risposta di un caricamento -----

    [Fact]
    public void An_upload_response_needs_a_boolean_deduplicated()
    {
        var captured = WireFixtures.Captured("47-doc-upload");

        WireTest.Unexpected(() => ReadUpload(Variants.Without(captured, "deduplicated")), 201);
        WireTest.Unexpected(() => ReadUpload(Variants.With(captured, "deduplicated", "null")), 201);
        WireTest.Unexpected(() => ReadUpload(Variants.With(captured, "deduplicated", "\"true\"")), 201);
        WireTest.Unexpected(() => ReadUpload(Variants.With(captured, "deduplicated", "1")), 201);
        Assert.True(ReadUpload(Variants.With(captured, "deduplicated", "true")).Deduplicated);
    }

    [Fact]
    public void A_detail_does_not_need_deduplicated()
    {
        Assert.NotNull(Read(Detail()));
    }

    [Fact]
    public void An_error_in_an_upload_response_carries_the_201_status()
    {
        var exception = WireTest.Unexpected(() => ReadUpload(Variants.Without(WireFixtures.Captured("47-doc-upload"), "id")), 201);

        Assert.Equal(201, exception.StatusCode);
    }

    // ----- pagine: errori -----

    [Fact]
    public void A_bad_document_in_a_page_fails_the_page_and_names_the_element()
    {
        var body = Variants.EditItem(WireFixtures.Captured("70-docs-list-default"), 3, item => item["sha256"] = "tronco");

        var exception = WireTest.Unexpected(() => ReadPage(body));

        Assert.Contains("items[3].sha256", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_page_item_may_carry_contacts_when_the_server_sends_them()
    {
        var body = Variants.EditItem(WireFixtures.Captured("71-docs-list-limit1-page1"), 0, item => item["contacts"] = System.Text.Json.Nodes.JsonNode.Parse("[{\"role\":\"sender\",\"id\":7,\"name\":\"Acme Srl\"}]"));

        var page = ReadPage(body);

        Assert.Single(page.Items[0].Contacts);
    }

    // ----- spostamento in blocco -----

    [Fact]
    public void The_captured_bulk_move_results_are_read_as_counts()
    {
        // 133-docs-bulk-move: {"moved":2}; 134-docs-bulk-move-to-root: {"moved":1}
        Assert.Equal(2, DocumentWire.ReadMoved(WireFixtures.Captured("133-docs-bulk-move"), WireTest.Context()));
        Assert.Equal(1, DocumentWire.ReadMoved(WireFixtures.Captured("134-docs-bulk-move-to-root"), WireTest.Context()));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"moved\":null}")]
    [InlineData("{\"moved\":-1}")]
    [InlineData("{\"moved\":1.5}")]
    [InlineData("{\"moved\":\"2\"}")]
    [InlineData("{\"moved\":2147483648}")]
    [InlineData("{\"spostati\":2}")]
    [InlineData("[]")]
    [InlineData("2")]
    public void A_bulk_move_result_without_a_valid_count_is_not_interpretable(string json)
    {
        WireTest.Unexpected(() => DocumentWire.ReadMoved(WireTest.Utf8(json), WireTest.Context()));
    }

    [Fact]
    public void A_bulk_move_count_of_zero_and_of_int_max_are_valid()
    {
        Assert.Equal(0, DocumentWire.ReadMoved(WireTest.Utf8("{\"moved\":0}"), WireTest.Context()));
        Assert.Equal(int.MaxValue, DocumentWire.ReadMoved(WireTest.Utf8("{\"moved\":2147483647}"), WireTest.Context()));
    }
}
