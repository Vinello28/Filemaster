using Filemaster.Application;
using Filemaster.Domain;
using Filemaster.UnitTests.Infrastructure.Documents;
using Filemaster.UnitTests.Wire;

namespace Filemaster.UnitTests.Infrastructure.Resources;

/// <summary>
/// L'adapter dell'anagrafica: percorsi e query esatti (catture 150, 151, 152), paginazione come i documenti, categorie come lista, lettura
/// delle risposte. <b>Le risposte con contatti e categorie sono le fixture DERIVATE dal codice del server</b> (<c>Wire/Fixtures/derived</c>):
/// il server di prova non aveva contatti, le catture hanno solo elenchi vuoti.
/// </summary>
public sealed class HttpContactDirectoryTests
{
    private const string Base = "https://filemaster.example.test";

    [Fact]
    public async Task List_without_arguments_is_a_GET_of_contacts_as_in_capture_150_and_reads_the_page()
    {
        using var rig = new ResourceRig();
        rig.Then(() => ResourceRig.Derived("contacts-page"));

        var page = await rig.Contacts.ListAsync();

        var sent = rig.Single;
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal(Base + "/contacts", sent.Uri.AbsoluteUri);
        Assert.Equal(WireFixtures.RequestPath("150-contacts-list"), sent.PathAndQuery);
        Assert.Equal("application/json", sent.Header("Accept"));
        Assert.Equal(TransportRig.Key, sent.Header("X-API-Key"));
        Assert.Null(sent.Body);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal("7", page.Items[0].Id.Value);
        Assert.Equal(12, page.Items[1].Id.Number);
        Assert.Equal("Acme Srl", page.Items[0].Name);
        Assert.Equal(ContactKind.External, page.Items[0].Kind);
        Assert.Equal(3, page.Items[0].DocumentsAsSender);
        Assert.Equal(0, page.Items[0].DocumentsAsRecipient);
        Assert.Equal(ContactKind.User, page.Items[1].Kind);
        Assert.Equal(12, page.Items[1].DocumentsAsRecipient);
        Assert.Equal("bjF8MTJ8TWFyaW8gUm9zc2k", page.NextCursor);
    }

