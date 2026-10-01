using Filemaster.Application;

namespace Filemaster.UnitTests.Application;

/// <summary>
/// <see cref="PageRequest"/> e <see cref="Page{T}"/>: il limite 1..200 e il cursore opaco del server. Un cursore vuoto non
/// e' la "prima pagina" (per il server lo sarebbe): chi scorre le pagine con un cursore vuoto per errore ripartirebbe
/// da capo all'infinito.
/// </summary>
public sealed class PagingTests
{
    // Un cursore reale del server dev (fixture 71): base64url, opaco per il client.
    private const string Cursor = "djF8MTc5MDg0ODc1NjM1MDEzNnxkb2NfMDFNM1ZFRVRLWTdHOVFNWkhWNERQUTNRQ1Y";

    [Fact]
    public void PageRequest_without_arguments_asks_for_the_first_page_with_the_server_default_limit()
    {
        var request = new PageRequest();

        Assert.Null(request.Cursor);
        Assert.Null(request.Limit);
    }

    [Fact]
    public void PageRequest_keeps_the_cursor_exactly_as_given()
    {
        var request = new PageRequest(Cursor);

        Assert.Equal(Cursor, request.Cursor);
        Assert.Null(request.Limit);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(200)]
    public void PageRequest_accepts_a_limit_from_1_to_200(int limit)
    {
        var request = new PageRequest(limit: limit);

        Assert.Equal(limit, request.Limit);
        Assert.Null(request.Cursor);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(201)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void PageRequest_with_a_limit_out_of_range_throws_ArgumentOutOfRangeException_for_limit(int limit)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => new PageRequest(limit: limit));

        Assert.Equal("limit", exception.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    public void PageRequest_with_an_empty_or_blank_cursor_throws_ArgumentException_for_cursor(string cursor)
    {
        var exception = Assert.Throws<ArgumentException>(() => new PageRequest(cursor));

        Assert.Equal("cursor", exception.ParamName);
    }

    [Fact]
    public void PageRequest_MaxLimit_is_the_server_maximum()
    {
        Assert.Equal(200, PageRequest.MaxLimit);
    }

    [Fact]
    public void PageRequest_has_value_equality()
    {
        Assert.Equal(new PageRequest(Cursor, 10), new PageRequest(Cursor, 10));
        Assert.NotEqual(new PageRequest(Cursor, 10), new PageRequest(Cursor, 11));
        Assert.NotEqual(new PageRequest(Cursor, 10), new PageRequest(null, 10));
    }

    [Fact]
    public void Page_keeps_the_items_and_the_next_cursor()
    {
        var items = new[] { "a", "b" };

        var page = new Page<string>(items, Cursor);

        Assert.Same(items, page.Items);
        Assert.Equal(Cursor, page.NextCursor);
    }

    [Fact]
    public void Page_without_a_next_cursor_is_the_last_page()
    {
        // Il server omette next_cursor sull'ultima pagina, anche se e' piena (fixture 73: limit uguale al totale).
        var page = new Page<string>(new[] { "a" }, null);

        Assert.Null(page.NextCursor);
    }

    [Fact]
    public void Page_with_null_items_has_an_empty_list_so_Items_is_never_null()
    {
        var page = new Page<string>(null!, null);

        Assert.NotNull(page.Items);
        Assert.Empty(page.Items);
    }

    [Fact]
    public void Two_pages_with_equal_items_are_not_equal_because_the_list_compares_by_reference()
    {
        // Come Document.Contacts: un IReadOnlyList in un record si confronta per riferimento.
        var a = new Page<string>(new[] { "x" }, null);
        var b = new Page<string>(new[] { "x" }, null);

        Assert.NotEqual(a, b);
        Assert.Equal(a.Items.Single(), b.Items.Single());
    }
}
