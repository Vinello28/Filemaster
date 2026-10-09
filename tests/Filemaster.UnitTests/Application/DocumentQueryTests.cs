using System.Text;
using System.Text.Json;
using Filemaster.Application;
using Filemaster.Domain;

namespace Filemaster.UnitTests.Application;

/// <summary>
/// <see cref="DocumentQuery"/>: i filtri di <c>GET /documents</c> del server, tutti e soli. Qui si prova la validazione:
/// id e codici vuoti, l'intervallo di date (<c>created_from</c> deve precedere <c>created_to</c>) e il filtro sui
/// metadati con i limiti esatti del server (oggetto JSON, 64 KiB, al massimo 64 valori e 16 livelli).
/// </summary>
public sealed class DocumentQueryTests
{
    private const string ContactIdText = "7001";

    private static JsonElement Json(string text)
    {
        // Mai smaltito: l'elemento vive per la durata del test.
        return JsonDocument.Parse(text).RootElement;
    }

    // {"p0":0,"p1":1,...}: un oggetto con "properties" valori semplici, cioe' properties + 1 valori in tutto (radice inclusa).
    private static JsonElement ObjectWithProperties(int properties)
    {
        var builder = new StringBuilder("{");
        for (var i = 0; i < properties; i++)
        {
            builder.Append(i == 0 ? string.Empty : ",").Append("\"p").Append(i).Append("\":").Append(i);
        }

        return Json(builder.Append('}').ToString());
    }

    // {"a":{"a":{...{}}}} con l'oggetto piu' interno a profondita' "depth" (la radice e' a profondita' 1).
    private static JsonElement Nested(int depth)
    {
        var text = new StringBuilder();
        for (var i = 1; i < depth; i++)
        {
            text.Append("{\"a\":");
        }

        text.Append("{}").Append('}', depth - 1);
        return Json(text.ToString());
    }

    private static void AssertRejects(DocumentQuery query, string paramName)
    {
        var exception = Assert.Throws<ArgumentException>(query.Validate);

        Assert.Equal(paramName, exception.ParamName);
    }

    [Fact]
    public void A_new_query_has_no_filter_at_all_and_is_valid()
    {
        var query = new DocumentQuery();

        Assert.Null(query.FolderId);
        Assert.Null(query.Owner);
        Assert.Null(query.Tag);
        Assert.Null(query.FileName);
        Assert.Null(query.Sender);
        Assert.Null(query.Recipient);
        Assert.Null(query.SenderId);
        Assert.Null(query.RecipientId);
        Assert.Null(query.Text);
        Assert.Null(query.MetadataText);
        Assert.Null(query.Metadata);
        Assert.Null(query.CreatedFrom);
        Assert.Null(query.CreatedBefore);
        query.Validate();
    }

    [Fact]
    public void Validate_accepts_a_query_with_every_filter_set()
    {
        var query = new DocumentQuery();
        query.FolderId = new FolderCode("FATTURE");
        query.Owner = "gabriele";
        query.Tag = "fattura";
        query.FileName = "fattura";
        query.Sender = "Acme";
        query.Recipient = "Beta";
        query.SenderId = new ContactId(ContactIdText);
        query.RecipientId = new ContactId("7002");
        query.Text = "acme";
        query.MetadataText = "X";
        query.Metadata = Json("""{"arxivar":{"docnumber":12345}}""");
        query.CreatedFrom = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        query.CreatedBefore = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);

