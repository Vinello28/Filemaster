using Filemaster.Application;
using Filemaster.Domain;

namespace Filemaster.UnitTests.Application;

/// <summary>Un <see cref="IContactDirectory"/> scritto a mano: <c>ListAsync</c> risponde con uno <see cref="PagedScript{T}"/>, gli altri metodi lanciano <see cref="NotImplementedException"/>.</summary>
public sealed class FakeContactDirectory : IContactDirectory
{
    /// <summary>Le pagine di <c>ListAsync</c>.</summary>
    public PagedScript<Contact> Pages { get; } = new();

    /// <summary>I filtri ricevuti da <c>ListAsync</c>, uno per chiamata.</summary>
    public List<ContactQuery?> Queries { get; } = new();

    public Task<Page<Contact>> ListAsync(ContactQuery? query = null, PageRequest? page = null, CancellationToken cancellationToken = default)
    {
        Queries.Add(query);
        return Pages.ListAsync(page, cancellationToken);
    }

    public Task<Contact> GetAsync(ContactId id, CancellationToken cancellationToken = default) => throw new NotImplementedException();

    public Task<IReadOnlyList<ContactCategory>> ListCategoriesAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
}
