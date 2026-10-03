using Filemaster.Application;
using Filemaster.Domain;
using Filemaster.UnitTests.Infrastructure.Documents;
using Filemaster.UnitTests.Wire;

namespace Filemaster.UnitTests.Infrastructure.Resources;

/// <summary>
/// L'adapter delle cartelle: metodo, percorso, intestazioni e corpo esatti (confrontati con le richieste che il server ha accettato nelle
/// catture 21, 22, 24, 26, 28, 44 e 215), lettura delle risposte catturate, i 409 (duplicato, cartella non vuota, lock ARXivar) come
/// <see cref="ConflictException"/> mai ritentata, e gli altri errori principali.
/// </summary>
public sealed class HttpFolderCatalogTests
{
    private const string Base = "https://filemaster.example.test";

    // ----- creazione -----

    [Fact]
    public async Task Create_of_a_top_level_folder_is_a_POST_with_the_body_of_capture_21_and_reads_the_201()
    {
        using var rig = new ResourceRig();
        rig.Then(() => FixtureReply.Json("21-folders-create-parent"));

        var folder = await rig.Folders.CreateAsync(new CreateFolderRequest(new FolderCode("FATTURE"), "Fatture"));

        var sent = rig.Single;
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal(Base + "/folders", sent.Uri.AbsoluteUri);
        Assert.Equal(WireFixtures.RequestPath("21-folders-create-parent"), sent.PathAndQuery);
        Assert.Equal("application/json", sent.Header("Accept"));
        Assert.Equal("application/json", sent.ContentHeader("Content-Type"));
        Assert.Equal(TransportRig.Key, sent.Header("X-API-Key"));
        Assert.Equal(WireFixtures.RequestBody("21-folders-create-parent"), sent.BodyText);
        Assert.Equal(201, WireFixtures.Status("21-folders-create-parent"));
        Assert.Equal(new FolderCode("FATTURE"), folder.Id);
        Assert.Null(folder.ParentId);
        Assert.Equal("Fatture", folder.Name);
        Assert.Equal(WireTest.Utc(2026, 10, 1, 9, 59, 12, 9531000), folder.CreatedAt);
    }

    [Fact]
    public async Task Create_of_a_child_folder_sends_parent_id_as_in_capture_22()
    {
        using var rig = new ResourceRig();
        rig.Then(() => FixtureReply.Json("22-folders-create-child"));

        var folder = await rig.Folders.CreateAsync(
            new CreateFolderRequest(new FolderCode("FATTURE.2026"), "Fatture 2026") { ParentId = new FolderCode("FATTURE") });

        Assert.Equal(WireFixtures.RequestBody("22-folders-create-child"), rig.Single.BodyText);
        Assert.Equal(new FolderCode("FATTURE.2026"), folder.Id);
        Assert.Equal(new FolderCode("FATTURE"), folder.ParentId);
        Assert.Equal("Fatture 2026", folder.Name);
        Assert.Equal(WireTest.Utc(2026, 10, 1, 9, 59, 13, 1097060), folder.CreatedAt);
    }

    [Fact]
    public async Task A_duplicate_code_or_name_is_ConflictException_and_is_not_retried()
    {
        using var rig = new ResourceRig();
        rig.Then(() => Reply.Problem(409, "conflict", "esiste gia' una cartella con id FATTURE"));
        rig.Then(() => FixtureReply.Json("21-folders-create-parent"));

        var exception = await Assert.ThrowsAsync<ConflictException>(() => rig.Call("folder-create"));

        Assert.Equal(409, exception.StatusCode);
        Assert.Equal("esiste gia' una cartella con id FATTURE", exception.Detail);
        Assert.Single(rig.Sent);
        Assert.Empty(rig.Rig.Delays.Delays);
    }

    [Fact]
    public async Task A_parent_that_does_not_exist_is_NotFoundException()
    {
        using var rig = new ResourceRig();
        rig.Then(() => Reply.Problem(404, "not-found", "cartella NESSUNA non trovata"));

        var exception = await Assert.ThrowsAsync<NotFoundException>(() => rig.Folders.CreateAsync(
            new CreateFolderRequest(new FolderCode("FIGLIA"), "Figlia") { ParentId = new FolderCode("NESSUNA") }));

        Assert.Equal("cartella NESSUNA non trovata", exception.Detail);
    }

    [Fact]
    public async Task A_400_on_create_is_InvalidRequestException()
    {
        using var rig = new ResourceRig();
        rig.Then(() => Reply.Problem(400, "validation-error", "nome non valido"));

        var exception = await Assert.ThrowsAsync<InvalidRequestException>(() => rig.Call("folder-create"));

        Assert.Equal(400, exception.StatusCode);
    }

