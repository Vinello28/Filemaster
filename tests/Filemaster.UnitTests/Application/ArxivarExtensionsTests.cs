using System.Text.Json;
using Filemaster.Application;
using Filemaster.Domain;

namespace Filemaster.UnitTests.Application;

/// <summary>
/// <see cref="DocumentExtensions.GetArxivarMetadata"/> e <see cref="DocumentStoreExtensions.FindByArxivarDocnumberAsync"/>: il filtro che
/// si invia al server (testo JSON esatto, numero e non stringa), la lettura di tutte le pagine e la vita del <see cref="JsonDocument"/>
/// del filtro, che deve restare valido durante le chiamate e poi essere smaltito.
/// </summary>
public sealed class ArxivarExtensionsTests
{
    private const string ArxivarMetadata61617 =
        """{"arxivar":{"docnumber":61617,"categoria":"FATT","oggetto":"Fattura 12","stato":"VALIDO","numero":"0012","data_documento":"2026-09-21","protocollo":"P-7","anno":"2026","revisione":3,"impronta":"d41d8cd98f00b204e9800998ecf8427e"}}""";

    // --- GetArxivarMetadata -------------------------------------------------------------------------------------

    [Fact]
    public void GetArxivarMetadata_reads_the_arxivar_section_like_ArxivarMetadata_From()
    {
        var document = TestData.Doc(1, metadataJson: ArxivarMetadata61617);

        var metadata = document.GetArxivarMetadata();

        Assert.NotNull(metadata);
        Assert.Equal(61617, metadata.Docnumber);
        Assert.Equal("FATT", metadata.Category);
        Assert.Equal("Fattura 12", metadata.Subject);
        Assert.Equal("VALIDO", metadata.Status);
        Assert.Equal("0012", metadata.Number);
        Assert.Equal(new DateTime(2026, 9, 21), metadata.DocumentDate);
        Assert.Equal("P-7", metadata.Protocol);
        Assert.Equal("2026", metadata.Year);
        Assert.Equal(3, metadata.Revision);
        Assert.Equal("d41d8cd98f00b204e9800998ecf8427e", metadata.Fingerprint);
        Assert.Equal(ArxivarMetadata.From(document.Metadata), metadata);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"altro":{"docnumber":1}}""")]
    [InlineData("""{"arxivar":null}""")]
    [InlineData("""{"arxivar":[]}""")]
    [InlineData("""{"arxivar":{}}""")]
    [InlineData("""{"arxivar":{"docnumber":"61617"}}""")]
    [InlineData("""{"arxivar":{"oggetto":"senza numero"}}""")]
    public void GetArxivarMetadata_returns_null_when_the_document_has_no_valid_arxivar_section(string metadataJson)
    {
        var document = TestData.Doc(1, metadataJson: metadataJson);

        Assert.Null(document.GetArxivarMetadata());
    }

    [Fact]
    public void GetArxivarMetadata_returns_null_for_default_metadata()
    {
        var document = TestData.Doc(1, metadataJson: null);

        Assert.Null(document.GetArxivarMetadata());
    }

    [Fact]
    public void GetArxivarMetadata_with_a_null_document_throws_ArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => ((Document)null!).GetArxivarMetadata());

        Assert.Equal("document", exception.ParamName);
    }

    // --- FindByArxivarDocnumberAsync ----------------------------------------------------------------------------

    [Fact]
    public async Task The_filter_sent_to_the_store_is_exactly_the_arxivar_docnumber_as_a_JSON_number()
    {
        var store = new FakeDocumentStore();
        store.Pages.Add(null, new[] { TestData.Doc(1) }, null);
        var filters = new List<string>();
        store.OnList = query => filters.Add(query!.Metadata!.Value.GetRawText());

        await store.FindByArxivarDocnumberAsync(61617);

        Assert.Equal(new[] { """{"arxivar":{"docnumber":61617}}""" }, filters);
    }

    [Theory]
    [InlineData(1, """{"arxivar":{"docnumber":1}}""")]
    [InlineData(61617, """{"arxivar":{"docnumber":61617}}""")]
    [InlineData(int.MaxValue, """{"arxivar":{"docnumber":2147483647}}""")]
    public async Task The_filter_text_has_the_number_without_quotes_for_any_positive_value(int docnumber, string expectedFilter)
    {
        var store = new FakeDocumentStore();
        store.Pages.Add(null, Array.Empty<Document>(), null);
        JsonValueKind? kind = null;
        var filters = new List<string>();
        store.OnList = query =>
        {
            filters.Add(query!.Metadata!.Value.GetRawText());
            kind = query.Metadata.Value.GetProperty("arxivar").GetProperty("docnumber").ValueKind;
        };

        await store.FindByArxivarDocnumberAsync(docnumber);

        Assert.Equal(expectedFilter, Assert.Single(filters));
        Assert.Equal(JsonValueKind.Number, kind);
    }

    [Fact]
    public async Task The_query_has_only_the_metadata_filter_and_the_filter_passes_the_documents_validation()
    {
        var store = new FakeDocumentStore();
        store.Pages.Add(null, Array.Empty<Document>(), null);
        DocumentQuery? sent = null;
        store.OnList = query => sent = query;

        await store.FindByArxivarDocnumberAsync(61617);

        Assert.NotNull(sent);
        Assert.NotNull(sent.Metadata);
        Assert.Null(sent.FolderId);
        Assert.Null(sent.Owner);
        Assert.Null(sent.Tag);
        Assert.Null(sent.FileName);
        Assert.Null(sent.Text);
        Assert.Null(sent.MetadataText);
        Assert.Null(sent.CreatedFrom);
        Assert.Null(sent.CreatedBefore);
    }

    [Fact]
    public async Task All_the_pages_are_read_and_the_documents_come_back_in_the_server_order()
    {
        var store = new FakeDocumentStore();
        var first = TestData.Doc(1);
        var second = TestData.Doc(2);
        var third = TestData.Doc(3);
        store.Pages
            .Add(null, new[] { first, second }, "c1")
            .Add("c1", new[] { third }, null);

        var found = await store.FindByArxivarDocnumberAsync(61617);

        Assert.Equal(new[] { first, second, third }, found);
        Assert.Equal(2, store.Pages.Requests.Count);
        Assert.Equal(new PageRequest(null, PageRequest.MaxLimit), store.Pages.Requests[0]);
        Assert.Equal(new PageRequest("c1", PageRequest.MaxLimit), store.Pages.Requests[1]);
    }

    [Fact]
    public async Task No_document_gives_an_empty_list_and_not_null()
    {
        var store = new FakeDocumentStore();
        store.Pages.MaxCalls = 1;
        store.Pages.Add(null, Array.Empty<Document>(), null);

        var found = await store.FindByArxivarDocnumberAsync(61617);

        Assert.NotNull(found);
        Assert.Empty(found);
    }

    [Fact]
    public async Task Several_documents_with_the_same_docnumber_are_all_returned()
    {
        // Un documento importato dalla sincronizzazione e uno caricato via API con gli stessi metadati: stesso filtro, due risultati.
        var store = new FakeDocumentStore();
        var imported = TestData.Doc(1, metadataJson: ArxivarMetadata61617);
        var uploaded = TestData.Doc(2, metadataJson: ArxivarMetadata61617);
        store.Pages.MaxCalls = 1;
        store.Pages.Add(null, new[] { uploaded, imported }, null);

        var found = await store.FindByArxivarDocnumberAsync(61617);

        Assert.Equal(new[] { uploaded, imported }, found);
    }

    [Fact]
    public async Task The_filter_JsonDocument_is_alive_during_every_call_and_disposed_afterwards()
    {
        var store = new FakeDocumentStore();
        store.Pages.Add(null, new[] { TestData.Doc(1) }, "c1").Add("c1", new[] { TestData.Doc(2) }, "c2").Add("c2", new[] { TestData.Doc(3) }, null);
        var elements = new List<JsonElement>();
        var textsDuringCalls = new List<string>();
        store.OnList = query =>
        {
            elements.Add(query!.Metadata!.Value);

            // Se il documento del filtro fosse gia' smaltito, ogni lettura lancerebbe ObjectDisposedException.
            textsDuringCalls.Add(query.Metadata.Value.GetRawText());
        };

        await store.FindByArxivarDocnumberAsync(61617);

        Assert.Equal(3, textsDuringCalls.Count);
        Assert.All(textsDuringCalls, text => Assert.Equal("""{"arxivar":{"docnumber":61617}}""", text));
        Assert.All(elements, element => Assert.Throws<ObjectDisposedException>(() => element.GetRawText()));
    }

    [Fact]
    public async Task The_filter_JsonDocument_is_disposed_also_when_the_search_fails()
    {
        var store = new FakeDocumentStore();
        store.Pages.Add(null, new[] { TestData.Doc(1) }, "c1").Fail("c1", new InvalidOperationException("rete"));
        JsonElement element = default;
        store.OnList = query => element = query!.Metadata!.Value;

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.FindByArxivarDocnumberAsync(61617));

        Assert.Throws<ObjectDisposedException>(() => element.GetRawText());
    }

    [Fact]
    public async Task A_server_that_does_not_advance_the_cursor_stops_the_search_with_UnexpectedResponseException()
    {
        var store = new FakeDocumentStore();
        store.Pages.MaxCalls = 2;
        store.Pages.Add(null, new[] { TestData.Doc(1) }, "A").Add("A", new[] { TestData.Doc(2) }, "A");

        var exception = await Assert.ThrowsAsync<UnexpectedResponseException>(() => store.FindByArxivarDocnumberAsync(61617));

        Assert.Equal(200, exception.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public async Task A_docnumber_that_is_not_positive_is_rejected_without_calling_the_store(int docnumber)
    {
        var store = new FakeDocumentStore();

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.FindByArxivarDocnumberAsync(docnumber));

        Assert.Equal("docnumber", exception.ParamName);
        Assert.Empty(store.Pages.Requests);
    }

    [Fact]
    public async Task A_null_store_throws_ArgumentNullException()
    {
        var exception = await Assert.ThrowsAsync<ArgumentNullException>(() => ((IDocumentStore)null!).FindByArxivarDocnumberAsync(61617));

        Assert.Equal("store", exception.ParamName);
    }

    [Fact]
    public async Task The_token_reaches_every_request()
    {
        var store = new FakeDocumentStore();
        store.Pages.Add(null, new[] { TestData.Doc(1) }, "c1").Add("c1", new[] { TestData.Doc(2) }, null);
        using var cancellation = new CancellationTokenSource();

        await store.FindByArxivarDocnumberAsync(61617, cancellation.Token);

        Assert.Equal(2, store.Pages.Tokens.Count);
        Assert.All(store.Pages.Tokens, token => Assert.Equal(cancellation.Token, token));
    }

    [Fact]
    public async Task A_cancelled_token_stops_the_search_before_any_request()
    {
        var store = new FakeDocumentStore();
        store.Pages.Add(null, new[] { TestData.Doc(1) }, null);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.FindByArxivarDocnumberAsync(61617, cancellation.Token));

        Assert.Empty(store.Pages.Requests);
    }
}
