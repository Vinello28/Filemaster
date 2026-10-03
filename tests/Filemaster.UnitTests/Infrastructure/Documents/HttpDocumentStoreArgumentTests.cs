using Filemaster.Application;
using Filemaster.Domain;
using Filemaster.Infrastructure;

namespace Filemaster.UnitTests.Infrastructure.Documents;

/// <summary>
/// Gli argomenti non validi dell'adapter dei documenti: null -> <see cref="ArgumentNullException"/>, id o codice vuoto (<c>default</c>),
/// collezione vuota o con un id vuoto, richiesta o filtri non validi -> <see cref="ArgumentException"/>, sempre <b>prima di toccare la
/// rete</b> (nessuna richiesta al gestore) e <b>dentro il Task</b> (la chiamata restituisce un task fallito, non lancia in modo
/// sincrono: e' la scelta documentata nell'adapter). Poi l'annullamento: un token gia' scattato non invia nulla.
/// </summary>
public sealed class HttpDocumentStoreArgumentTests
{
    private static readonly DocumentId[] OneId = { StoreRig.Id };

    public static TheoryData<string> EmptyArguments => new()
    {
        "get-id",
        "delete-id",
        "move-id",
        "move-folder",
        "verify-id",
        "content-id",
        "content-id-with-range",
        "preview-id",
        "move-many-empty",
        "move-many-default-id",
        "move-many-folder",
        "verify-many-empty",
        "verify-many-default-id",
        "list-query",
        "upload-validate",
    };

    [Theory]
    [MemberData(nameof(EmptyArguments))]
    public async Task An_invalid_argument_is_ArgumentException_inside_the_task_and_nothing_is_sent(string which)
    {
        using var rig = new StoreRig();

        var task = InvalidCall(rig.Store, which);

        Assert.True(task.IsFaulted, which + ": la validazione deve fallire subito, prima di qualunque attesa");
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => task);
        Assert.Equal(ExpectedParamName(which), exception.ParamName);
        Assert.Empty(rig.Handler.Requests);
    }

    [Fact]
    public async Task A_null_upload_request_is_ArgumentNullException_inside_the_task()
    {
        using var rig = new StoreRig();

        var task = rig.Store.UploadAsync(null!);

        Assert.True(task.IsFaulted);
        var exception = await Assert.ThrowsAsync<ArgumentNullException>(() => task);
        Assert.Equal("request", exception.ParamName);
        Assert.Empty(rig.Handler.Requests);
    }

    [Fact]
    public async Task Null_id_collections_are_ArgumentNullException_inside_the_task()
    {
        using var rig = new StoreRig();

        var move = rig.Store.MoveManyAsync(null!, null);
        var verify = rig.Store.VerifyManyAsync(null!);

        Assert.True(move.IsFaulted && verify.IsFaulted);
        Assert.Equal("ids", (await Assert.ThrowsAsync<ArgumentNullException>(() => move)).ParamName);
        Assert.Equal("ids", (await Assert.ThrowsAsync<ArgumentNullException>(() => verify)).ParamName);
        Assert.Empty(rig.Handler.Requests);
    }

    [Fact]
    public void The_constructor_rejects_a_null_transport()
    {
        Assert.Throws<ArgumentNullException>(() => new HttpDocumentStore(null!));
    }

    [Theory]
    [InlineData("get")]
    [InlineData("list")]
    [InlineData("delete")]
    [InlineData("move")]
    [InlineData("verify")]
    [InlineData("move-many")]
    [InlineData("verify-many")]
    [InlineData("content")]
    [InlineData("preview")]
    [InlineData("upload")]
    public async Task An_already_cancelled_token_is_OperationCanceledException_and_nothing_is_sent(string operation)
    {
        using var rig = new StoreRig();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HttpDocumentStoreJsonTests.Call(rig, operation, cts.Token));

        Assert.IsNotType<FilemasterTimeoutException>(exception);
        Assert.Empty(rig.Handler.Requests);
    }

    [Theory]
    [InlineData("get")]
    [InlineData("delete")]
    [InlineData("move")]
    [InlineData("verify-many")]
    [InlineData("content")]
    public async Task Cancelling_while_waiting_for_the_server_is_OperationCanceledException(string operation)
    {
        using var rig = new StoreRig();
        using var cts = new CancellationTokenSource();
        var entered = Waiting.NewSignal();
        rig.Handler.Then(Waiting.Hang(entered));

        var call = HttpDocumentStoreJsonTests.Call(rig, operation, cts.Token);
        await Waiting.Within(entered.Task);
        cts.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Waiting.Within(call));
        Assert.IsNotType<FilemasterTimeoutException>(exception);
        Assert.Single(rig.Handler.Requests);
    }

    private static Task InvalidCall(HttpDocumentStore store, string which) => which switch
    {
        "get-id" => store.GetAsync(default),
        "delete-id" => store.DeleteAsync(default),
        "move-id" => store.MoveAsync(default, new FolderCode("FATTURE")),
        "move-folder" => store.MoveAsync(StoreRig.Id, default(FolderCode)),
        "verify-id" => store.VerifyAsync(default),
        "content-id" => store.OpenContentAsync(default),
        "content-id-with-range" => store.OpenContentAsync(default, ByteRange.Between(0, 9)),
        "preview-id" => store.OpenPreviewAsync(default),
        "move-many-empty" => store.MoveManyAsync(Array.Empty<DocumentId>(), null),
        "move-many-default-id" => store.MoveManyAsync(new[] { StoreRig.Id, default }, null),
        "move-many-folder" => store.MoveManyAsync(OneId, default(FolderCode)),
        "verify-many-empty" => store.VerifyManyAsync(Array.Empty<DocumentId>()),
        "verify-many-default-id" => store.VerifyManyAsync(new[] { default(DocumentId) }),
        "list-query" => store.ListAsync(new DocumentQuery { SenderId = default(ContactId) }),
        "upload-validate" => store.UploadAsync(new UploadDocumentRequest(new MemoryStream(new byte[3]), "a.pdf") { Owner = new string('x', 256) }),
        _ => throw new ArgumentOutOfRangeException(nameof(which), which, "caso sconosciuto"),
    };

    private static string ExpectedParamName(string which) => which switch
    {
        "move-folder" or "move-many-folder" => "folder",
        "move-many-empty" or "move-many-default-id" or "verify-many-empty" or "verify-many-default-id" => "ids",
        "list-query" => nameof(DocumentQuery.SenderId),
        "upload-validate" => nameof(UploadDocumentRequest.Owner),
        _ => "id",
    };
}