    [Fact]
    public async Task An_unreadable_created_folder_is_UnexpectedResponseException_with_the_real_status()
    {
        using var rig = new ResourceRig();
        rig.Then(() => Reply.Json(201, "{\"id\":\"FATTURE\",\"name\":\"Fatture\"}"));

        var exception = await Assert.ThrowsAsync<UnexpectedResponseException>(() => rig.Call("folder-create"));

        Assert.Equal(201, exception.StatusCode);
    }

    // ----- modifica -----

    [Fact]
    public async Task Update_of_the_name_is_a_PATCH_with_the_body_of_capture_28_and_reads_the_folder()
    {
        using var rig = new ResourceRig();
        rig.Then(() => FixtureReply.Json("28-folders-patch-name"));

        var folder = await rig.Folders.UpdateAsync(new FolderCode("FATTURE.2026"), new UpdateFolderRequest { Name = "Fatture 2026 rinominata" });

        var sent = rig.Single;
        Assert.Equal("PATCH", sent.Method.Method);
        Assert.Equal(WireFixtures.RequestPath("28-folders-patch-name"), sent.PathAndQuery);
        Assert.Equal("application/json", sent.Header("Accept"));
        Assert.Equal("application/json", sent.ContentHeader("Content-Type"));
        Assert.Equal(TransportRig.Key, sent.Header("X-API-Key"));
        Assert.Equal(WireFixtures.RequestBody("28-folders-patch-name"), sent.BodyText);
        Assert.Equal(new FolderCode("FATTURE.2026"), folder.Id);
        Assert.Equal(new FolderCode("FATTURE"), folder.ParentId);
        Assert.Equal("Fatture 2026 rinominata", folder.Name);
    }

    [Fact]
    public async Task Update_of_the_code_sends_the_new_code_as_id_as_in_capture_44_and_returns_the_new_code()
    {
        using var rig = new ResourceRig();
        rig.Then(() => FixtureReply.Json("44-folders-patch-code"));

        var folder = await rig.Folders.UpdateAsync(
            new FolderCode("FATTURE.2026"),
            new UpdateFolderRequest { NewCode = new FolderCode("FATTURE.2027"), Name = "Fatture 2027" });

        Assert.Equal(WireFixtures.RequestPath("44-folders-patch-code"), rig.Single.PathAndQuery);
        Assert.Equal(WireFixtures.RequestBody("44-folders-patch-code"), rig.Single.BodyText);
        Assert.Equal(new FolderCode("FATTURE.2027"), folder.Id);
        Assert.Equal("Fatture 2027", folder.Name);
    }

    [Fact]
    public async Task Update_of_the_code_only_sends_only_the_id()
    {
        using var rig = new ResourceRig();
        rig.Then(() => FixtureReply.Json("44-folders-patch-code"));

        await rig.Folders.UpdateAsync(new FolderCode("FATTURE.2026"), new UpdateFolderRequest { NewCode = new FolderCode("FATTURE.2027") });

        Assert.Equal("{\"id\":\"FATTURE.2027\"}", rig.Single.BodyText);
    }

    [Fact]
    public async Task The_409_of_the_ARXivar_lock_on_update_is_ConflictException_and_is_not_retried()
    {
        using var rig = new ResourceRig();
        rig.Then(() => Reply.Problem(409, "conflict", "collegamento ARXivar in corso: riprova tra poco"));
        rig.Then(() => FixtureReply.Json("44-folders-patch-code"));

        var exception = await Assert.ThrowsAsync<ConflictException>(() => rig.Folders.UpdateAsync(
            new FolderCode("FATTURE.2026"), new UpdateFolderRequest { NewCode = new FolderCode("FATTURE.2027") }));

        Assert.Equal("collegamento ARXivar in corso: riprova tra poco", exception.Detail);
        Assert.Single(rig.Sent);
        Assert.Empty(rig.Rig.Delays.Delays);
    }

    [Fact]
    public async Task Update_of_a_folder_that_does_not_exist_is_NotFoundException()
    {
        using var rig = new ResourceRig();
        rig.Then(() => Reply.Problem(404, "not-found"));

        await Assert.ThrowsAsync<NotFoundException>(() => rig.Call("folder-update"));
    }

    // ----- elenco -----

    [Fact]
    public async Task ListChildren_of_null_is_a_GET_of_folders_without_query_and_reads_capture_24()
    {
        using var rig = new ResourceRig();
        rig.Then(() => FixtureReply.Json("24-folders-list"));

        var folders = await rig.Folders.ListChildrenAsync(null);

        var sent = rig.Single;
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal(Base + "/folders", sent.Uri.AbsoluteUri);
        Assert.Equal(WireFixtures.RequestPath("24-folders-list"), sent.PathAndQuery);
        Assert.Equal("application/json", sent.Header("Accept"));
        Assert.Equal(TransportRig.Key, sent.Header("X-API-Key"));
        Assert.Null(sent.Body);
        Assert.Equal(new[] { "CHARSET", "FATTURE" }, folders.Select(f => f.Id.Value).ToArray());
        Assert.Equal(new[] { "Charset", "Fatture" }, folders.Select(f => f.Name).ToArray());
        Assert.All(folders, f => Assert.Null(f.ParentId));
    }

