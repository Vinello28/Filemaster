using Filemaster.Application;
using Filemaster.Domain;
using static Filemaster.IntegrationTests.Live.LiveServer;

namespace Filemaster.IntegrationTests.Live;

/// <summary>
/// Anagrafica (in sola lettura) e documento senza contenuto: i dati li semina <c>eng/e2e/seed.sh</c> via SQL (valori in
/// <see cref="LiveSeed"/>), perche' le API non sanno crearli. Questi test non creano e non cancellano niente.
/// </summary>
[Trait("Category", "Live")]
public sealed class LiveContactTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_seeded_category_is_listed()
    {
        using var client = Client(ReadKey);

        var categories = await client.Contacts.ListCategoriesAsync(Ct);

        var category = Assert.Single(categories, c => c.Id == LiveSeed.CategoryId);
        Assert.Equal(LiveSeed.CategoryName, category.Name);
        Assert.Equal(LiveSeed.CategoryArxivarId, category.ArxivarId);
    }

    [Fact]
    public async Task Contacts_are_listed_filtered_enumerated_one_per_page_and_read_one_by_one()
    {
        using var client = Client(ReadKey);
        var query = new ContactQuery { Text = LiveSeed.SearchText };

        var page = await client.Contacts.ListAsync(query, cancellationToken: Ct);
        Assert.Equal(
            new[] { LiveSeed.ExternalCode, LiveSeed.GroupCode, LiveSeed.UserCode },
            page.Items.Select(c => c.Code!).OrderBy(c => c, StringComparer.Ordinal));

        var external = Assert.Single(page.Items, c => c.Code == LiveSeed.ExternalCode);
        Assert.Equal(LiveSeed.ExternalName, external.Name);
        Assert.Equal(ContactKind.External, external.Kind);
        Assert.Equal(LiveSeed.CategoryId, external.CategoryId);
        Assert.Equal(LiveSeed.ExternalArxivarId, external.ArxivarId);
        Assert.Equal("Torino", external.City);
        Assert.Equal("TO", external.Province);
        Assert.Equal("01234567890", external.VatNumber);
        Assert.Null(external.Fax);
        // I contatori ci sono solo negli elenchi: il documento seminato ha il contatto esterno come mittente.
        Assert.Equal(1, external.DocumentsAsSender);
        Assert.Equal(0, external.DocumentsAsRecipient);
        var user = Assert.Single(page.Items, c => c.Code == LiveSeed.UserCode);
        Assert.Equal(ContactKind.User, user.Kind);
        Assert.Equal(LiveSeed.UserArxivarId, user.ArxivarId);
        Assert.Equal(1, user.DocumentsAsRecipient);
        var group = Assert.Single(page.Items, c => c.Code == LiveSeed.GroupCode);
        Assert.Equal(ContactKind.Group, group.Kind);
        Assert.Null(group.ArxivarId);

        var byCategory = await client.Contacts.ListAsync(new ContactQuery { CategoryId = LiveSeed.CategoryId }, cancellationToken: Ct);
        Assert.Equal(external.Id, Assert.Single(byCategory.Items).Id);
        var byKind = await client.Contacts.ListAsync(new ContactQuery { Text = LiveSeed.SearchText, Kind = ContactKind.Group }, cancellationToken: Ct);
        Assert.Equal(group.Id, Assert.Single(byKind.Items).Id);

        var first = await client.Contacts.ListAsync(query, new PageRequest(limit: 1), Ct);
        Assert.Single(first.Items);
        Assert.NotNull(first.NextCursor);

        var enumerated = new List<Contact>();
        await foreach (var contact in client.Contacts.EnumerateAsync(query, 1, Ct))
        {
            enumerated.Add(contact);
        }

        Assert.Equal(
            page.Items.Select(c => c.Id.Number).OrderBy(id => id),
            enumerated.Select(c => c.Id.Number).OrderBy(id => id));

        var detail = await client.Contacts.GetAsync(external.Id, Ct);
        Assert.Equal(external.Id, detail.Id);
        Assert.Equal(LiveSeed.ExternalName, detail.Name);
        Assert.Equal("fornitore.e2e@pec.example.com", detail.Pec);
        Assert.Equal(external.CreatedAt, detail.CreatedAt);
        Assert.Null(detail.DocumentsAsSender);
        Assert.Null(detail.DocumentsAsRecipient);
    }

    [Fact]
    public async Task The_seeded_document_without_content_carries_its_contacts_and_refuses_content_and_verify()
    {
        using var client = Client(ReadKey);
        var contacts = await client.Contacts.ListAsync(new ContactQuery { Text = LiveSeed.SearchText }, cancellationToken: Ct);
        var external = Assert.Single(contacts.Items, c => c.Code == LiveSeed.ExternalCode);
        var user = Assert.Single(contacts.Items, c => c.Code == LiveSeed.UserCode);
        var group = Assert.Single(contacts.Items, c => c.Code == LiveSeed.GroupCode);

        var listed = await client.Documents.ListAsync(new DocumentQuery { Owner = LiveSeed.DocumentOwner }, cancellationToken: Ct);
        var seeded = Assert.Single(listed.Items);
        Assert.Equal(LiveSeed.DocumentFileName, seeded.OriginalFilename);
        Assert.False(seeded.HasContent);
        Assert.Null(seeded.Sha256);
        Assert.Equal(0, seeded.SizeBytes);
        Assert.Empty(seeded.Contacts); // gli elenchi non portano i contatti, il dettaglio si'

        var document = await client.Documents.GetAsync(seeded.Id, Ct);
        Assert.Equal(3, document.Contacts.Count);
        var sender = Assert.Single(document.Contacts, c => c.Role == ContactRole.Sender);
        Assert.Equal(external.Id, sender.ContactId);
        Assert.Equal(LiveSeed.ExternalName, sender.Name);
        Assert.Equal(
            new[] { group.Id.Number, user.Id.Number }.OrderBy(id => id),
            document.Contacts.Where(c => c.Role == ContactRole.Recipient).Select(c => c.ContactId.Number).OrderBy(id => id));

        var bySender = await client.Documents.ListAsync(new DocumentQuery { SenderId = external.Id }, cancellationToken: Ct);
        Assert.Contains(bySender.Items, d => d.Id == seeded.Id);
        var byRecipient = await client.Documents.ListAsync(new DocumentQuery { RecipientId = group.Id }, cancellationToken: Ct);
        Assert.Contains(byRecipient.Items, d => d.Id == seeded.Id);
        var notSender = await client.Documents.ListAsync(new DocumentQuery { SenderId = user.Id }, cancellationToken: Ct);
        Assert.DoesNotContain(notSender.Items, d => d.Id == seeded.Id);

        var content = await Assert.ThrowsAsync<ContentUnavailableException>(() => client.Documents.OpenContentAsync(seeded.Id, cancellationToken: Ct));
        Assert.Equal(409, content.StatusCode);
        Assert.Equal("content-unavailable", content.ProblemType);
        await Assert.ThrowsAsync<ContentUnavailableException>(() => client.Documents.VerifyAsync(seeded.Id, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => client.Documents.OpenVerifiedContentAsync(seeded, Ct));

        var bulk = await client.Documents.VerifyManyAsync(new[] { seeded.Id }, Ct);
        Assert.Equal(1, bulk.Total);
        Assert.Equal(0, bulk.Verified);
        Assert.Equal(0, bulk.Failed);
        Assert.Equal(1, bulk.WithoutContent);
    }
}
