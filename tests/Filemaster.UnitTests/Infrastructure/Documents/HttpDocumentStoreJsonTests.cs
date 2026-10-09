using System.Text.Json;
using Filemaster.Application;
using Filemaster.Domain;
using Filemaster.UnitTests.Wire;

namespace Filemaster.UnitTests.Infrastructure.Documents;

/// <summary>
/// Le chiamate JSON dell'adapter dei documenti (elenco, dettaglio, cancellazione, spostamento, verifica e le due operazioni in blocco):
/// metodo, percorso, intestazioni e corpo esatti (confrontati con le richieste che il server ha accettato nelle catture 72, 80, 129,
/// 133, 134, 136, 138), lettura delle risposte catturate, errori principali, e ritentativi solo per i <c>GET</c>.
/// </summary>
public sealed class HttpDocumentStoreJsonTests
{
    private const string Base = "https://filemaster.example.test";

    // ----- elenco -----

    [Fact]
    public async Task List_without_arguments_is_a_GET_of_documents_with_Accept_json_and_no_body_and_reads_the_captured_page()
    {
        using var rig = new StoreRig();
        rig.Then(() => FixtureReply.Json("70-docs-list-default"));

        var page = await rig.Store.ListAsync();

        var sent = rig.Single;
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal(Base + "/documents", sent.Uri.AbsoluteUri);
        Assert.Equal("application/json", sent.Header("Accept"));
        Assert.Null(sent.Body);
        using var oracle = JsonDocument.Parse(WireFixtures.Captured("70-docs-list-default"));
        var items = oracle.RootElement.GetProperty("items");
        Assert.Equal(items.GetArrayLength(), page.Items.Count);
        Assert.Equal(items[0].GetProperty("id").GetRawText(), page.Items[0].Id.Value); // l'id e' un numero JSON
        Assert.Equal(oracle.RootElement.TryGetProperty("next_cursor", out var cursor) ? cursor.GetString() : null, page.NextCursor);
    }

    [Fact]
    public async Task List_with_filters_sends_the_query_the_server_accepted_in_capture_80()
    {
        using var rig = new StoreRig();
        rig.Then(() => FixtureReply.Json("80-docs-list-filter-owner-tag"));

        var page = await rig.Store.ListAsync(new DocumentQuery { Owner = "maria", Tag = "fattura" });

        Assert.Equal(WireFixtures.RequestPath("80-docs-list-filter-owner-tag"), rig.Single.PathAndQuery);
        Assert.NotEmpty(page.Items);
    }

    [Fact]
    public async Task List_with_a_page_sends_limit_and_cursor_as_in_capture_72_and_reads_the_next_cursor_of_capture_71()
    {
        using var rig = new StoreRig();
        rig.Then(() => FixtureReply.Json("71-docs-list-limit1-page1"));
        rig.Then(() => FixtureReply.Json("72-docs-list-limit1-page2"));

        var first = await rig.Store.ListAsync(page: new PageRequest(limit: 1));
        await rig.Store.ListAsync(page: new PageRequest(first.NextCursor, 1));

        Assert.Equal(WireFixtures.RequestPath("71-docs-list-limit1-page1"), rig.Sent[0].PathAndQuery);
        Assert.Equal(WireFixtures.RequestPath("72-docs-list-limit1-page2"), rig.Sent[1].PathAndQuery);
    }

    [Fact]
    public async Task List_with_invalid_filters_throws_ArgumentException_and_sends_nothing()
    {
        using var rig = new StoreRig();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => rig.Store.ListAsync(new DocumentQuery { FolderId = default(FolderCode) }));

