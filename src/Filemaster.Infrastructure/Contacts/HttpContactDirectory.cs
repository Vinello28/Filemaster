using Filemaster.Application;
using Filemaster.Domain;

namespace Filemaster.Infrastructure;

/// <summary>
/// L'adapter HTTP di <see cref="IContactDirectory"/>: <c>GET contacts</c> (pagina con <c>next_cursor</c>, come i documenti),
/// <c>GET contacts/{id}</c> e <c>GET contact-categories</c> (elenco non paginato), attraverso il <see cref="FilemasterTransport"/> e il
/// livello wire (<see cref="ContactWire"/>, <see cref="Routes"/>). Sono tutte letture: il trasporto le ritenta sugli errori
/// transitori. Non tiene stato oltre al trasporto.
/// </summary>
/// <remarks>
/// <b>Validazione, sempre prima della rete e dentro il Task</b> (come <see cref="HttpDocumentStore"/>): un id vuoto o dei filtri non
/// validi (<see cref="ArgumentException"/>) escono dal <see cref="Task"/> restituito, senza che nessuna richiesta parta.
/// </remarks>
internal sealed class HttpContactDirectory : IContactDirectory
{
    private readonly FilemasterTransport _transport;

    /// <summary>Crea l'adapter sul trasporto indicato (condivisibile con gli altri adapter).</summary>
    /// <param name="transport">Il trasporto.</param>
    /// <exception cref="ArgumentNullException"><paramref name="transport"/> e' null.</exception>
    internal HttpContactDirectory(FilemasterTransport transport)
    {
        Guard.NotNull(transport);
        _transport = transport;
    }

    /// <inheritdoc />
    /// <remarks>Query <c>q</c>, <c>category_id</c>, <c>kind</c>, poi <c>limit</c> e <c>cursor</c> (<see cref="ContactWire.ListPath"/>).</remarks>
    public async Task<Page<Contact>> ListAsync(
        ContactQuery? query = null,
        PageRequest? page = null,
        CancellationToken cancellationToken = default)
    {
        var path = ContactWire.ListPath(query, page);
        var response = await JsonCalls.SendAsync(_transport, HttpMethod.Get, path, body: null, cancellationToken).ConfigureAwait(false);
        return ContactWire.ReadPage(response.Body, WireContext.Of(response));
    }

    /// <inheritdoc />
    public async Task<Contact> GetAsync(ContactId id, CancellationToken cancellationToken = default)
    {
        var path = Routes.Contact(id);
        var response = await JsonCalls.SendAsync(_transport, HttpMethod.Get, path, body: null, cancellationToken).ConfigureAwait(false);
        return ContactWire.ReadContact(response.Body, WireContext.Of(response));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ContactCategory>> ListCategoriesAsync(CancellationToken cancellationToken = default)
    {
        var response = await JsonCalls.SendAsync(_transport, HttpMethod.Get, Routes.ContactCategories, body: null, cancellationToken).ConfigureAwait(false);
        return ContactWire.ReadCategories(response.Body, WireContext.Of(response));
    }
}
