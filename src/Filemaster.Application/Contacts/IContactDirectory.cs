using Filemaster.Domain;

namespace Filemaster.Application;

/// <summary>
/// L'anagrafica dell'ente (mittenti e destinatari che i documenti possono citare), in sola lettura: la alimenta il
/// collegamento con ARXivar, il client non crea ne' modifica contatti. E' la porta per le risorse <c>/contacts</c> e
/// <c>/contact-categories</c> del server dev.
/// </summary>
/// <remarks>
/// Valgono le regole comuni di <see cref="IDocumentStore"/> (annullamento, argomenti null e id vuoti, errori
/// 401/403/connessione/5xx); qui si documentano solo gli errori specifici. Tutti i metodi richiedono lo scope <c>read</c>.
/// </remarks>
public interface IContactDirectory
{
    /// <summary>
    /// Elenca i contatti che soddisfano i filtri, in ordine di nome e poi di id (<c>GET /contacts</c>). Ogni contatto
    /// dell'elenco ha i contatori <see cref="Contact.DocumentsAsSender"/> e <see cref="Contact.DocumentsAsRecipient"/>
    /// (nel dettaglio sono null).
    /// </summary>
    /// <param name="query">I filtri; null per non filtrare. Si valida con <see cref="ContactQuery.Validate"/>.</param>
    /// <param name="page">Quale pagina e quanti elementi; null per la prima pagina con il limite di default del server (50).</param>
    /// <param name="cancellationToken">Per annullare la chiamata.</param>
    /// <returns>La pagina e il cursore per la successiva (null se e' l'ultima).</returns>
    /// <exception cref="ArgumentException">I filtri non rispettano i limiti del server (vedi <see cref="ContactQuery.Validate"/>).</exception>
    /// <exception cref="InvalidRequestException">Il server ha rifiutato i parametri (400): per esempio un cursore non suo (quelli dei documenti non valgono qui).</exception>
    Task<Page<Contact>> ListAsync(
        ContactQuery? query = null,
        PageRequest? page = null,
        CancellationToken cancellationToken = default);

    /// <summary>Legge un contatto (<c>GET /contacts/{id}</c>); i contatori dei documenti sono null.</summary>
    /// <param name="id">L'id del contatto.</param>
    /// <param name="cancellationToken">Per annullare la chiamata.</param>
    /// <returns>Il contatto.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> e' l'id vuoto (<c>default</c>).</exception>
    /// <exception cref="NotFoundException">Il contatto non esiste.</exception>
    Task<Contact> GetAsync(ContactId id, CancellationToken cancellationToken = default);

    /// <summary>Elenca tutte le categorie dei contatti, in ordine di nome (<c>GET /contact-categories</c>); non e' paginato.</summary>
    /// <param name="cancellationToken">Per annullare la chiamata.</param>
    /// <returns>Le categorie; lista vuota se non ce ne sono.</returns>
    Task<IReadOnlyList<ContactCategory>> ListCategoriesAsync(CancellationToken cancellationToken = default);
}