    [Fact]
    public async Task ListChildren_of_a_parent_sends_parent_id_as_in_capture_26()
    {
        using var rig = new ResourceRig();
        rig.Then(() => FixtureReply.Json("26-folders-list-children"));

        var folders = await rig.Folders.ListChildrenAsync(new FolderCode("FATTURE"));

        Assert.Equal(WireFixtures.RequestPath("26-folders-list-children"), rig.Single.PathAndQuery);
        var child = Assert.Single(folders);
        Assert.Equal(new FolderCode("FATTURE.2026"), child.Id);
        Assert.Equal(new FolderCode("FATTURE"), child.ParentId);
    }

    [Fact]
    public async Task An_empty_level_is_an_empty_list_as_in_capture_215()
    {
        using var rig = new ResourceRig();
        rig.Then(() => FixtureReply.Json("215-folders-list-after-delete"));

        var folders = await rig.Folders.ListChildrenAsync();

        Assert.Empty(folders);
    }

    [Fact]
    public async Task ListChildren_is_a_GET_and_so_is_retried_on_a_transient_503()
    {
        using var rig = new ResourceRig();
        rig.Then(() => Reply.Text(503));
        rig.Then(() => FixtureReply.Json("24-folders-list"));

        var folders = await rig.Folders.ListChildrenAsync();

        Assert.Equal(2, rig.Sent.Count);
        Assert.Equal(2, folders.Count);
    }

    [Fact]
    public async Task A_paged_answer_is_read_as_the_items_only()
    {
        using var rig = new ResourceRig();
        rig.Then(() => Reply.Json(200, "{\"items\":[],\"next_cursor\":\"abc\"}"));

        Assert.Empty(await rig.Folders.ListChildrenAsync());
    }

    [Fact]
    public async Task An_unreadable_list_is_UnexpectedResponseException()
    {
        using var rig = new ResourceRig();
        rig.Then(() => Reply.Json(200, "{\"folders\":[]}"));

        var exception = await Assert.ThrowsAsync<UnexpectedResponseException>(() => rig.Folders.ListChildrenAsync());

        Assert.Equal(200, exception.StatusCode);
    }

    // ----- cancellazione -----

    [Fact]
    public async Task Delete_is_a_DELETE_of_the_folder_path_with_an_empty_body_and_a_204_completes()
    {
        using var rig = new ResourceRig();
        rig.Then(() => Reply.Empty(204));

        await rig.Folders.DeleteAsync(new FolderCode("FATTURE.2027"));

        var sent = rig.Single;
        Assert.Equal(HttpMethod.Delete, sent.Method);
        Assert.Equal("/folders/FATTURE.2027", sent.PathAndQuery);
        Assert.Equal("application/json", sent.Header("Accept"));
        Assert.Equal(TransportRig.Key, sent.Header("X-API-Key"));
        // Corpo vuoto con Content-Length: 0, perche' il gestore di .NET non rimandi da solo il DELETE (vedi FilemasterTransport).
        Assert.Empty(sent.Body!);
        Assert.Equal(0, sent.DeclaredLength);
    }

    [Theory]
    [InlineData("la cartella non e' vuota")]
    [InlineData("collegamento ARXivar in corso: riprova tra poco")]
    public async Task A_409_on_delete_is_ConflictException_and_is_not_retried(string detail)
    {
        using var rig = new ResourceRig();
        rig.Then(() => Reply.Problem(409, "conflict", detail));
        rig.Then(() => Reply.Empty(204));

        var exception = await Assert.ThrowsAsync<ConflictException>(() => rig.Call("folder-delete"));

        Assert.Equal(detail, exception.Detail);
        Assert.Equal(409, exception.StatusCode);
        Assert.Single(rig.Sent);
        Assert.Empty(rig.Rig.Delays.Delays);
    }

    [Fact]
    public async Task A_409_without_problem_body_on_delete_is_still_ConflictException()
    {
        using var rig = new ResourceRig();
        rig.Then(() => Reply.Empty(409));

        await Assert.ThrowsAsync<ConflictException>(() => rig.Call("folder-delete"));
        Assert.Single(rig.Sent);
    }

    [Fact]
    public async Task A_second_delete_answered_404_is_NotFoundException()
    {
        using var rig = new ResourceRig();
        rig.Then(() => Reply.Problem(404, "not-found"));

        await Assert.ThrowsAsync<NotFoundException>(() => rig.Call("folder-delete"));
    }
}
