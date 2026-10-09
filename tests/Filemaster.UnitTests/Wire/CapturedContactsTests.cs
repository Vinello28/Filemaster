using System.Text.Json;
using Filemaster.Application;
using Filemaster.Domain;
using Filemaster.Infrastructure;

namespace Filemaster.UnitTests.Wire;

/// <summary>
/// Le catture vere dell'anagrafica (t64, 2026-10-09, Sharp-a-File <c>master</c> 541f378: fixture 150, 151, 258 e 259, dati seminati da
/// <c>eng/e2e/seed.sh</c>) e il documento senza contenuto con tre contatti (fixture DERIVATA dalla cattura 265 piu' i contatti dei dati seminati: il
/// dettaglio di quel documento non e' stato catturato). Due controlli: i lettori del client leggono il corpo vero, e ogni nome di campo che il
/// server manda esiste anche nella fixture DERIVATA corrispondente, che resta l'oracolo dei test di dettaglio (ha piu' campi valorizzati della
/// cattura). Se il server rinomina o aggiunge un campo, questi test lo dicono prima dei test di dettaglio, che continuerebbero a passare sulla
/// fixture scritta a mano. Gli id dei contatti sono NUMERI JSON (1, 2, 3), non piu' testi con prefisso.
/// </summary>
public sealed class CapturedContactsTests
{
    private static readonly ContactId Supplier = new("1");
    private static readonly ContactId Group = new("3");
    private static readonly ContactId User = new("2");

    // "Fornitore E2E Citta' Srl" con la a accentata: il server la manda come UTF-8 grezzo, non come escape.
    private const string SupplierName = "Fornitore E2E Citt\U000000E0 Srl";

    [Fact]
    public void The_captured_first_contact_page_has_the_full_supplier_the_counters_and_the_cursor()
    {
        var page = ContactWire.ReadPage(WireFixtures.Captured("258-contacts-list-first"), WireTest.Context());

        var supplier = Assert.Single(page.Items);
        Assert.Equal(Supplier, supplier.Id);
        Assert.Equal(1L, supplier.Id.Number);
        Assert.Equal(SupplierName, supplier.Name);
        Assert.Equal(ContactKind.External, supplier.Kind);
        Assert.Equal("E2E-FORNITORI", supplier.CategoryId);
        Assert.Equal("E2E-EXT", supplier.Code);
        Assert.Equal("user@example.test", supplier.Email);
        Assert.Equal("pec@example.test", supplier.Pec);
        Assert.Equal(9101, supplier.ArxivarId);
        Assert.Equal(new DateTimeOffset(2026, 10, 9, 11, 0, 45, TimeSpan.Zero).AddTicks(989640), supplier.CreatedAt);
        Assert.Equal(1, supplier.DocumentsAsSender);
        Assert.Equal(0, supplier.DocumentsAsRecipient);
        Assert.Null(supplier.Fax);
        Assert.NotNull(page.NextCursor);
    }

    [Fact]
    public void The_captured_contact_list_has_the_three_kinds_and_no_cursor_on_the_last_page()
    {
        var page = ContactWire.ReadPage(WireFixtures.Captured("150-contacts-list"), WireTest.Context());

        Assert.Equal(new[] { Supplier, Group, User }, page.Items.Select(c => c.Id).ToArray());
        Assert.Equal(new[] { ContactKind.External, ContactKind.Group, ContactKind.User }, page.Items.Select(c => c.Kind).ToArray());
        Assert.Null(page.Items[1].CategoryId);
        Assert.Equal(1, page.Items[1].DocumentsAsRecipient);
        Assert.Equal(9102, page.Items[2].ArxivarId);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public void The_captured_contact_detail_has_no_counters()
    {
        var contact = ContactWire.ReadContact(WireFixtures.Captured("259-contact-get"), WireTest.Context());

        Assert.Equal(Supplier, contact.Id);
        Assert.Equal(SupplierName, contact.Name);
        Assert.Null(contact.DocumentsAsSender);
        Assert.Null(contact.DocumentsAsRecipient);
    }

    [Fact]
    public void The_captured_categories_are_read_with_the_arxivar_id()
    {
        var category = Assert.Single(ContactWire.ReadCategories(WireFixtures.Captured("151-contact-categories-list"), WireTest.Context()));

        Assert.Equal("E2E-FORNITORI", category.Id);
        Assert.Equal("Fornitori E2E", category.Name);
        Assert.Equal(9001, category.ArxivarId);
    }

    [Fact]
    public void The_document_without_content_has_no_hash_and_lists_its_contacts_in_order()
    {
        var document = DocumentWire.ReadDocument(WireFixtures.Derived("doc-detail-without-content-with-contacts"), WireTest.Context());

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
        { "258-contacts-list-first", "contacts-page", "" },
        { "150-contacts-list", "contacts-page", "" },
        // Il dettaglio derivato e' un contatto minimo: l'oracolo dei nomi e' il contatto completo della pagina derivata.
        { "259-contact-get", "contacts-page", "items[]." },
        { "151-contact-categories-list", "contact-categories", "" },
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
