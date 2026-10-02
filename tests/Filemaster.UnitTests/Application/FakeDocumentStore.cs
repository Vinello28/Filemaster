using Filemaster.Application;
using Filemaster.Domain;

namespace Filemaster.UnitTests.Application;

/// <summary>
/// Un <see cref="IDocumentStore"/> scritto a mano: <c>ListAsync</c> risponde con uno <see cref="PagedScript{T}"/> e <c>OpenContentAsync</c>
/// con una funzione, e ogni chiamata si registra. Tutti gli altri metodi lanciano <see cref="NotImplementedException"/>: se un'estensione
/// ne usasse uno non previsto il test fallirebbe.
/// </summary>
public sealed class FakeDocumentStore : IDocumentStore
{
    /// <summary>Le pagine di <c>ListAsync</c>.</summary>
    public PagedScript<Document> Pages { get; } = new();

    /// <summary>I filtri ricevuti da <c>ListAsync</c>, uno per chiamata.</summary>
    public List<DocumentQuery?> Queries { get; } = new();

    /// <summary>Chiamata a ogni <c>ListAsync</c> con i filtri ricevuti, prima di rispondere.</summary>
    public Action<DocumentQuery?>? OnList { get; set; }

    /// <summary>Le chiamate a <c>OpenContentAsync</c>.</summary>
    public List<(DocumentId Id, ByteRange? Range, CancellationToken Token)> OpenCalls { get; } = new();

    /// <summary>La risposta di <c>OpenContentAsync</c>; senza, la chiamata lancia.</summary>
    public Func<DocumentId, ByteRange?, CancellationToken, Task<DocumentContent>>? OnOpenContent { get; set; }

    public Task<Page<Document>> ListAsync(DocumentQuery? query = null, PageRequest? page = null, CancellationToken cancellationToken = default)
    {
        Queries.Add(query);
        OnList?.Invoke(query);
        return Pages.ListAsync(page, cancellationToken);
    }

    public Task<DocumentContent> OpenContentAsync(DocumentId id, ByteRange? range = null, CancellationToken cancellationToken = default)
    {
        OpenCalls.Add((id, range, cancellationToken));
        return OnOpenContent is null
            ? throw new InvalidOperationException("OnOpenContent non e' impostato.")
            : OnOpenContent(id, range, cancellationToken);
    }

    public Task<UploadResult> UploadAsync(UploadDocumentRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();

    public Task<Document> GetAsync(DocumentId id, CancellationToken cancellationToken = default) => throw new NotImplementedException();

    public Task<DocumentContent> OpenPreviewAsync(DocumentId id, CancellationToken cancellationToken = default) => throw new NotImplementedException();

    public Task DeleteAsync(DocumentId id, CancellationToken cancellationToken = default) => throw new NotImplementedException();

    public Task MoveAsync(DocumentId id, FolderCode? folder, CancellationToken cancellationToken = default) => throw new NotImplementedException();

    public Task<IntegrityCheck> VerifyAsync(DocumentId id, CancellationToken cancellationToken = default) => throw new NotImplementedException();

    public Task<int> MoveManyAsync(IReadOnlyCollection<DocumentId> ids, FolderCode? folder, CancellationToken cancellationToken = default) => throw new NotImplementedException();

    public Task<BulkVerifyResult> VerifyManyAsync(IReadOnlyCollection<DocumentId> ids, CancellationToken cancellationToken = default) => throw new NotImplementedException();
}
