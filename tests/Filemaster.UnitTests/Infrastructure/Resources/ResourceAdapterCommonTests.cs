using Filemaster.Application;
using Filemaster.Domain;
using Filemaster.Infrastructure;

namespace Filemaster.UnitTests.Infrastructure.Resources;

/// <summary>
/// Cio' che vale per tutti e quattro gli adapter: argomenti non validi -> eccezione <b>dentro il Task</b> e nessuna richiesta inviata;
/// annullamento (token gia' scattato, annullamento durante l'attesa); errori comuni 401/403/500; ritentativi solo per i <c>GET</c>
/// (scritture mai, nemmeno su un errore di rete); <c>RequestTimeout</c> su ogni chiamata (sono tutte chiamate bufferizzate: un
/// <c>TransferTimeout</c> al suo posto lascerebbe una sonda appesa per mezz'ora).
/// </summary>
public sealed class ResourceAdapterCommonTests
{
    public static TheoryData<string> Operations
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var operation in ResourceRig.AllOperations)
            {
                data.Add(operation);
            }

            return data;
        }
    }

    public static TheoryData<string> Writes => new() { "folder-create", "folder-update", "folder-delete" };

    public static TheoryData<string> Reads
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var operation in ResourceRig.Reads)
            {
                data.Add(operation);
            }

            return data;
        }
    }

    public static TheoryData<string> InvalidArguments => new()
    {
        "create-code",
        "create-name-blank",
        "create-name-too-long",
        "create-parent",
        "create-name-surrogate",
        "update-id",
        "update-nothing",
        "update-new-code",
        "update-name-blank",
        "update-name-surrogate",
        "list-children-parent",
        "delete-id",
        "contacts-kind",
        "contacts-category",
        "contacts-text-surrogate",
        "contacts-cursor-surrogate",
        "contact-id",
    };

    // ----- argomenti -----

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public async Task An_invalid_argument_is_ArgumentException_inside_the_task_and_nothing_is_sent(string which)
    {
        using var rig = new ResourceRig();

        var task = InvalidCall(rig, which);

        Assert.True(task.IsFaulted, which + ": la validazione deve fallire subito, prima di qualunque attesa");
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => task);
        Assert.Equal(ExpectedParamName(which), exception.ParamName);
        Assert.Empty(rig.Handler.Requests);
    }

    [Fact]
    public async Task Null_requests_are_ArgumentNullException_inside_the_task()
    {
        using var rig = new ResourceRig();

        var create = rig.Folders.CreateAsync(null!);
        var update = rig.Folders.UpdateAsync(new FolderCode("FATTURE"), null!);
        var updateWithEmptyCode = rig.Folders.UpdateAsync(default, null!);

        Assert.True(create.IsFaulted && update.IsFaulted && updateWithEmptyCode.IsFaulted);
        Assert.Equal("request", (await Assert.ThrowsAsync<ArgumentNullException>(() => create)).ParamName);
        Assert.Equal("request", (await Assert.ThrowsAsync<ArgumentNullException>(() => update)).ParamName);
        Assert.Equal("request", (await Assert.ThrowsAsync<ArgumentNullException>(() => updateWithEmptyCode)).ParamName);
        Assert.Empty(rig.Handler.Requests);
    }

    [Fact]
    public void The_constructors_reject_a_null_transport()
    {
        Assert.Equal("transport", Assert.Throws<ArgumentNullException>(() => new HttpFolderCatalog(null!)).ParamName);
        Assert.Equal("transport", Assert.Throws<ArgumentNullException>(() => new HttpContactDirectory(null!)).ParamName);
        Assert.Equal("transport", Assert.Throws<ArgumentNullException>(() => new HttpTenantInfo(null!)).ParamName);
        Assert.Equal("transport", Assert.Throws<ArgumentNullException>(() => new HttpFilemasterHealth(null!)).ParamName);
    }

    // ----- annullamento -----

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task An_already_cancelled_token_is_OperationCanceledException_and_nothing_is_sent(string operation)
    {
        using var rig = new ResourceRig();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Call(operation, cts.Token));

        Assert.IsNotType<FilemasterTimeoutException>(exception);
        Assert.Empty(rig.Handler.Requests);
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task Cancelling_while_waiting_for_the_server_is_OperationCanceledException(string operation)
    {
        using var rig = new ResourceRig();
        using var cts = new CancellationTokenSource();
        var entered = Waiting.NewSignal();
        rig.Handler.Then(Waiting.Hang(entered));

        var call = rig.Call(operation, cts.Token);
        await Waiting.Within(entered.Task);
        cts.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Waiting.Within(call));
        Assert.IsNotType<FilemasterTimeoutException>(exception);
        Assert.Single(rig.Handler.Requests);
    }

    // ----- tempo -----

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task Every_call_ends_at_RequestTimeout_and_not_at_TransferTimeout(string operation)
    {
        using var rig = new ResourceRig(o =>
        {
            o.RequestTimeout = TimeSpan.FromSeconds(5);
            o.TransferTimeout = TimeSpan.FromSeconds(600);
        });
        var entered = Waiting.NewSignal();
        rig.Handler.Then(Waiting.Hang(entered));

        var call = rig.Call(operation);
        await Waiting.Within(entered.Task);
        rig.Rig.Time.Advance(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<FilemasterTimeoutException>(() => Waiting.Within(call));
        Assert.Single(rig.Handler.Requests);
    }

    // ----- ritentativi -----

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task Writes_are_never_retried_on_a_transient_503(string operation)
    {
        using var rig = new ResourceRig();
        rig.Then(() => Reply.Text(503));
        rig.Then(() => ResourceRig.Success(operation));

        await Assert.ThrowsAsync<ServerErrorException>(() => rig.Call(operation));

        Assert.Single(rig.Sent);
        Assert.Empty(rig.Rig.Delays.Delays);
    }

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task Writes_are_never_retried_on_a_connection_error(string operation)
    {
        using var rig = new ResourceRig();
        rig.ThenFail(new HttpRequestException("rete"));
        rig.Then(() => ResourceRig.Success(operation));

        await Assert.ThrowsAsync<ConnectionException>(() => rig.Call(operation));

        Assert.Single(rig.Sent);
    }

    [Theory]
    [MemberData(nameof(Reads))]
    public async Task Reads_are_retried_on_a_connection_error(string operation)
    {
        using var rig = new ResourceRig();
        rig.ThenFail(new HttpRequestException("rete"));
        rig.Then(() => ResourceRig.Success(operation));

        await rig.Call(operation);

        Assert.Equal(2, rig.Sent.Count);
        Assert.Equal(HttpMethod.Get, rig.Sent[1].Method);
    }

    [Theory]
    [MemberData(nameof(Reads))]
    public async Task Reads_are_retried_on_a_transient_502(string operation)
    {
        using var rig = new ResourceRig();
        rig.Then(() => Reply.Text(502));
        rig.Then(() => ResourceRig.Success(operation));

        await rig.Call(operation);

        Assert.Equal(2, rig.Sent.Count);
        Assert.Single(rig.Rig.Delays.Delays);
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task Every_operation_succeeds_on_its_success_answer_with_a_single_request(string operation)
    {
        using var rig = new ResourceRig();
        rig.Then(() => ResourceRig.Success(operation));

        await rig.Call(operation);

        Assert.Single(rig.Sent);
    }

    // ----- errori comuni -----

    [Theory]
    [InlineData(401, "unauthorized", typeof(UnauthorizedException))]
    [InlineData(403, "forbidden", typeof(ForbiddenException))]
    [InlineData(500, "internal-error", typeof(ServerErrorException))]
    [InlineData(404, "not-found", typeof(NotFoundException))]
    public async Task The_common_errors_are_mapped_on_every_call_and_never_retried(int status, string slug, Type expected)
    {
        foreach (var operation in ResourceRig.AllOperations)
        {
            using var rig = new ResourceRig();
            rig.Then(() => Reply.Problem(status, slug));

            var exception = await Assert.ThrowsAnyAsync<FilemasterException>(() => rig.Call(operation));

            Assert.IsType(expected, exception);
            Assert.Equal(status, exception.StatusCode);
            Assert.Single(rig.Sent);
        }
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task Only_the_probes_omit_the_api_key(string operation)
    {
        using var rig = new ResourceRig();
        rig.Then(() => ResourceRig.Success(operation));

        await rig.Call(operation);

        var anonymous = operation is "healthz" or "readyz";
        Assert.Equal(anonymous ? null : TransportRig.Key, rig.Single.Header("X-API-Key"));
    }

    private static Task InvalidCall(ResourceRig rig, string which) => which switch
    {
        "create-code" => rig.Folders.CreateAsync(new CreateFolderRequest(default, "Fatture")),
        "create-name-blank" => rig.Folders.CreateAsync(new CreateFolderRequest(new FolderCode("FATTURE"), "  ")),
        "create-name-too-long" => rig.Folders.CreateAsync(new CreateFolderRequest(new FolderCode("FATTURE"), new string('x', CreateFolderRequest.MaxNameLength + 1))),
        "create-parent" => rig.Folders.CreateAsync(new CreateFolderRequest(new FolderCode("FATTURE"), "Fatture") { ParentId = default(FolderCode) }),
        "create-name-surrogate" => rig.Folders.CreateAsync(new CreateFolderRequest(new FolderCode("FATTURE"), "a" + (char)0xD800)),
        "update-id" => rig.Folders.UpdateAsync(default, new UpdateFolderRequest { Name = "Fatture" }),
        "update-nothing" => rig.Folders.UpdateAsync(new FolderCode("FATTURE"), new UpdateFolderRequest()),
        "update-new-code" => rig.Folders.UpdateAsync(new FolderCode("FATTURE"), new UpdateFolderRequest { NewCode = default(FolderCode) }),
        "update-name-blank" => rig.Folders.UpdateAsync(new FolderCode("FATTURE"), new UpdateFolderRequest { Name = string.Empty }),
        "update-name-surrogate" => rig.Folders.UpdateAsync(new FolderCode("FATTURE"), new UpdateFolderRequest { Name = "a" + (char)0xDC00 }),
        "list-children-parent" => rig.Folders.ListChildrenAsync(default(FolderCode)),
        "delete-id" => rig.Folders.DeleteAsync(default),
        "contacts-kind" => rig.Contacts.ListAsync(new ContactQuery { Kind = ContactKind.Unknown }),
        "contacts-category" => rig.Contacts.ListAsync(new ContactQuery { CategoryId = "non valida/" }),
        "contacts-text-surrogate" => rig.Contacts.ListAsync(new ContactQuery { Text = "a" + (char)0xD800 }),
        "contacts-cursor-surrogate" => rig.Contacts.ListAsync(page: new PageRequest("a" + (char)0xD800)),
        "contact-id" => rig.Contacts.GetAsync(default),
        _ => throw new ArgumentOutOfRangeException(nameof(which), which, "caso sconosciuto"),
    };

    private static string? ExpectedParamName(string which) => which switch
    {
        "create-code" => nameof(CreateFolderRequest.Code),
        "create-name-blank" or "create-name-too-long" or "create-name-surrogate" => nameof(CreateFolderRequest.Name),
        "create-parent" => nameof(CreateFolderRequest.ParentId),
        "update-id" or "delete-id" or "contact-id" => "id",
        "update-nothing" => null,
        "update-new-code" => nameof(UpdateFolderRequest.NewCode),
        "update-name-blank" or "update-name-surrogate" => nameof(UpdateFolderRequest.Name),
        "list-children-parent" => "parentId",
        "contacts-kind" => nameof(ContactQuery.Kind),
        "contacts-category" => nameof(ContactQuery.CategoryId),
        "contacts-text-surrogate" => nameof(ContactQuery.Text),
        "contacts-cursor-surrogate" => nameof(PageRequest.Cursor),
        _ => throw new ArgumentOutOfRangeException(nameof(which), which, "caso sconosciuto"),
    };
}