        query.Validate();
    }

    [Fact]
    public void Validate_accepts_any_text_for_the_free_text_filters_because_the_server_has_no_limit_on_them()
    {
        // Il server trimma e ignora un filtro vuoto; l'unico tetto e' la riga di richiesta del server web, che il client non
        // puo' conoscere. Nessuna eccezione qui: vuoto, spazi, lungo, caratteri speciali di LIKE.
        var query = new DocumentQuery();
        query.Owner = string.Empty;
        query.Tag = "   ";
        query.FileName = "100%_[x]\\";
        query.Sender = new string('a', 10_000);
        query.Recipient = "perche' e'";
        query.Text = string.Empty;
        query.MetadataText = "{\"k\":";

        query.Validate();
    }

    // --- id e codici vuoti ---

    [Fact]
    public void Validate_with_the_empty_default_folder_code_throws_ArgumentException_for_FolderId()
    {
        var query = new DocumentQuery();
        query.FolderId = default(FolderCode);

        AssertRejects(query, nameof(DocumentQuery.FolderId));
    }

    [Fact]
    public void Validate_with_the_empty_default_sender_id_throws_ArgumentException_for_SenderId()
    {
        var query = new DocumentQuery();
        query.SenderId = default(ContactId);

        AssertRejects(query, nameof(DocumentQuery.SenderId));
    }

    [Fact]
    public void Validate_with_the_empty_default_recipient_id_throws_ArgumentException_for_RecipientId()
    {
        var query = new DocumentQuery();
        query.RecipientId = default(ContactId);

        AssertRejects(query, nameof(DocumentQuery.RecipientId));
    }

    // --- Date ---

    [Fact]
    public void Validate_accepts_each_bound_alone()
    {
        var onlyFrom = new DocumentQuery();
        onlyFrom.CreatedFrom = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        onlyFrom.Validate();

        var onlyBefore = new DocumentQuery();
        onlyBefore.CreatedBefore = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        onlyBefore.Validate();
    }

    [Fact]
    public void Validate_with_created_from_equal_to_created_before_throws_ArgumentException_for_CreatedBefore()
    {
        // Il server risponde 400 "created_from deve precedere created_to" quando start >= end.
        var instant = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var query = new DocumentQuery();
        query.CreatedFrom = instant;
        query.CreatedBefore = instant;

        AssertRejects(query, nameof(DocumentQuery.CreatedBefore));
    }

    [Fact]
    public void Validate_with_created_from_after_created_before_throws_ArgumentException_for_CreatedBefore()
    {
        var query = new DocumentQuery();
        query.CreatedFrom = new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero);
        query.CreatedBefore = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        AssertRejects(query, nameof(DocumentQuery.CreatedBefore));
    }

    [Fact]
    public void Validate_compares_the_instants_in_UTC_whatever_the_offsets()
    {
        // 2026-06-01T02:00+02:00 e 2026-06-01T00:00Z sono lo stesso istante: l'intervallo e' vuoto.
        var query = new DocumentQuery();
        query.CreatedFrom = new DateTimeOffset(2026, 6, 1, 2, 0, 0, TimeSpan.FromHours(2));
        query.CreatedBefore = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        AssertRejects(query, nameof(DocumentQuery.CreatedBefore));

        // Un'ora piu' tardi (in UTC) e' un intervallo valido anche se il numero sull'orologio e' piu' piccolo.
        query.CreatedBefore = new DateTimeOffset(2026, 6, 1, 1, 0, 0, TimeSpan.Zero);
        query.Validate();
    }

    // --- Filtro sui metadati ---

    [Fact]
    public void Validate_accepts_an_empty_metadata_filter_object()
    {
        // {} contiene tutto: il server lo accetta (e non filtra nulla).
        var query = new DocumentQuery();
        query.Metadata = Json("{}");

        query.Validate();
    }

    [Theory]
    [InlineData("[1]")]
    [InlineData("\"testo\"")]
    [InlineData("7")]
    [InlineData("false")]
    [InlineData("null")]
    public void Validate_with_a_metadata_filter_that_is_not_a_JSON_object_throws_ArgumentException_for_Metadata(string json)
    {
        // Fixture 96: metadata=[1] e' 400 sul server.
        var query = new DocumentQuery();
        query.Metadata = Json(json);

        AssertRejects(query, nameof(DocumentQuery.Metadata));
    }

    [Fact]
    public void Validate_with_default_JsonElement_metadata_throws_ArgumentException_not_InvalidOperationException()
    {
        var query = new DocumentQuery();
        query.Metadata = default(JsonElement);

        AssertRejects(query, nameof(DocumentQuery.Metadata));
    }

    [Fact]
    public void Validate_accepts_a_metadata_filter_of_64_values_and_rejects_65()
    {
        // Radice + 63 valori = 64 nodi (il limite del server); 64 valori = 65 nodi.
        var fits = new DocumentQuery();
        fits.Metadata = ObjectWithProperties(63);
        fits.Validate();

        var tooMany = new DocumentQuery();
        tooMany.Metadata = ObjectWithProperties(64);
        AssertRejects(tooMany, nameof(DocumentQuery.Metadata));
    }

    [Fact]
    public void Validate_counts_the_values_inside_arrays_and_nested_objects()
    {
        // Un array di 64 elementi dentro un oggetto: radice + array + 64 = 66 nodi.
        var items = string.Join(",", Enumerable.Repeat("1", 64));
        var query = new DocumentQuery();
        query.Metadata = Json("{\"k\":[" + items + "]}");

        AssertRejects(query, nameof(DocumentQuery.Metadata));

        // Radice + array + 62 = 64 nodi: al limite.
        var fits = new DocumentQuery();
        fits.Metadata = Json("{\"k\":[" + string.Join(",", Enumerable.Repeat("1", 62)) + "]}");
        fits.Validate();
    }

    [Fact]
    public void Validate_accepts_a_metadata_filter_nested_16_levels_and_rejects_17()
    {
        var fits = new DocumentQuery();
        fits.Metadata = Nested(16);
        fits.Validate();

        var tooDeep = new DocumentQuery();
        tooDeep.Metadata = Nested(17);
        AssertRejects(tooDeep, nameof(DocumentQuery.Metadata));
    }

    [Fact]
    public void Validate_measures_the_metadata_filter_in_UTF8_bytes()
    {
        // Pochi valori ma 30000 simboli dell'euro: 90000 byte, oltre i 64 KiB.
        var query = new DocumentQuery();
        query.Metadata = Json("{\"k\":\"" + new string('\U000020AC', 30000) + "\"}");

        AssertRejects(query, nameof(DocumentQuery.Metadata));
    }

    [Fact]
    public void Validate_accepts_a_metadata_filter_of_exactly_65536_bytes_and_rejects_65537()
    {
        const string Frame = "{\"k\":\"\"}";
        var fits = new DocumentQuery();
        fits.Metadata = Json("{\"k\":\"" + new string('a', 65536 - Frame.Length) + "\"}");
        fits.Validate();

        var tooBig = new DocumentQuery();
        tooBig.Metadata = Json("{\"k\":\"" + new string('a', 65537 - Frame.Length) + "\"}");
        AssertRejects(tooBig, nameof(DocumentQuery.Metadata));
    }
}
