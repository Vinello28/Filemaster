using Filemaster.Application;
using Filemaster.Domain;
using Filemaster.Infrastructure;

namespace Filemaster.UnitTests.Wire;

/// <summary>
/// Il percorso dell'elenco dei contatti: <c>q</c>, <c>category_id</c> e <c>kind</c> (in quest'ordine), poi <c>limit</c> e <c>cursor</c>. Il genere
/// sul filo e' il nome del server in minuscolo (<c>external</c>, <c>user</c>, <c>group</c>), scritto a mano e mai derivato dal nome dell'enum;
/// <c>Unknown</c> non e' un genere e <c>Validate()</c> lo rifiuta prima di costruire.
/// </summary>
public sealed class ContactRequestWireTests
{
    private static string List(ContactQuery? query = null, PageRequest? page = null) => ContactWire.ListPath(query, page);

    [Fact]
    public void Without_filters_the_path_is_just_contacts_and_the_captured_text_filter_is_reproduced()
    {
        Assert.Equal("contacts", List());
        Assert.Equal("contacts", List(new ContactQuery(), new PageRequest()));
        Assert.Equal(WireFixtures.RequestPath("152-contacts-list-q").TrimStart('/'), List(new ContactQuery { Text = "acme" }));
    }

    [Theory]
    [InlineData(ContactKind.External, "contacts?kind=external")]
    [InlineData(ContactKind.User, "contacts?kind=user")]
    [InlineData(ContactKind.Group, "contacts?kind=group")]
    public void The_kind_is_the_lowercase_name_of_the_server(ContactKind kind, string expected)
    {
        Assert.Equal(expected, List(new ContactQuery { Kind = kind }));
    }

    [Fact]
    public void The_kind_is_not_the_name_of_the_client_enum()
    {
        foreach (var kind in new[] { ContactKind.External, ContactKind.User, ContactKind.Group })
        {
            var path = List(new ContactQuery { Kind = kind });

            Assert.DoesNotContain(kind.ToString(), path, StringComparison.Ordinal); // "External" con la maiuscola non va mai sul filo
            Assert.Equal(kind.ToString().ToLowerInvariant(), path.Remove(0, "contacts?kind=".Length)); // e' il nome minuscolo, scritto a mano nel codice
        }
    }

    [Fact]
    public void Every_filter_and_the_page_have_a_stable_order_and_the_exact_text()
    {
        var query = new ContactQuery { Kind = ContactKind.Group, CategoryId = "ARX-12", Text = "caff\U000000E8 & co" };

        var path = List(query, new PageRequest("a+b=", 200));

        Assert.Equal("contacts?q=caff%C3%A8%20%26%20co&category_id=ARX-12&kind=group&limit=200&cursor=a%2Bb%3D", path);
    }

    [Fact]
    public void Each_filter_alone_uses_its_canonical_name()
    {
        Assert.Equal("contacts?q=acme", List(new ContactQuery { Text = "acme" }));
        Assert.Equal("contacts?category_id=ASSOCIATI", List(new ContactQuery { CategoryId = "ASSOCIATI" }));
        Assert.Equal("contacts?limit=10", List(page: new PageRequest(limit: 10)));
        Assert.Equal("contacts?cursor=abc", List(page: new PageRequest("abc")));
    }

    [Fact]
    public void The_text_is_sent_as_it_is_and_an_empty_text_is_sent_empty_but_null_is_omitted()
    {
        Assert.Equal("contacts?q=%20acme%20", List(new ContactQuery { Text = " acme " }));
        Assert.Equal("contacts?q=", List(new ContactQuery { Text = string.Empty }));
        Assert.Equal("contacts", List(new ContactQuery { Text = null, CategoryId = null, Kind = null }));
    }

    [Fact]
    public void An_invalid_query_is_refused_before_anything_is_built()
    {
        var unknown = Assert.Throws<ArgumentException>(() => List(new ContactQuery { Kind = ContactKind.Unknown }));
        var outside = Assert.Throws<ArgumentException>(() => List(new ContactQuery { Kind = (ContactKind)99 }));
        var emptyCategory = Assert.Throws<ArgumentException>(() => List(new ContactQuery { CategoryId = string.Empty }));
        var badCategory = Assert.Throws<ArgumentException>(() => List(new ContactQuery { CategoryId = "con spazio" }));

        Assert.Equal("Kind", unknown.ParamName);
        Assert.Equal("Kind", outside.ParamName);
        Assert.Equal("CategoryId", emptyCategory.ParamName);
        Assert.Equal("CategoryId", badCategory.ParamName);
    }

    [Fact]
    public void A_text_with_a_lone_surrogate_is_refused_with_the_property_name()
    {
        var exception = Assert.Throws<ArgumentException>(() => List(new ContactQuery { Text = "a" + new string((char)0xD800, 1) }));

        Assert.Equal("Text", exception.ParamName);
    }

    [Fact]
    public void The_contact_route_and_the_categories_route_are_fixed()
    {
        Assert.Equal("contacts/con_01M3VEF0K9Z8X7Y6W5V4T3S2R1", Routes.Contact(new ContactId("con_01M3VEF0K9Z8X7Y6W5V4T3S2R1")));
        Assert.Equal("contact-categories", Routes.ContactCategories);
    }
}
