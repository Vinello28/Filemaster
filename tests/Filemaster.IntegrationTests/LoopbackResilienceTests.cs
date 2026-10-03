using System.Diagnostics;
using Filemaster.Application;
using Filemaster.Domain;
using Filemaster.IntegrationTests.Loopback;
using static Filemaster.IntegrationTests.Loopback.LoopbackSupport;

namespace Filemaster.IntegrationTests;

/// <summary>
/// Ritentativi, tempi e connessioni interrotte col gestore vero: il server conta le richieste, cosi' un ritentativo in piu' (o in meno)
/// si vede sul filo e non solo nel trasporto.
/// </summary>
public sealed class LoopbackResilienceTests
{
    private const string Busy = "occupato";

    private const string TooLargeProblem = """{"type":"/problems/request-too-large","title":"richiesta troppo grande","status":413,"detail":"il corpo supera il limite"}""";

    // Il primo tentativo fallisce con failFirst, i successivi ricevono la risposta giusta.
    private static LoopbackServer FailingOnce(Func<LoopbackExchange, Task> failFirst, Func<LoopbackExchange, Task> succeed)
    {
        var count = 0;
        return LoopbackServer.Start(exchange => Interlocked.Increment(ref count) == 1 ? failFirst(exchange) : succeed(exchange));
    }

    private static Task Unavailable(LoopbackExchange exchange) => exchange.RespondAsync(503, Busy, "text/plain");

    private static async Task CloseWithoutResponse(LoopbackExchange exchange)
    {
        await exchange.ReadBodyAsync().ConfigureAwait(false);
        exchange.Close();
    }

    private static async Task ReadAndRespond(LoopbackExchange exchange, int status, string body)
    {
        await exchange.ReadBodyAsync().ConfigureAwait(false);
        await exchange.RespondAsync(status, body).ConfigureAwait(false);
    }

