using Filemaster.Application;
using Filemaster.Domain;

namespace Filemaster.UnitTests.Application;

/// <summary><see cref="DocumentStoreExtensions.EnumerateAsync"/> sui documenti: le prove sono in <see cref="EnumerationTests{TItem, TQuery}"/>.</summary>
public sealed class DocumentEnumerationTests : EnumerationTests<Document, DocumentQuery>
{
    protected override Harness CreateHarness() => new DocumentHarness();

    protected override Document CreateItem(int n) => TestData.Doc(n);

    protected override DocumentQuery ValidQuery() => new() { Owner = "mario.rossi" };

    protected override DocumentQuery InvalidQuery() => new() { FolderId = default(FolderCode) };

    protected override string InvalidQueryParamName => nameof(DocumentQuery.FolderId);

    private sealed class DocumentHarness : Harness
    {
        private readonly FakeDocumentStore _store = new();

        public override PagedScript<Document> Pages => _store.Pages;

        public override IReadOnlyList<DocumentQuery?> Queries => _store.Queries;

        public override string StoreParameterName => "store";

        public override IAsyncEnumerable<Document> Enumerate(DocumentQuery? query = null, int? pageSize = null, CancellationToken cancellationToken = default) =>
            _store.EnumerateAsync(query, pageSize, cancellationToken);

        public override IAsyncEnumerable<Document> EnumerateOnNull() => ((IDocumentStore)null!).EnumerateAsync();
    }
}