        Assert.Equal(nameof(DocumentQuery.FolderId), exception.ParamName);
        Assert.Empty(rig.Handler.Requests);
    }

    [Fact]
    public async Task List_is_a_GET_and_so_is_retried_on_a_transient_503()
    {
        using var rig = new StoreRig();
        rig.Then(() => Reply.Text(503));
        rig.Then(() => FixtureReply.Json("70-docs-list-default"));

        var page = await rig.Store.ListAsync();

        Assert.Equal(2, rig.Sent.Count);
        Assert.NotEmpty(page.Items);
    }

    [Fact]
    public async Task A_400_on_the_list_is_InvalidRequestException()
    {
        using var rig = new StoreRig();
        rig.Then(() => Reply.Problem(400, "validation-error", "cursore non valido"));

        var exception = await Assert.ThrowsAsync<InvalidRequestException>(() => rig.Store.ListAsync(page: new PageRequest("!!!")));

        Assert.Equal(400, exception.StatusCode);
    }

    // ----- dettaglio -----

    [Fact]
    public async Task Get_is_a_GET_of_the_document_path_and_reads_the_captured_document()
    {
        using var rig = new StoreRig();
        rig.Then(() => FixtureReply.Json("98-doc-get"));

        var document = await rig.Store.GetAsync(StoreRig.Id);

        var sent = rig.Single;
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal(WireFixtures.RequestPath("98-doc-get"), sent.PathAndQuery);
        Assert.Equal("application/json", sent.Header("Accept"));
        Assert.Null(sent.Body);
        Assert.Equal(StoreRig.Id, document.Id);
        Assert.Equal("fattura.pdf", document.OriginalFilename);
        Assert.Equal(590, document.SizeBytes);
    }

    [Fact]
    public async Task A_404_on_get_is_NotFoundException_with_the_request_id_of_the_body()
    {
        using var rig = new StoreRig();
        rig.Then(() => Reply.Problem(404, "not-found", requestId: "srv-404"));

        var exception = await Assert.ThrowsAsync<NotFoundException>(() => rig.Store.GetAsync(StoreRig.Id));

        Assert.Equal(404, exception.StatusCode);
        Assert.Equal("srv-404", exception.RequestId);
        Assert.Single(rig.Sent);
    }

    [Fact]
    public async Task An_unreadable_document_is_UnexpectedResponseException_with_the_real_status()
    {
        using var rig = new StoreRig();
        rig.Then(() => Reply.Json(200, "{\"id\":\"non-un-id\"}"));

        var exception = await Assert.ThrowsAsync<UnexpectedResponseException>(() => rig.Store.GetAsync(StoreRig.Id));

        Assert.Equal(200, exception.StatusCode);
    }

    // ----- cancellazione -----

    [Fact]
    public async Task Delete_is_a_DELETE_of_the_document_path_with_an_empty_body_and_a_204_completes()
    {
        using var rig = new StoreRig();
        rig.Then(() => Reply.Empty(204));

        await rig.Store.DeleteAsync(StoreRig.Id);

        var sent = rig.Single;
        Assert.Equal(HttpMethod.Delete, sent.Method);
        Assert.Equal("/documents/" + StoreRig.Id.Value, sent.PathAndQuery);
        Assert.Equal("application/json", sent.Header("Accept"));
        // Corpo vuoto con Content-Length: 0, perche' il gestore di .NET non rimandi da solo il DELETE (vedi FilemasterTransport).
        Assert.Empty(sent.Body!);
        Assert.Equal(0, sent.DeclaredLength);
    }

    [Fact]
    public async Task A_second_delete_answered_404_is_NotFoundException()
    {
        using var rig = new StoreRig();
        rig.Then(() => Reply.Problem(404, "not-found"));

        await Assert.ThrowsAsync<NotFoundException>(() => rig.Store.DeleteAsync(StoreRig.Id));
    }

    // ----- spostamento -----

    [Fact]
    public async Task Move_is_a_PATCH_with_the_json_body_of_capture_136()
    {
        using var rig = new StoreRig();
        rig.Then(() => Reply.Empty(204));

        await rig.Store.MoveAsync(StoreRig.Id, new FolderCode("FATTURE.2027"));

        var sent = rig.Single;
        Assert.Equal("PATCH", sent.Method.Method);
        Assert.Equal(WireFixtures.RequestPath("136-doc-move-to-folder"), sent.PathAndQuery);
        Assert.Equal("application/json", sent.ContentHeader("Content-Type"));
        Assert.Equal("application/json", sent.Header("Accept"));
        Assert.Equal(WireFixtures.RequestBody("136-doc-move-to-folder"), sent.BodyText);
    }

    [Fact]
    public async Task Move_to_the_root_sends_an_explicit_null_as_in_capture_138()
    {
        using var rig = new StoreRig();
        rig.Then(() => Reply.Empty(204));

        await rig.Store.MoveAsync(StoreRig.Id, folder: null);

        Assert.Equal(WireFixtures.RequestPath("138-doc-move-to-null"), rig.Single.PathAndQuery);
        Assert.Equal(WireFixtures.RequestBody("138-doc-move-to-null"), rig.Single.BodyText);
    }

    [Fact]
    public async Task Move_to_a_folder_that_does_not_exist_is_NotFoundException()
    {
        using var rig = new StoreRig();
        rig.Then(() => Reply.Problem(404, "not-found", "cartella non trovata"));

        var exception = await Assert.ThrowsAsync<NotFoundException>(() => rig.Store.MoveAsync(StoreRig.Id, new FolderCode("NESSUNA")));

        Assert.Equal("cartella non trovata", exception.Detail);
    }

    // ----- verifica -----

    [Fact]
    public async Task Verify_is_a_POST_without_body_and_reads_capture_126()
    {
        using var rig = new StoreRig();
        rig.Then(() => FixtureReply.Json("126-doc-verify"));

        var check = await rig.Store.VerifyAsync(StoreRig.Id);

        var sent = rig.Single;
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal(WireFixtures.RequestPath("126-doc-verify"), sent.PathAndQuery);
        Assert.Equal("application/json", sent.Header("Accept"));
        Assert.True(sent.Body is null || sent.Body.Length == 0);
        Assert.Equal(StoreRig.Id, check.DocumentId);
        Assert.True(check.Ok);
        Assert.Equal("cc1ba284a9fe9cefa40d4bd9dfb8d9e7fb395431aaf79478efca4e04da6c9d7e", check.Sha256);
    }

    [Fact]
    public async Task Verify_of_a_document_without_content_is_ContentUnavailableException()
    {
        using var rig = new StoreRig();
        rig.Then(() => Reply.Problem(409, "content-unavailable"));

        var exception = await Assert.ThrowsAsync<ContentUnavailableException>(() => rig.Store.VerifyAsync(StoreRig.Id));

        Assert.Equal(409, exception.StatusCode);
    }

    [Fact]
    public async Task A_409_conflict_is_not_content_unavailable()
    {
        using var rig = new StoreRig();
        rig.Then(() => Reply.Problem(409, "conflict"));

        var exception = await Assert.ThrowsAsync<ConflictException>(() => rig.Store.VerifyAsync(StoreRig.Id));

        Assert.IsNotType<ContentUnavailableException>(exception);
    }

    // ----- in blocco -----

    [Fact]
    public async Task MoveMany_is_a_POST_of_bulk_move_with_the_body_of_capture_133_and_returns_moved()
    {
        using var rig = new StoreRig();
        rig.Then(() => FixtureReply.Json("133-docs-bulk-move"));

        var moved = await rig.Store.MoveManyAsync(new[] { StoreRig.OtherId, StoreRig.ThirdId }, new FolderCode("FATTURE.2027"));

        var sent = rig.Single;
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal(WireFixtures.RequestPath("133-docs-bulk-move"), sent.PathAndQuery);
        Assert.Equal("application/json", sent.ContentHeader("Content-Type"));
        Assert.Equal(WireFixtures.RequestBody("133-docs-bulk-move"), sent.BodyText);
        Assert.Equal(2, moved);
    }

    [Fact]
    public async Task MoveMany_to_the_root_sends_the_body_of_capture_134()
    {
        using var rig = new StoreRig();
        rig.Then(() => FixtureReply.Json("134-docs-bulk-move-to-root"));

        await rig.Store.MoveManyAsync(new[] { StoreRig.OtherId }, folder: null);

        Assert.Equal(WireFixtures.RequestBody("134-docs-bulk-move-to-root"), rig.Single.BodyText);
    }

    [Fact]
    public async Task VerifyMany_is_a_POST_of_bulk_verify_with_the_body_of_capture_129_and_reads_the_counts()
    {
        using var rig = new StoreRig();
        rig.Then(() => FixtureReply.Json("129-docs-bulk-verify"));

        var result = await rig.Store.VerifyManyAsync(new[] { StoreRig.Id, StoreRig.OtherId });

        var sent = rig.Single;
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal(WireFixtures.RequestPath("129-docs-bulk-verify"), sent.PathAndQuery);
        Assert.Equal("application/json", sent.ContentHeader("Content-Type"));
        Assert.Equal("application/json", sent.Header("Accept"));
        Assert.Equal(WireFixtures.RequestBody("129-docs-bulk-verify"), sent.BodyText);
        Assert.Equal(new BulkVerifyResult(2, 2, 0, 0), result);
    }

    [Fact]
    public async Task VerifyMany_with_an_unknown_id_is_NotFoundException_for_the_whole_batch()
    {
        using var rig = new StoreRig();
        rig.Then(() => Reply.Problem(404, "not-found"));

        await Assert.ThrowsAsync<NotFoundException>(() => rig.Store.VerifyManyAsync(new[] { StoreRig.Id }));
    }

    [Fact]
    public async Task MoveMany_with_too_many_ids_is_RequestTooLargeException()
    {
        using var rig = new StoreRig();
        rig.Then(() => Reply.Problem(413, "request-too-large"));

        await Assert.ThrowsAsync<RequestTooLargeException>(() => rig.Store.MoveManyAsync(new[] { StoreRig.Id }, null));
    }

    // ----- mai ritentate: scritture e verifiche -----

    public static TheoryData<string> Writes => new() { "delete", "move", "verify", "move-many", "verify-many" };

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task Writes_and_verifications_are_never_retried_on_a_transient_503(string operation)
    {
        using var rig = new StoreRig();
        rig.Then(() => Reply.Text(503));
        rig.Then(() => Reply.Empty(204));

        await Assert.ThrowsAsync<ServerErrorException>(() => Call(rig, operation));

        Assert.Single(rig.Sent);
        Assert.Empty(rig.Rig.Delays.Delays);
    }

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task Writes_and_verifications_are_never_retried_on_a_connection_error(string operation)
    {
        using var rig = new StoreRig();
        rig.ThenFail(new HttpRequestException("rete"));
        rig.Then(() => Reply.Empty(204));

        await Assert.ThrowsAsync<ConnectionException>(() => Call(rig, operation));

        Assert.Single(rig.Sent);
    }

    [Theory]
    [InlineData("get")]
    [InlineData("list")]
    public async Task Reads_are_retried_on_a_connection_error(string operation)
    {
        using var rig = new StoreRig();
        rig.ThenFail(new HttpRequestException("rete"));
        rig.Then(() => operation == "get" ? FixtureReply.Json("98-doc-get") : FixtureReply.Json("70-docs-list-default"));

        await Call(rig, operation);

        Assert.Equal(2, rig.Sent.Count);
    }

    [Theory]
    [InlineData(401, "unauthorized", typeof(UnauthorizedException))]
    [InlineData(403, "forbidden", typeof(ForbiddenException))]
    [InlineData(500, "internal-error", typeof(ServerErrorException))]
    public async Task The_common_errors_are_mapped_on_every_json_call(int status, string slug, Type expected)
    {
        foreach (var operation in new[] { "get", "list", "delete", "move", "verify", "move-many", "verify-many" })
        {
            using var rig = new StoreRig();
            rig.Then(() => Reply.Problem(status, slug));

            var exception = await Assert.ThrowsAnyAsync<FilemasterException>(() => Call(rig, operation));

            Assert.IsType(expected, exception);
            Assert.Equal(status, exception.StatusCode);
        }
    }

    internal static Task Call(StoreRig rig, string operation, CancellationToken cancellationToken = default) => operation switch
    {
        "get" => rig.Store.GetAsync(StoreRig.Id, cancellationToken),
        "list" => rig.Store.ListAsync(cancellationToken: cancellationToken),
        "delete" => rig.Store.DeleteAsync(StoreRig.Id, cancellationToken),
        "move" => rig.Store.MoveAsync(StoreRig.Id, new FolderCode("FATTURE"), cancellationToken),
        "verify" => rig.Store.VerifyAsync(StoreRig.Id, cancellationToken),
        "move-many" => rig.Store.MoveManyAsync(new[] { StoreRig.Id }, null, cancellationToken),
        "verify-many" => rig.Store.VerifyManyAsync(new[] { StoreRig.Id }, cancellationToken),
        "content" => rig.Store.OpenContentAsync(StoreRig.Id, cancellationToken: cancellationToken),
        "preview" => rig.Store.OpenPreviewAsync(StoreRig.Id, cancellationToken),
        "upload" => rig.Store.UploadAsync(new UploadDocumentRequest(new MemoryStream(new byte[3]), "a.pdf"), cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "operazione sconosciuta"),
    };
}
