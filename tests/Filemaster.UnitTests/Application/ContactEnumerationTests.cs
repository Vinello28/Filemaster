using Filemaster.Application;
using Filemaster.Domain;

namespace Filemaster.UnitTests.Application;

/// <summary><see cref="ContactDirectoryExtensions.EnumerateAsync"/> sui contatti: le prove sono in <see cref="EnumerationTests{TItem, TQuery}"/>.</summary>
public sealed class ContactEnumerationTests : EnumerationTests<Contact, ContactQuery>
{
    protected override Harness CreateHarness() => new ContactHarness();

    protected override Contact CreateItem(int n) => TestData.ContactOf(n);

    protected override ContactQuery ValidQuery() => new() { Text = "rossi", Kind = ContactKind.External };

    protected override ContactQuery InvalidQuery() => new() { Kind = ContactKind.Unknown };

    protected override string InvalidQueryParamName => nameof(ContactQuery.Kind);

    private sealed class ContactHarness : Harness
    {
        private readonly FakeContactDirectory _directory = new();

        public override PagedScript<Contact> Pages => _directory.Pages;

        public override IReadOnlyList<ContactQuery?> Queries => _directory.Queries;

        public override string StoreParameterName => "directory";

        public override IAsyncEnumerable<Contact> Enumerate(ContactQuery? query = null, int? pageSize = null, CancellationToken cancellationToken = default) =>
            _directory.EnumerateAsync(query, pageSize, cancellationToken);

        public override IAsyncEnumerable<Contact> EnumerateOnNull() => ((IContactDirectory)null!).EnumerateAsync();
    }
}
