using System.Text.RegularExpressions;
using Filemaster.Application;
using Filemaster.Domain;
using Filemaster.IntegrationTests.Loopback;
using static Filemaster.IntegrationTests.Loopback.LoopbackSupport;

namespace Filemaster.IntegrationTests;

/// <summary>
/// Cosa mette il client sul filo col gestore vero: le intestazioni di ogni chiamata (chiave, id di richiesta, User-Agent; niente chiave
/// alle sonde) e i redirect, che non si seguono (la chiave non arriva mai a un secondo indirizzo).
/// </summary>
public sealed class LoopbackWireTests
{
    private static readonly Regex RequestId = new("^[0-9a-f]{32}$", RegexOptions.CultureInvariant);

    // Risponde come il server alle rotte che i test usano.
    private static Task Route(LoopbackExchange exchange)
    {
        var path = exchange.Path;
        var method = exchange.Request.Method;
        return (method, path) switch
        {
            ("GET", "/healthz") => exchange.RespondAsync(200, "ok", "text/plain; charset=utf-8"),
            ("GET", "/readyz") => exchange.RespondAsync(200, ReadyJson),
            ("GET", "/tenant") => exchange.RespondAsync(200, TenantJson),
            ("GET", "/documents") => exchange.RespondAsync(200, PageJson),
            ("DELETE", _) => exchange.RespondAsync(204),
            ("POST", "/documents") => ReadThenRespond(exchange, 201, UploadJson),
            _ => exchange.RespondAsync(404, """{"type":"/problems/not-found","title":"non trovato","status":404}""", "application/problem+json"),
        };
    }

    private static async Task ReadThenRespond(LoopbackExchange exchange, int status, string body)
    {
        await exchange.ReadBodyAsync().ConfigureAwait(false);
        await exchange.RespondAsync(status, body).ConfigureAwait(false);
    }

    [Fact]
    public async Task Probes_go_without_the_key_and_every_other_call_carries_key_request_id_and_user_agent()
    {
        using var server = LoopbackServer.Start(Route);
        using var client = Client(server);
        var token = TestContext.Current.CancellationToken;
        using var source = new MemoryStream(Bytes(1000));

        Assert.True((await Within(client.Health.CheckLivenessAsync(token))).IsHealthy);
        Assert.True((await Within(client.Health.CheckReadinessAsync(token))).IsHealthy);
        await Within(client.Tenant.GetAsync(token));
        await Within(client.Documents.ListAsync(cancellationToken: token));
        await Within(client.Documents.UploadAsync(new UploadDocumentRequest(source, "a.pdf"), token));
        await Within(client.Documents.DeleteAsync(DocId, token));

        var requests = server.Requests;
        Assert.Equal(
            new[] { "GET /healthz", "GET /readyz", "GET /tenant", "GET /documents", "POST /documents", "DELETE /documents/5000000001" },
            requests.Select(r => r.Method + " " + r.Target));
        foreach (var request in requests)
        {
            var probe = request.Target is "/healthz" or "/readyz";
            Assert.Equal(probe ? 0 : 1, request.HeaderCount("X-API-Key"));
            if (!probe)
            {
                Assert.Equal(Key, request.Header("X-API-Key"));
            }

            Assert.Equal(1, request.HeaderCount("X-Request-ID"));
            Assert.Matches(RequestId, request.Header("X-Request-ID")!);
            Assert.StartsWith("Filemaster/", request.Header("User-Agent"), StringComparison.Ordinal);
        }

        Assert.Equal(requests.Count, requests.Select(r => r.Header("X-Request-ID")).Distinct().Count());
    }

    public static TheoryData<int> RedirectStatuses => new() { 301, 302, 303, 307, 308 };

    [Theory]
    [MemberData(nameof(RedirectStatuses))]
    public async Task A_redirect_is_not_followed_so_the_key_never_reaches_the_other_address(int status)
    {
        using var elsewhere = LoopbackServer.Start(Route);
        using var server = LoopbackServer.Start(exchange => exchange.RespondAsync(status, string.Empty, "text/plain", "Location: " + elsewhere.BaseAddress + "tenant"));
        using var client = Client(server);

        var exception = await ThrowsWithin<UnexpectedResponseException>(() => client.Tenant.GetAsync(TestContext.Current.CancellationToken));

        Assert.Equal(status, exception.StatusCode);
        Assert.Single(server.Requests);
        Assert.Empty(elsewhere.Requests);
        Assert.Equal(0, elsewhere.ConnectionCount);
    }

    [Fact]
    public async Task A_redirect_of_a_delete_or_of_a_download_is_not_followed_either()
    {
        using var elsewhere = LoopbackServer.Start(Route);
        using var server = LoopbackServer.Start(exchange => exchange.RespondAsync(307, string.Empty, "text/plain", "Location: " + elsewhere.BaseAddress + exchange.Path.TrimStart('/')));
        using var client = Client(server);
        var token = TestContext.Current.CancellationToken;

        var delete = await ThrowsWithin<UnexpectedResponseException>(() => client.Documents.DeleteAsync(DocId, token));
        var download = await ThrowsWithin<UnexpectedResponseException>(() => client.Documents.OpenContentAsync(DocId, cancellationToken: token));

        Assert.Equal(307, delete.StatusCode);
        Assert.Equal(307, download.StatusCode);
        Assert.Equal(2, server.Requests.Count);
        Assert.Equal(0, elsewhere.ConnectionCount);
    }
}
