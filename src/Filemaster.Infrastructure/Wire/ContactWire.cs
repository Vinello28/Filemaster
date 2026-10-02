using Filemaster.Application;
using Filemaster.Domain;

namespace Filemaster.Infrastructure;

/// <summary>
/// L'anagrafica sul filo (<c>ContactDto</c> e <c>ContactCategoryDto</c> del server dev). Un contatto e'
/// <c>{"id","name","kind","category_id"?,"code"?,"address"?,"postal_code"?,"city"?,"province"?,"country"?,"email"?,"pec"?,"phone"?,"fax"?,"mobile"?,
/// "vat_number"?,"tax_code"?,"ipa_code"?,"office_code"?,"notes"?,"id_arxivar"?,"created_at","documents_as_sender"?,"documents_as_recipient"?}</c>:
/// il server omette i campi vuoti, e i due contatori ci sono solo negli elenchi. Una categoria e' <c>{"id","name","id_arxivar"?}</c>.
/// <b>Nessuna cattura contiene un contatto</b> (il server di prova non ne aveva): la forma e' DERIVATA dal codice del server e si
/// conferma con la prima cattura vera.
/// </summary>
internal static class ContactWire
{
    /// <summary>Legge un contatto (dettaglio: i contatori dei documenti sono null).</summary>
    /// <param name="body">I byte del corpo.</param>
    /// <param name="context">Lo status e l'id di correlazione della risposta.</param>
    /// <exception cref="UnexpectedResponseException">Il corpo non ha la forma di un contatto.</exception>
    internal static Contact ReadContact(byte[]? body, WireContext context) =>
        WireJson.ReadObject(body, context, "contatto", ReadContact);

    /// <summary>Legge una pagina di contatti (<c>GET /contacts</c>): ogni contatto ha i due contatori dei documenti.</summary>
    /// <param name="body">I byte del corpo.</param>
    /// <param name="context">Lo status e l'id di correlazione della risposta.</param>
    /// <exception cref="UnexpectedResponseException">Il corpo non ha la forma di una pagina di contatti.</exception>
    internal static Page<Contact> ReadPage(byte[]? body, WireContext context) =>
        PageWire.ReadPage(body, context, ReadContact);

    /// <summary>Legge l'elenco delle categorie (<c>GET /contact-categories</c>): <c>{"items":[...]}</c>, non paginato.</summary>
    /// <param name="body">I byte del corpo.</param>
    /// <param name="context">Lo status e l'id di correlazione della risposta.</param>
    /// <exception cref="UnexpectedResponseException">Il corpo non ha la forma di un elenco di categorie.</exception>
    internal static IReadOnlyList<ContactCategory> ReadCategories(byte[]? body, WireContext context) =>
        PageWire.ReadItems(body, context, ReadCategory);

    /// <summary>
    /// Il percorso dell'elenco dei contatti: <c>contacts</c> piu' la query <c>q</c>, <c>category_id</c>, <c>kind</c> (nell'ordine di
    /// <see cref="ContactQuery"/>) e poi <c>limit</c> e <c>cursor</c>. <c>kind</c> e' il nome del server, minuscolo, scritto a mano: mai il
    /// nome dell'enum del client.
    /// </summary>
    /// <param name="query">I filtri; null per non filtrare. Si valida con <see cref="ContactQuery.Validate"/> prima di costruire.</param>
    /// <param name="page">La pagina; null per la prima con il limite di default del server.</param>
    /// <exception cref="ArgumentException">I filtri non sono validi, o un testo ha un surrogato isolato.</exception>
    internal static string ListPath(ContactQuery? query, PageRequest? page)
    {
        query?.Validate();
        var builder = new QueryBuilder();
        if (query is not null)
        {
            builder
                .Add("q", query.Text, nameof(ContactQuery.Text))
                .Add("category_id", query.CategoryId, nameof(ContactQuery.CategoryId))
                .Add("kind", query.Kind is { } kind ? KindName(kind) : null, nameof(ContactQuery.Kind));
        }

        PageWire.AddPage(builder, page);
        return Routes.Contacts + builder.Build();
    }

    private static string KindName(ContactKind kind) =>
        kind switch
        {
            ContactKind.External => "external",
            ContactKind.User => "user",
            ContactKind.Group => "group",
            _ => throw new ArgumentException("Il genere deve essere External, User o Group.", nameof(kind)),
        };

    private static Contact ReadContact(WireObject contact) =>
        new(
            contact.RequiredId<ContactId>("id", ContactId.TryParse, "un id di contatto"),
            contact.RequiredString("name"),
            KindOf(contact.OptionalString("kind")),
            contact.OptionalString("category_id"),
            contact.OptionalString("code"),
            contact.OptionalString("address"),
            contact.OptionalString("postal_code"),
            contact.OptionalString("city"),
            contact.OptionalString("province"),
            contact.OptionalString("country"),
            contact.OptionalString("email"),
            contact.OptionalString("pec"),
            contact.OptionalString("phone"),
            contact.OptionalString("fax"),
            contact.OptionalString("mobile"),
            contact.OptionalString("vat_number"),
            contact.OptionalString("tax_code"),
            contact.OptionalString("ipa_code"),
            contact.OptionalString("office_code"),
            contact.OptionalString("notes"),
            contact.OptionalInt32("id_arxivar"),
            contact.RequiredDate("created_at"),
            contact.OptionalCount("documents_as_sender"),
            contact.OptionalCount("documents_as_recipient"));

    private static ContactCategory ReadCategory(WireObject category) =>
        new(
            category.RequiredString("id"),
            category.RequiredString("name"),
            category.OptionalInt32("id_arxivar"));

    // Confronto ordinale sui tre nomi del server. Assente o non riconosciuto e' Unknown (e' il default dell'enum).
    private static ContactKind KindOf(string? kind) =>
        kind switch
        {
            "external" => ContactKind.External,
            "user" => ContactKind.User,
            "group" => ContactKind.Group,
            _ => ContactKind.Unknown,
        };
}
