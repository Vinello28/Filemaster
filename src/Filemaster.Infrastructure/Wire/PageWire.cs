using Filemaster.Application;

namespace Filemaster.Infrastructure;

/// <summary>
/// Gli elenchi del server: la forma paginata <c>{"items":[...],"next_cursor":"..."}</c> (documenti, contatti) e quella non paginata
/// <c>{"items":[...]}</c> (cartelle, categorie dei contatti). Il tipo degli elementi lo legge il chiamante.
/// </summary>
internal static class PageWire
{
    /// <summary>
    /// Legge una pagina. <c>items</c> e' obbligatorio (anche vuoto); <c>next_cursor</c> e' assente sull'ultima pagina. Il cursore e' opaco:
    /// si restituisce cosi' com'e', ma un cursore presente e vuoto (o di soli spazi) e' non interpretabile, perche' <see cref="PageRequest"/>
    /// lo rifiuterebbe e chi scorre le pagine ripartirebbe da capo senza finire mai.
    /// </summary>
    /// <param name="body">I byte del corpo.</param>
    /// <param name="context">Lo status e l'id di correlazione della risposta.</param>
    /// <param name="readItem">La lettura di un elemento.</param>
    /// <exception cref="Filemaster.Domain.UnexpectedResponseException">Il corpo non ha la forma di una pagina.</exception>
    internal static Page<T> ReadPage<T>(byte[]? body, WireContext context, Func<WireObject, T> readItem) =>
        WireJson.ReadObject(
            body,
            context,
            "pagina",
            page =>
            {
                var items = page.RequiredList("items", readItem);
                var cursor = page.OptionalString("next_cursor");
                if (cursor is not null && string.IsNullOrWhiteSpace(cursor))
                {
                    throw context.Unexpected("Risposta non interpretabile: il campo 'pagina.next_cursor' e' vuoto.");
                }

                return new Page<T>(items, cursor);
            });

    /// <summary>
    /// Accoda <c>limit</c> e <c>cursor</c> (in quest'ordine) alla query di un elenco paginato. Una pagina null non aggiunge nulla (il server
    /// parte dalla prima con il limite di default); un limite null o un cursore null non aggiungono il loro parametro. Il cursore e' opaco: si
    /// codifica percent cosi' com'e', mai interpretato.
    /// </summary>
    /// <param name="builder">La query in costruzione.</param>
    /// <param name="page">Quale pagina chiedere; null per la prima.</param>
    internal static void AddPage(QueryBuilder builder, PageRequest? page)
    {
        Guard.NotNull(builder);
        if (page is null)
        {
            return;
        }

        builder.Add("limit", page.Limit).Add("cursor", page.Cursor, nameof(PageRequest.Cursor));
    }

    /// <summary>Legge un elenco non paginato: <c>{"items":[...]}</c>. Un eventuale <c>next_cursor</c> si ignora.</summary>
    /// <param name="body">I byte del corpo.</param>
    /// <param name="context">Lo status e l'id di correlazione della risposta.</param>
    /// <param name="readItem">La lettura di un elemento.</param>
    /// <exception cref="Filemaster.Domain.UnexpectedResponseException">Il corpo non ha la forma di un elenco.</exception>
    internal static IReadOnlyList<T> ReadItems<T>(byte[]? body, WireContext context, Func<WireObject, T> readItem) =>
        WireJson.ReadObject(body, context, "elenco", list => list.RequiredList("items", readItem));
}
