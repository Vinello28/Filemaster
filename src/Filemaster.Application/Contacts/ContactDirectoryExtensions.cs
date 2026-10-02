using Filemaster.Domain;

namespace Filemaster.Application;

/// <summary>Estensioni di <see cref="IContactDirectory"/> costruite sopra i metodi della porta.</summary>
public static class ContactDirectoryExtensions
{
    /// <summary>
    /// Elenca tutti i contatti che soddisfano i filtri, scorrendo da solo le pagine (<see cref="IContactDirectory.ListAsync"/> con
    /// il cursore di ogni pagina) finche' <see cref="Page{T}.NextCursor"/> e' null. Funziona come
    /// <see cref="DocumentStoreExtensions.EnumerateAsync(IDocumentStore, DocumentQuery, int?, CancellationToken)"/>: stesso ordine del server (per nome e poi id),
    /// stessi controlli subito, stessa protezione da un server che non avanza e stesso annullamento; li' le note complete.
    /// </summary>
    /// <remarks>
    /// <c>await foreach</c> richiede C# 8: chi e' fermo a C# 7.3 usa <see cref="IContactDirectory.ListAsync"/> con
    /// <c>new PageRequest(page.NextCursor)</c> fino a un cursore null.
    /// </remarks>
    /// <param name="directory">La porta dei contatti.</param>
    /// <param name="query">I filtri, passati invariati a ogni pagina; null per non filtrare.</param>
    /// <param name="pageSize">Quanti elementi chiedere per pagina, da 1 a <see cref="PageRequest.MaxLimit"/>; null per il default del server (50).</param>
    /// <param name="cancellationToken">Per annullare l'enumerazione; arriva a ogni <see cref="IContactDirectory.ListAsync"/>.</param>
    /// <returns>Una sequenza asincrona di tutti i contatti, che fa le richieste mentre la si legge.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="directory"/> e' null.</exception>
    /// <exception cref="ArgumentException"><paramref name="query"/> non e' valida (vedi <see cref="ContactQuery.Validate"/>).</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageSize"/> e' minore di 1 o maggiore di <see cref="PageRequest.MaxLimit"/>.</exception>
    /// <exception cref="UnexpectedResponseException">Durante l'iterazione: il server restituisce un cursore gia' usato o vuoto.</exception>
    public static IAsyncEnumerable<Contact> EnumerateAsync(
        this IContactDirectory directory,
        ContactQuery? query = null,
        int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        Guard.NotNull(directory);
        query?.Validate();
        Pagination.CheckPageSize(pageSize);
        return Pagination.IterateAsync((request, token) => directory.ListAsync(query, request, token), pageSize, cancellationToken);
    }
}
