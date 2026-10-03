using System.Text.Json;
using Filemaster.Application;
using Filemaster.Domain;
using Filemaster.Infrastructure;

namespace Filemaster.UnitTests.Wire;

/// <summary>
/// Le catture vere di anagrafica e documenti senza contenuto (T6.3, 2026-10-03: fixture 301-304, dati seminati da <c>eng/e2e/seed.sh</c> su un
/// server <c>dev</c> 8aec8bb). Due controlli: i lettori del client leggono il corpo vero, e ogni nome di campo che il server manda esiste anche
/// nella fixture DERIVATA corrispondente, che resta l'oracolo dei test di dettaglio (ha piu' campi valorizzati della cattura). Se il server
/// rinomina o aggiunge un campo, questi test lo dicono prima dei test di dettaglio, che continuerebbero a passare sulla fixture scritta a mano.
/// </summary>
public sealed class CapturedContactsTests
{
    private static readonly ContactId Supplier = new("con_01M40AM8YZQ7RVR7VDFNGZBNR9");
    private static readonly ContactId Group = new("con_01M40AM900GRTAVXQ0TKBJG58N");
    private static readonly ContactId User = new("con_01M40AM8ZGBYFRXPPEVXSTQPRJ");

    // "Fornitore E2E Citta' Srl" con la a accentata: il server la manda come UTF-8 grezzo, non come escape.
    private const string SupplierName = "Fornitore E2E Citt\U000000E0 Srl";

    [Fact]
    public void The_captured_contact_page_is_read_with_both_kinds_the_counters_and_the_cursor()
    {
        var page = ContactWire.ReadPage(WireFixtures.Captured("301-contacts-page"), WireTest.Context());

        Assert.Equal(2, page.Items.Count);
        var supplier = page.Items[0];
        Assert.Equal(Supplier, supplier.Id);
        Assert.Equal(SupplierName, supplier.Name);
        Assert.Equal(ContactKind.External, supplier.Kind);
        Assert.Equal("E2E-FORNITORI", supplier.CategoryId);
        Assert.Equal("E2E-EXT", supplier.Code);
        Assert.Equal("user@example.test", supplier.Email);
        Assert.Equal(9101, supplier.ArxivarId);
        Assert.Equal(new DateTimeOffset(2026, 10, 3, 7, 28, 33, TimeSpan.Zero).AddTicks(60150), supplier.CreatedAt);
        Assert.Equal(1, supplier.DocumentsAsSender);
        Assert.Equal(0, supplier.DocumentsAsRecipient);
        Assert.Null(supplier.Fax);

        var group = page.Items[1];
        Assert.Equal(Group, group.Id);
        Assert.Equal(ContactKind.Group, group.Kind);
        Assert.Null(group.CategoryId);
        Assert.Equal(1, group.DocumentsAsRecipient);
        Assert.NotNull(page.NextCursor);
    }

    [Fact]
    public void The_captured_contact_detail_has_no_counters()
    {
        var contact = ContactWire.ReadContact(WireFixtures.Captured("302-contact-detail"), WireTest.Context());

        Assert.Equal(Supplier, contact.Id);
        Assert.Equal(SupplierName, contact.Name);
        Assert.Null(contact.DocumentsAsSender);
        Assert.Null(contact.DocumentsAsRecipient);
    }

    [Fact]
    public void The_captured_categories_are_read_with_the_arxivar_id()
    {
        var category = Assert.Single(ContactWire.ReadCategories(WireFixtures.Captured("303-contact-categories"), WireTest.Context()));

        Assert.Equal("E2E-FORNITORI", category.Id);
        Assert.Equal("Fornitori E2E", category.Name);
        Assert.Equal(9001, category.ArxivarId);
    }

    [Fact]
    public void The_captured_document_without_content_has_no_hash_and_lists_its_contacts_in_order()
    {
        var document = DocumentWire.ReadDocument(WireFixtures.Captured("304-doc-detail-without-content-with-contacts"), WireTest.Context());

        Assert.False(document.HasContent);
        Assert.Null(document.Sha256);
        Assert.Equal(0, document.SizeBytes);
        Assert.Null(document.FolderId);
        Assert.Equal(
            new[] { (ContactRole.Sender, Supplier), (ContactRole.Recipient, Group), (ContactRole.Recipient, User) },
            document.Contacts.Select(c => (c.Role, c.ContactId)).ToArray());
        Assert.Equal(SupplierName, document.Contacts[0].Name);
    }

    public static TheoryData<string, string, string> CapturedAndDerived => new()
    {
        { "301-contacts-page", "contacts-page", "" },
        // Il dettaglio derivato e' un contatto minimo: l'oracolo dei nomi e' il contatto completo della pagina derivata.
        { "302-contact-detail", "contacts-page", "items[]." },
        { "303-contact-categories", "contact-categories", "" },
        { "304-doc-detail-without-content-with-contacts", "doc-detail-with-contacts", "" },
    };

    [Theory]
    [MemberData(nameof(CapturedAndDerived))]
    public void Every_field_the_server_sends_exists_in_the_derived_fixture(string captured, string derived, string derivedPrefix)
    {
        var real = FieldPaths(WireFixtures.Captured(captured));
        var handWritten = new HashSet<string>(
            FieldPaths(WireFixtures.Derived(derived))
                .Where(path => path.StartsWith(derivedPrefix, StringComparison.Ordinal))
                .Select(path => path.Substring(derivedPrefix.Length)),
            StringComparer.Ordinal);

        // "metadata" e' libero: i suoi campi interni non sono contratto.
        Assert.DoesNotContain(real, path => !path.StartsWith("metadata.", StringComparison.Ordinal) && !handWritten.Contains(path));
    }

    // Percorsi dei campi, con gli elementi degli array fusi ("items[].name", "contacts[].role").
    private static HashSet<string> FieldPaths(byte[] body)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(body);
        Collect(document.RootElement, string.Empty, paths);
        return paths;
    }

    private static void Collect(JsonElement element, string prefix, HashSet<string> paths)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var path = prefix + property.Name;
                paths.Add(path);
                Collect(property.Value, path + ".", paths);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                Collect(item, prefix.TrimEnd('.') + "[].", paths);
            }
        }
    }
}