    [Fact]
    public async Task A_GET_is_retried_after_a_503_that_is_not_a_problem()
    {
        using var server = FailingOnce(Unavailable, e => e.RespondAsync(200, PageJson));
        using var client = Client(server);

        var page = await Within(client.Documents.ListAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.Single(page.Items);
        Assert.Equal(2, server.Requests.Count);
        Assert.Single(server.Requests.Select(r => r.Header("X-Request-ID")).Distinct());
    }

    [Fact]
    public async Task A_GET_is_retried_by_the_client_after_the_connection_is_closed_before_the_response()
    {
        // Le prime 4 richieste trovano la connessione chiusa senza risposta, la quinta riceve l'ente. Su .NET il gestore vero rimanda
        // da solo un GET fino a 3 volte (4 invii per tentativo): solo il ritentativo del client arriva alla quinta. Con 10 tentativi
        // il conto torna anche dove il gestore non rimanda (.NET Framework: 4 tentativi falliti e il quinto riuscito).
        var count = 0;
        using var server = LoopbackServer.Start(exchange =>
            Interlocked.Increment(ref count) <= 4 ? CloseWithoutResponse(exchange) : exchange.RespondAsync(200, TenantJson));
        using var client = Client(server, o => o.Retry.MaxAttempts = 10);

        var tenant = await Within(client.Tenant.GetAsync(TestContext.Current.CancellationToken));

        Assert.Equal("acme-test", tenant.Slug);
        Assert.Equal(5, server.Requests.Count);
        Assert.Equal(5, server.ConnectionCount);
        Assert.Single(server.Requests.Select(r => r.Header("X-Request-ID")).Distinct());
    }

    [Fact]
    public async Task A_GET_gives_up_after_MaxAttempts_with_ServerErrorException()
    {
        using var server = LoopbackServer.Start(Unavailable);
        using var client = Client(server);

        var exception = await ThrowsWithin<ServerErrorException>(() => client.Documents.ListAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(503, exception.StatusCode);
        Assert.Equal(new FilemasterRetryOptionsSnapshot().MaxAttempts, server.Requests.Count);
    }

    [Fact]
    public async Task A_POST_a_DELETE_and_an_upload_are_never_retried_after_a_503()
    {
        using var server = LoopbackServer.Start(async exchange =>
        {
            await exchange.ReadBodyAsync().ConfigureAwait(false);
            await Unavailable(exchange).ConfigureAwait(false);
        });
        using var client = Client(server);
        var token = TestContext.Current.CancellationToken;
        using var source = new MemoryStream(Bytes(10_000));

        await ThrowsWithin<ServerErrorException>(() => client.Documents.VerifyAsync(DocId, token));
        await ThrowsWithin<ServerErrorException>(() => client.Documents.DeleteAsync(DocId, token));
        await ThrowsWithin<ServerErrorException>(() => client.Documents.UploadAsync(new UploadDocumentRequest(source, "a.pdf"), token));

        Assert.Equal(new[] { "POST", "DELETE", "POST" }, server.Requests.Select(r => r.Method));
    }

    public static TheoryData<string> Writes => new() { "delete", "verify", "folder-delete", "move-many", "upload" };

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task A_write_or_a_verify_is_sent_once_when_the_connection_closes_before_the_response(string call)
    {
        // Il gestore vero (SocketsHttpHandler, misurato su .NET 8 e 10) rimanda da solo, fino a 3 volte e su connessioni nuove, una
        // richiesta SENZA corpo quando la connessione si chiude prima del primo byte di risposta: per un DELETE o una verifica
        // gia' eseguiti dal server sarebbe un secondo invio (un 404 fuorviante, una verifica doppia nello storico). Il trasporto
        // da' un corpo vuoto alle richieste non GET, che cosi' partono una volta sola.
        using var server = LoopbackServer.Start(CloseWithoutResponse);
        using var client = Client(server);
        var token = TestContext.Current.CancellationToken;
        using var source = new MemoryStream(Bytes(10_000));

        Func<Task> send = call switch
        {
            "delete" => () => client.Documents.DeleteAsync(DocId, token),
            "verify" => () => client.Documents.VerifyAsync(DocId, token),
            "folder-delete" => () => client.Folders.DeleteAsync(new FolderCode("FATTURE"), token),
            "move-many" => () => client.Documents.MoveManyAsync(new[] { DocId }, null, token),
            _ => () => client.Documents.UploadAsync(new UploadDocumentRequest(source, "a.pdf"), token),
        };
        await ThrowsWithin<ConnectionException>(send);

        var request = Assert.Single(server.Requests);
        Assert.True(request.BodyComplete);
        Assert.Equal(1, server.ConnectionCount);
    }

    [Fact]
    public async Task A_server_that_does_not_answer_ends_in_FilemasterTimeoutException_after_RequestTimeout()
    {
        using var server = LoopbackServer.Start(exchange => exchange.HangUntilClientClosesAsync());
        using var client = Client(server, o => o.RequestTimeout = TimeSpan.FromSeconds(1));
        var watch = Stopwatch.StartNew();

        var exception = await ThrowsWithin<FilemasterTimeoutException>(() => client.Tenant.GetAsync(TestContext.Current.CancellationToken));

        watch.Stop();
        Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(0.9), TimeSpan.FromSeconds(10));
        Assert.NotNull(exception.RequestId);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task Cancelling_a_call_to_a_server_that_does_not_answer_is_OperationCanceledException()
    {
        using var server = LoopbackServer.Start(exchange => exchange.HangUntilClientClosesAsync());
        using var client = Client(server);
        using var cancellation = new CancellationTokenSource();

        var call = client.Tenant.GetAsync(cancellation.Token);
        await server.WaitForRequestsAsync(1);
        cancellation.Cancel();

        await Within(Assert.ThrowsAnyAsync<OperationCanceledException>(() => call));
    }

    [Fact]
    public async Task A_download_that_stops_mid_body_ends_in_FilemasterTimeoutException_after_TransferTimeout()
    {
        using var server = LoopbackServer.Start(async exchange =>
        {
            await exchange.WriteHeadAsync(200, "application/pdf", 50_000).ConfigureAwait(false);
            await exchange.WriteAsync(Bytes(1000)).ConfigureAwait(false);
            await exchange.HangUntilClientClosesAsync().ConfigureAwait(false);
        });
        using var client = Client(server, o => o.TransferTimeout = TimeSpan.FromSeconds(1));

        using var content = await Within(client.Documents.OpenContentAsync(DocId, cancellationToken: TestContext.Current.CancellationToken));
        var watch = Stopwatch.StartNew();
        await ThrowsWithin<FilemasterTimeoutException>(() => ReadAllAsync(content.Content));

        // Misurato su .NET 10: circa 3 s, cioe' TransferTimeout piu' i 2 s di ResponseDrainTimeout di SocketsHttpHandler (lo
        // smaltimento della risposta prova a svuotarla prima di chiudere la connessione, e la lettura ferma finisce solo dopo).
        watch.Stop();
        TestContext.Current.TestOutputHelper?.WriteLine("lettura ferma interrotta dopo " + watch.ElapsedMilliseconds + " ms");
        Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(0.9), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task An_upload_the_server_stops_reading_ends_in_FilemasterTimeoutException_after_TransferTimeout()
    {
        // 64 MiB generati: piu' di quanto stia nei buffer dei socket, cosi' il client resta fermo a scrivere.
        using var server = LoopbackServer.Start(exchange => exchange.HoldAsync());
        using var client = Client(server, o => o.TransferTimeout = TimeSpan.FromSeconds(1));
        using var source = new GeneratedStream(64L * 1024 * 1024, seekable: true);

        await ThrowsWithin<FilemasterTimeoutException>(() => client.Documents.UploadAsync(new UploadDocumentRequest(source, "a.pdf"), TestContext.Current.CancellationToken));

        Assert.Single(server.Requests);
        Assert.False(source.Disposed);
    }

    [Fact]
    public async Task An_upload_reset_mid_body_is_a_ConnectionException_and_is_not_retried()
    {
        using var server = LoopbackServer.Start(async exchange =>
        {
            await exchange.ReadBodyAsync(64 * 1024).ConfigureAwait(false);
            exchange.Reset();
        });
        using var client = Client(server);
        using var source = new GeneratedStream(16L * 1024 * 1024, seekable: true);

        await ThrowsWithin<ConnectionException>(() => client.Documents.UploadAsync(new UploadDocumentRequest(source, "a.pdf"), TestContext.Current.CancellationToken));

        Assert.Single(server.Requests);
        Assert.False(source.Disposed);
    }

    [Fact]
    public async Task An_upload_answered_with_413_and_closed_mid_body_is_RequestTooLarge_or_ConnectionException_as_documented()
    {
        // Come il limite del server web: risponde 413 con Connection: close prima di aver letto il corpo, poi chiude. Il doc di
        // UploadAsync e del trasporto ammette entrambi gli esiti (dipende da chi vince fra la scrittura del corpo e la lettura della
        // risposta); non deve mai restare appeso ne' ritentare.
        using var server = LoopbackServer.Start(async exchange =>
        {
            await exchange.ReadBodyAsync(64 * 1024).ConfigureAwait(false);
            await exchange.RespondAsync(413, TooLargeProblem, "application/problem+json", "Connection: close").ConfigureAwait(false);
            exchange.Close();
        });
        using var client = Client(server);
        using var source = new GeneratedStream(16L * 1024 * 1024, seekable: true);

        var exception = await Within(Assert.ThrowsAnyAsync<FilemasterException>(() => client.Documents.UploadAsync(new UploadDocumentRequest(source, "a.pdf"), TestContext.Current.CancellationToken)));

        TestContext.Current.TestOutputHelper?.WriteLine("413 con chiusura a meta' corpo -> " + exception.GetType().Name);
        Assert.True(exception is RequestTooLargeException or ConnectionException, exception.GetType().FullName);
        if (exception is RequestTooLargeException tooLarge)
        {
            Assert.Equal(413, tooLarge.StatusCode);
        }

        Assert.Single(server.Requests);
    }

    // I valori di default dei ritentativi, letti dalle opzioni invece che ripetuti a mano.
    private sealed class FilemasterRetryOptionsSnapshot
    {
        internal int MaxAttempts { get; } = new Filemaster.Infrastructure.FilemasterRetryOptions().MaxAttempts;
    }
}
