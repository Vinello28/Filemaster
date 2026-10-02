using System.Text;
using Filemaster.Application;
using Filemaster.Domain;

// Come in VerifiedContentStreamTests: si usa la forma con array di ReadAsync, l'unica che esiste anche su net48.
#pragma warning disable CA1835

namespace Filemaster.UnitTests.Application;

/// <summary>
/// <see cref="DocumentStoreExtensions.OpenVerifiedContentAsync"/> con un <see cref="IDocumentStore"/> finto: documento senza contenuto,
/// risposta parziale, catena di smaltimento (stream e risposta originale, una volta sola), intestazioni copiate e verifica dell'hash.
/// </summary>
public sealed class OpenVerifiedContentTests
{
    // printf 'abc' | shasum -a 256
    private const string AbcHash = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    // printf '' | shasum -a 256
    private const string EmptyHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private static readonly byte[] Abc = Encoding.ASCII.GetBytes("abc");
    private static readonly DateTimeOffset Modified = new(2026, 10, 1, 9, 59, 15, TimeSpan.Zero);

    private sealed class TrackingOwner : IDisposable
    {
        private readonly List<string>? _log;

        public TrackingOwner(List<string>? log = null) => _log = log;

        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
            _log?.Add("owner");
        }
    }

    private static (DocumentContent Content, ScriptedStream Stream, TrackingOwner Owner) Original(
        byte[] data,
        ContentRange? range = null,
        List<string>? log = null)
    {
        var stream = new ScriptedStream(data) { Log = log };
        var owner = new TrackingOwner(log);
        var content = new DocumentContent(stream, "application/pdf", "fattura.pdf", data.Length, Modified, range, owner);
        return (content, stream, owner);
    }

    private static FakeDocumentStore StoreReturning(DocumentContent content)
    {
        var store = new FakeDocumentStore();
        store.OnOpenContent = (_, _, _) => Task.FromResult(content);
        return store;
    }

    // --- argomenti ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_document_without_content_throws_ArgumentException_for_document_and_never_calls_the_store()
    {
        var store = new FakeDocumentStore();
        var withoutContent = TestData.Doc(1, sha256: null, sizeBytes: 0);
        Assert.False(withoutContent.HasContent);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => store.OpenVerifiedContentAsync(withoutContent));

        Assert.Equal("document", exception.ParamName);
        Assert.Empty(store.OpenCalls);
    }

    [Fact]
    public async Task A_null_store_throws_ArgumentNullException()
    {
        var exception = await Assert.ThrowsAsync<ArgumentNullException>(() => ((IDocumentStore)null!).OpenVerifiedContentAsync(TestData.Doc(1, AbcHash, 3)));

        Assert.Equal("store", exception.ParamName);
    }

    [Fact]
    public async Task A_null_document_throws_ArgumentNullException_and_never_calls_the_store()
    {
        var store = new FakeDocumentStore();

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(() => store.OpenVerifiedContentAsync(null!));

        Assert.Equal("document", exception.ParamName);
        Assert.Empty(store.OpenCalls);
    }

    // --- l'apertura ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_whole_content_is_opened_with_the_document_id_no_range_and_the_token()
    {
        var (original, _, _) = Original(Abc);
        var store = StoreReturning(original);
        var document = TestData.Doc(7, AbcHash, 3);
        using var cancellation = new CancellationTokenSource();

        using var result = await store.OpenVerifiedContentAsync(document, cancellation.Token);

        var call = Assert.Single(store.OpenCalls);
        Assert.Equal(document.Id, call.Id);
        Assert.Null(call.Range);
        Assert.Equal(cancellation.Token, call.Token);
    }

    [Fact]
    public async Task The_result_is_a_new_DocumentContent_with_a_VerifiedContentStream_and_the_same_headers()
    {
        var (original, _, _) = Original(Abc);
        var store = StoreReturning(original);

        using var result = await store.OpenVerifiedContentAsync(TestData.Doc(1, AbcHash, 3));

        Assert.NotSame(original, result);
        Assert.IsType<VerifiedContentStream>(result.Content);
        Assert.Equal("application/pdf", result.ContentType);
        Assert.Equal("fattura.pdf", result.FileName);
        Assert.Equal(3L, result.ContentLength);
        Assert.Equal(Modified, result.LastModified);
        Assert.Null(result.Range);
        Assert.False(result.IsPartial);
    }

    [Fact]
    public async Task Headers_that_the_server_did_not_send_stay_null()
    {
        var stream = new ScriptedStream(Abc);
        var original = new DocumentContent(stream, "application/octet-stream");
        var store = StoreReturning(original);

        using var result = await store.OpenVerifiedContentAsync(TestData.Doc(1, AbcHash, 3));

        Assert.Equal("application/octet-stream", result.ContentType);
        Assert.Null(result.FileName);
        Assert.Null(result.ContentLength);
        Assert.Null(result.LastModified);
    }

    [Fact]
    public async Task Reading_the_result_to_the_end_gives_the_bytes_and_a_clean_end_when_the_hash_matches()
    {
        var (original, _, _) = Original(Abc);
        var store = StoreReturning(original);
        using var result = await store.OpenVerifiedContentAsync(TestData.Doc(1, AbcHash, 3));
        using var all = new MemoryStream();

        await result.Content.CopyToAsync(all);

        Assert.Equal(Abc, all.ToArray());
    }

    [Fact]
    public async Task Reading_the_result_to_the_end_throws_ContentIntegrityException_when_the_hash_does_not_match()
    {
        var (original, _, _) = Original(Encoding.ASCII.GetBytes("abd"));
        var store = StoreReturning(original);
        using var result = await store.OpenVerifiedContentAsync(TestData.Doc(1, AbcHash, 3));
        using var all = new MemoryStream();

        var exception = await Assert.ThrowsAsync<ContentIntegrityException>(() => result.Content.CopyToAsync(all));

        Assert.False(exception.IsTruncated);
        Assert.Equal(200, exception.StatusCode);
        Assert.Equal(3, all.Length);
    }

    [Fact]
    public async Task The_size_of_the_document_is_the_expected_length_so_a_short_content_is_reported_as_truncated()
    {
        // L'hash e' quello di "abc" ma il documento dichiara 5 byte e ne arrivano 3: manca qualcosa.
        var (original, _, _) = Original(Abc);
        var store = StoreReturning(original);
        using var result = await store.OpenVerifiedContentAsync(TestData.Doc(1, AbcHash, 5));

        var exception = await Assert.ThrowsAsync<ContentIntegrityException>(() => result.Content.CopyToAsync(Stream.Null));

        Assert.True(exception.IsTruncated);
        Assert.Equal(5L, exception.ExpectedLength);
        Assert.Equal(3L, exception.ActualLength);
    }

    [Fact]
    public async Task The_size_of_the_document_is_the_expected_length_so_a_long_content_is_reported_as_not_truncated()
    {
        var (original, _, _) = Original(Abc);
        var store = StoreReturning(original);
        using var result = await store.OpenVerifiedContentAsync(TestData.Doc(1, AbcHash, 2));

        var exception = await Assert.ThrowsAsync<ContentIntegrityException>(() => result.Content.CopyToAsync(Stream.Null));

        Assert.False(exception.IsTruncated);
        Assert.Equal(2L, exception.ExpectedLength);
        Assert.Equal(3L, exception.ActualLength);
    }

    [Fact]
    public async Task A_document_with_empty_content_is_valid_with_the_hash_of_the_empty_string()
    {
        var (original, _, _) = Original(Array.Empty<byte>());
        var store = StoreReturning(original);
        var document = TestData.Doc(1, EmptyHash, 0);
        Assert.True(document.HasContent);

        using var result = await store.OpenVerifiedContentAsync(document);

        Assert.Equal(0, await result.Content.ReadAsync(new byte[4], 0, 4));
    }

    // --- risposta parziale --------------------------------------------------------------------------------------

    [Fact]
    public async Task A_partial_response_throws_UnexpectedResponseException_and_the_original_content_is_disposed_once()
    {
        var log = new List<string>();
        var (original, stream, owner) = Original(Abc, new ContentRange(0, 2, 590), log);
        var store = StoreReturning(original);

        var exception = await Assert.ThrowsAsync<UnexpectedResponseException>(() => store.OpenVerifiedContentAsync(TestData.Doc(1, AbcHash, 3)));

        Assert.Equal(206, exception.StatusCode);
        Assert.Equal(1, stream.DisposeCount);
        Assert.Equal(1, owner.DisposeCount);
        Assert.Equal(new[] { "stream", "owner" }, log);
    }

    // --- smaltimento --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Disposing_the_result_disposes_the_stream_and_then_the_original_owner_exactly_once()
    {
        var log = new List<string>();
        var (original, stream, owner) = Original(Abc, log: log);
        var store = StoreReturning(original);
        var result = await store.OpenVerifiedContentAsync(TestData.Doc(1, AbcHash, 3));

        result.Dispose();

        Assert.Equal(1, stream.DisposeCount);
        Assert.Equal(1, owner.DisposeCount);
        Assert.Equal(new[] { "stream", "owner" }, log);
    }

    [Fact]
    public async Task Disposing_the_result_twice_and_the_original_too_releases_everything_only_once()
    {
        var (original, stream, owner) = Original(Abc);
        var store = StoreReturning(original);
        var result = await store.OpenVerifiedContentAsync(TestData.Doc(1, AbcHash, 3));

        result.Dispose();
        result.Dispose();
        original.Dispose();

        Assert.Equal(1, stream.DisposeCount);
        Assert.Equal(1, owner.DisposeCount);
    }

    [Fact]
    public async Task Nothing_is_disposed_while_the_result_is_alive()
    {
        var (original, stream, owner) = Original(Abc);
        var store = StoreReturning(original);

        using var result = await store.OpenVerifiedContentAsync(TestData.Doc(1, AbcHash, 3));

        Assert.Equal(0, stream.DisposeCount);
        Assert.Equal(0, owner.DisposeCount);
        Assert.True(result.Content.CanRead);
    }

    [Fact]
    public async Task Disposing_the_result_before_the_end_gives_no_verdict_and_no_exception_even_with_a_wrong_hash()
    {
        var (original, stream, owner) = Original(Encoding.ASCII.GetBytes("abd"));
        var store = StoreReturning(original);
        var result = await store.OpenVerifiedContentAsync(TestData.Doc(1, AbcHash, 3));
        Assert.Equal(1, await result.Content.ReadAsync(new byte[1], 0, 1));

        result.Dispose();

        Assert.Equal(1, stream.DisposeCount);
        Assert.Equal(1, owner.DisposeCount);
    }

    [Fact]
    public async Task If_the_wrapper_cannot_be_created_the_original_content_is_disposed_once_and_the_error_propagates()
    {
        // Un hash malformato nel documento (un fake sbagliato: il server scrive 64 cifre esadecimali) fa fallire il costruttore dello stream.
        var log = new List<string>();
        var (original, stream, owner) = Original(Abc, log: log);
        var store = StoreReturning(original);
        var broken = TestData.Doc(1, "not-a-hash", 3);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => store.OpenVerifiedContentAsync(broken));

        Assert.Equal("expectedSha256", exception.ParamName);
        Assert.Equal(1, stream.DisposeCount);
        Assert.Equal(1, owner.DisposeCount);
        Assert.Equal(new[] { "stream", "owner" }, log);
    }

    [Fact]
    public async Task An_error_from_the_store_propagates_unchanged()
    {
        var store = new FakeDocumentStore();
        var notFound = new NotFoundException(null, 404);
        store.OnOpenContent = (_, _, _) => Task.FromException<DocumentContent>(notFound);

        var thrown = await Assert.ThrowsAsync<NotFoundException>(() => store.OpenVerifiedContentAsync(TestData.Doc(1, AbcHash, 3)));

        Assert.Same(notFound, thrown);
    }
}
