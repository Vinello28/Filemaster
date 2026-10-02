using Filemaster.Application;
using Filemaster.Domain;
using Filemaster.Infrastructure;

namespace Filemaster.UnitTests.Wire;

/// <summary>
/// Le pagine <c>{"items":[...],"next_cursor":"..."}</c> e gli elenchi non paginati <c>{"items":[...]}</c>, con un elemento fatto di un solo
/// testo (<c>n</c>) cosi' i test riguardano la pagina e non il documento (le pagine di documenti vere stanno in <c>DocumentWireTests</c>). Il
/// cursore e' opaco: passa com'e', anche con caratteri che andranno codificati; un cursore presente e vuoto e' non interpretabile.
/// </summary>
public sealed class PageWireTests
{
    private static Page<string> Read(string json) =>
        PageWire.ReadPage(WireTest.Utf8(json), WireTest.Context(), item => item.RequiredString("n"));

    private static IReadOnlyList<string> ReadItems(string json) =>
        PageWire.ReadItems(WireTest.Utf8(json), WireTest.Context(), item => item.RequiredString("n"));

    [Fact]
    public void A_page_with_a_cursor_has_its_items_in_order_and_the_cursor_as_it_is()
    {
        var page = Read("{\"items\":[{\"n\":\"a\"},{\"n\":\"b\"}],\"next_cursor\":\"djF8MTc5MDg0ODc1NjM1MDEzNnxkb2NfMDFNM1ZFRVRLWTdHOVFNWkhWNERQUTNRQ1Y\"}");

        Assert.Equal(new[] { "a", "b" }, page.Items);
        Assert.Equal("djF8MTc5MDg0ODc1NjM1MDEzNnxkb2NfMDFNM1ZFRVRLWTdHOVFNWkhWNERQUTNRQ1Y", page.NextCursor);
    }

    [Fact]
    public void The_last_page_has_no_cursor_whether_it_is_absent_or_null()
    {
        Assert.Null(Read("{\"items\":[{\"n\":\"a\"}]}").NextCursor);
        Assert.Null(Read("{\"items\":[{\"n\":\"a\"}],\"next_cursor\":null}").NextCursor);
    }

    [Fact]
    public void An_empty_page_is_valid_with_or_without_a_cursor()
    {
        Assert.Empty(Read("{\"items\":[]}").Items);
        var withCursor = Read("{\"items\":[],\"next_cursor\":\"abc\"}");
        Assert.Empty(withCursor.Items);
        Assert.Equal("abc", withCursor.NextCursor);
    }

    [Theory]
    [InlineData("a+b/c=d")]
    [InlineData("100%")]
    [InlineData("con spazio")]
    [InlineData("a&b=c?d#e")]
    [InlineData("perch\U000000E9 \U0001F600")]
    [InlineData(" iniziale")]
    [InlineData("x")]
    public void The_cursor_is_opaque_and_is_never_trimmed_decoded_or_interpreted(string cursor)
    {
        var page = Read("{\"items\":[],\"next_cursor\":\"" + cursor + "\"}");

        Assert.Equal(cursor, page.NextCursor);
    }

    [Fact]
    public void A_cursor_of_4096_characters_is_read_intact()
    {
        var cursor = new string('c', 4096);

        Assert.Equal(cursor, Read("{\"items\":[],\"next_cursor\":\"" + cursor + "\"}").NextCursor);
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\" \"")]
    [InlineData("\"   \"")]
    public void A_present_but_empty_cursor_is_not_interpretable_because_PageRequest_would_refuse_it(string cursor)
    {
        var exception = WireTest.Unexpected(() => Read("{\"items\":[],\"next_cursor\":" + cursor + "}"));

        Assert.Contains("next_cursor", exception.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => new PageRequest(cursor.Trim('"')));
    }

    [Theory]
    [InlineData("5")]
    [InlineData("true")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void A_cursor_of_the_wrong_type_is_not_interpretable(string cursor)
    {
        WireTest.Unexpected(() => Read("{\"items\":[],\"next_cursor\":" + cursor + "}"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"items\":null}")]
    [InlineData("{\"items\":{}}")]
    [InlineData("{\"items\":\"x\"}")]
    [InlineData("{\"next_cursor\":\"abc\"}")]
    public void A_page_without_a_valid_items_array_is_not_interpretable(string json)
    {
        WireTest.Unexpected(() => Read(json));
        WireTest.Unexpected(() => ReadItems(json));
    }

    [Theory]
    [InlineData("{\"items\":[null]}")]
    [InlineData("{\"items\":[1]}")]
    [InlineData("{\"items\":[\"a\"]}")]
    [InlineData("{\"items\":[[]]}")]
    [InlineData("{\"items\":[{\"n\":\"a\"},{}]}")]
    [InlineData("{\"items\":[{\"n\":5}]}")]
    public void A_page_with_a_bad_element_is_not_interpretable_as_a_whole(string json)
    {
        WireTest.Unexpected(() => Read(json));
        WireTest.Unexpected(() => ReadItems(json));
    }

    [Fact]
    public void A_page_that_is_not_an_object_is_not_interpretable()
    {
        WireTest.Unexpected(() => Read("[]"));
        WireTest.Unexpected(() => Read("null"));
        WireTest.Unexpected(() => ReadItems("[{\"n\":\"a\"}]"));
    }

    [Fact]
    public void A_page_of_500_elements_is_read_whole_and_unknown_properties_are_ignored()
    {
        var items = string.Join(",", Enumerable.Range(0, 500).Select(i => "{\"n\":\"v" + i + "\",\"extra\":" + i + "}"));

        var page = Read("{\"items\":[" + items + "],\"totale\":500,\"next_cursor\":\"x\"}");

        Assert.Equal(500, page.Items.Count);
        Assert.Equal("v0", page.Items[0]);
        Assert.Equal("v499", page.Items[499]);
    }

    [Fact]
    public void A_non_paginated_listing_ignores_a_next_cursor_even_an_empty_one()
    {
        Assert.Equal(new[] { "a" }, ReadItems("{\"items\":[{\"n\":\"a\"}],\"next_cursor\":\"\"}"));
        Assert.Empty(ReadItems("{\"items\":[]}"));
    }

    [Fact]
    public void The_page_items_are_a_list_that_cannot_be_null()
    {
        Assert.NotNull(Read("{\"items\":[]}").Items);
    }
}
