using Filemaster.Application;
using Filemaster.Domain;
using static Filemaster.IntegrationTests.Live.LiveServer;

namespace Filemaster.IntegrationTests.Live;

/// <summary>
/// Il contratto degli errori contro il server vero: ogni risposta d'errore diventa l'eccezione documentata (ProblemMapper),
/// con status, slug e request id letti dal corpo problem+json.
/// </summary>
[Trait("Category", "Live")]
public sealed class LiveErrorTests
{
    private static readonly DocumentId UnknownDocument = new("doc_00000000000000000000000000");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_wrong_key_is_unauthorized()
    {
        using var client = Client("saf_ThisKeyDoesNotExistOnTheServer_0123456789");

        var error = await Assert.ThrowsAsync<UnauthorizedException>(() => client.Tenant.GetAsync(Ct));

        Assert.Equal(401, error.StatusCode);
        Assert.Equal("unauthorized", error.ProblemType);
        Assert.NotNull(error.RequestId);
        await Assert.ThrowsAsync<UnauthorizedException>(() => client.Documents.ListAsync(cancellationToken: Ct));
    }

    [Fact]
    public async Task A_read_key_cannot_upload_move_or_delete()
    {
        using var reader = Client(ReadKey);
        using var writer = Client();
        await LiveCleanup.WithCleanupAsync(writer, async cleanup =>
        {
            var error = await Assert.ThrowsAsync<ForbiddenException>(() => UploadAsync(reader, cleanup, UniqueBytes(100), "vietato.pdf"));
            Assert.Equal(403, error.StatusCode);
            Assert.Equal("forbidden", error.ProblemType);

            var uploaded = await UploadAsync(writer, cleanup, UniqueBytes(100), "di-altri.pdf");
            await Assert.ThrowsAsync<ForbiddenException>(() => reader.Documents.DeleteAsync(uploaded.Document.Id, Ct));
            await Assert.ThrowsAsync<ForbiddenException>(() => reader.Documents.MoveAsync(uploaded.Document.Id, null, Ct));
            await Assert.ThrowsAsync<ForbiddenException>(
                () => reader.Folders.CreateAsync(new CreateFolderRequest(UniqueFolder("RO"), "Sola lettura"), Ct));

            // La lettura e la verifica restano permesse alla chiave read.
            Assert.Equal(uploaded.Document.Id, (await reader.Documents.GetAsync(uploaded.Document.Id, Ct)).Id);
            Assert.True((await reader.Documents.VerifyAsync(uploaded.Document.Id, Ct)).Ok);
        });
    }

    [Fact]
    public async Task Unknown_ids_are_not_found()
    {
        using var client = Client();

        var error = await Assert.ThrowsAsync<NotFoundException>(() => client.Documents.GetAsync(UnknownDocument, Ct));
        Assert.Equal(404, error.StatusCode);
        Assert.Equal("not-found", error.ProblemType);
        Assert.NotNull(error.RequestId);
        Assert.False(string.IsNullOrWhiteSpace(error.Detail));

        await Assert.ThrowsAsync<NotFoundException>(() => client.Documents.OpenContentAsync(UnknownDocument, cancellationToken: Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => client.Documents.OpenPreviewAsync(UnknownDocument, Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => client.Documents.MoveAsync(UnknownDocument, null, Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => client.Contacts.GetAsync(new ContactId("con_00000000000000000000000000"), Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => client.Folders.DeleteAsync(UniqueFolder("NONE"), Ct));
        await Assert.ThrowsAsync<NotFoundException>(
            () => client.Folders.UpdateAsync(UniqueFolder("NONE"), new UpdateFolderRequest { Name = "x" }, Ct));
    }

    [Fact]
    public async Task A_cursor_the_server_did_not_issue_is_an_invalid_request()
    {
        using var client = Client();

        var error = await Assert.ThrowsAsync<InvalidRequestException>(
            () => client.Documents.ListAsync(page: new PageRequest("not-a-cursor-from-the-server"), cancellationToken: Ct));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal("validation-error", error.ProblemType);
        await Assert.ThrowsAsync<InvalidRequestException>(
            () => client.Contacts.ListAsync(page: new PageRequest("not-a-cursor-from-the-server"), cancellationToken: Ct));
    }

    [Fact]
    public async Task A_range_beyond_the_end_is_an_unexpected_416()
    {
        using var client = Client();
        await LiveCleanup.WithCleanupAsync(client, async cleanup =>
        {
            var uploaded = await UploadAsync(client, cleanup, UniqueBytes(500), "corto.pdf");

            var error = await Assert.ThrowsAsync<UnexpectedResponseException>(
                () => client.Documents.OpenContentAsync(uploaded.Document.Id, ByteRange.From(10_000), Ct));

            Assert.Equal(416, error.StatusCode);
        });
    }
}