    [Fact]
    public async Task List_with_text_sends_q_as_in_capture_152_and_reads_the_empty_page()
    {
        using var rig = new ResourceRig();
        rig.Then(() => FixtureReply.Json("152-contacts-list-q"));

        var page = await rig.Contacts.ListAsync(new ContactQuery { Text = "acme" });

        Assert.Equal(WireFixtures.RequestPath("152-contacts-list-q"), rig.Single.PathAndQuery);
        Assert.Empty(page.Items);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task List_with_every_filter_and_a_page_sends_them_in_the_wire_order()
    {
        using var rig = new ResourceRig();
        rig.Then(() => FixtureReply.Json("150-contacts-list"));

        await rig.Contacts.ListAsync(
            new ContactQuery { Text = "rossi", CategoryId = "ARX-12", Kind = ContactKind.Group },
            new PageRequest("abc", 10));

        Assert.Equal("/contacts?q=rossi&category_id=ARX-12&kind=group&limit=10&cursor=abc", rig.Single.PathAndQuery);
    }

    [Fact]
    public async Task The_next_cursor_of_a_page_is_sent_as_is_for_the_next_page()
    {
        using var rig = new ResourceRig();
        rig.Then(() => ResourceRig.Derived("contacts-page"));
        rig.Then(() => FixtureReply.Json("150-contacts-list"));

        var first = await rig.Contacts.ListAsync(page: new PageRequest(limit: 2));
        var last = await rig.Contacts.ListAsync(page: new PageRequest(first.NextCursor, 2));

        Assert.Equal("/contacts?limit=2", rig.Sent[0].PathAndQuery);
        Assert.Equal("/contacts?limit=2&cursor=" + first.NextCursor, rig.Sent[1].PathAndQuery);
        Assert.Null(last.NextCursor);
    }

    [Fact]
    public async Task A_cursor_that_the_server_rejects_is_InvalidRequestException()
    {
        using var rig = new ResourceRig();
        rig.Then(() => Reply.Problem(400, "validation-error", "cursore non valido"));

        var exception = await Assert.ThrowsAsync<InvalidRequestException>(() => rig.Contacts.ListAsync(page: new PageRequest("non-suo")));

        Assert.Equal("cursore non valido", exception.Detail);
        Assert.Single(rig.Sent);
    }

    [Fact]
    public async Task An_empty_next_cursor_is_UnexpectedResponseException()
    {
        using var rig = new ResourceRig();
        rig.Then(() => Reply.Json(200, "{\"items\":[],\"next_cursor\":\"\"}"));

        var exception = await Assert.ThrowsAsync<UnexpectedResponseException>(() => rig.Contacts.ListAsync());

        Assert.Equal(200, exception.StatusCode);
    }

    [Fact]
    public async Task Get_is_a_GET_of_the_contact_path_and_reads_the_contact_without_counters()
    {
        using var rig = new ResourceRig();
        rig.Then(() => ResourceRig.Derived("contact-detail"));

        var contact = await rig.Contacts.GetAsync(ResourceRig.Contact);

        var sent = rig.Single;
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal("/contacts/12", sent.PathAndQuery);
        Assert.Equal("application/json", sent.Header("Accept"));
        Assert.Null(sent.Body);
        Assert.Equal(ResourceRig.Contact, contact.Id);
        Assert.Equal("Mario Rossi", contact.Name);
        Assert.Equal(ContactKind.User, contact.Kind);
        Assert.Equal(WireTest.Utc(2026, 9, 29, 17, 42, 0), contact.CreatedAt);
        Assert.Null(contact.DocumentsAsSender);
        Assert.Null(contact.DocumentsAsRecipient);
    }

    [Fact]
    public async Task A_contact_that_does_not_exist_is_NotFoundException()
    {
        using var rig = new ResourceRig();
        rig.Then(() => Reply.Problem(404, "not-found", requestId: "srv-404"));

        var exception = await Assert.ThrowsAsync<NotFoundException>(() => rig.Contacts.GetAsync(ResourceRig.Contact));

        Assert.Equal("srv-404", exception.RequestId);
        Assert.Single(rig.Sent);
    }

    [Fact]
    public async Task ListCategories_is_a_GET_of_contact_categories_as_in_capture_151_and_reads_a_list()
    {
        using var rig = new ResourceRig();
        rig.Then(() => ResourceRig.Derived("contact-categories"));

        var categories = await rig.Contacts.ListCategoriesAsync();

        var sent = rig.Single;
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal(WireFixtures.RequestPath("151-contact-categories-list"), sent.PathAndQuery);
        Assert.Equal("application/json", sent.Header("Accept"));
        Assert.Equal(TransportRig.Key, sent.Header("X-API-Key"));
        Assert.Null(sent.Body);
        Assert.Equal(
            new[] { new ContactCategory("ARX-12", "Clienti ARXivar", 12), new ContactCategory("ASSOCIATI", "Associati", null) },
            categories);
    }

    [Fact]
    public async Task The_real_category_list_of_capture_151_has_the_seeded_category()
    {
        using var rig = new ResourceRig();
        rig.Then(() => FixtureReply.Json("151-contact-categories-list"));

        var category = Assert.Single(await rig.Contacts.ListCategoriesAsync());

        Assert.Equal(new ContactCategory("E2E-FORNITORI", "Fornitori E2E", 9001), category);
    }

    [Fact]
    public async Task No_categories_is_an_empty_list_and_not_an_error()
    {
        using var rig = new ResourceRig();
        rig.Then(() => Reply.Json(200, "{\"items\":[]}"));

        Assert.Empty(await rig.Contacts.ListCategoriesAsync());
    }

    [Fact]
    public async Task An_unreadable_category_list_is_UnexpectedResponseException()
    {
        using var rig = new ResourceRig();
        rig.Then(() => Reply.Json(200, "{\"items\":[{\"name\":\"senza id\"}]}"));

        var exception = await Assert.ThrowsAsync<UnexpectedResponseException>(() => rig.Contacts.ListCategoriesAsync());

        Assert.Equal(200, exception.StatusCode);
    }
}
