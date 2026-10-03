using Filemaster.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Filemaster.UnitTests.Infrastructure;

/// <summary>
/// Cio' che vale per ogni modalita' del trasporto: le intestazioni per richiesta (<c>X-API-Key</c>, <c>X-Request-ID</c> nell'alfabeto
/// che il server tiene, <c>User-Agent</c>), l'indirizzo con il prefisso di percorso, il fatto che l'<c>HttpClient</c> non venga
/// toccato ne' smaltito, la copia delle opzioni, l'avviso su <c>http</c> non loopback, i controlli degli argomenti.
/// </summary>
public sealed class TransportCommonTests
{
    private static bool IsLowerHex(string value) => value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));

    // L'alfabeto che il server tiene dell'X-Request-ID del client (HttpPipeline.Sanitize): lettere e cifre ASCII, '-', '_' e '.', max 64.
    private static bool IsAcceptedByTheServer(string value) =>
        value.Length is > 0 and <= 64
        && value.All(c => (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '-' || c == '_' || c == '.');

    [Fact]
    public async Task Every_request_carries_the_key_a_conformant_request_id_and_a_user_agent()
    {
        using var rig = new TransportRig();
        rig.Handler.Then(Reply.Json(200, "{}"));

        await rig.Transport.SendBufferedAsync(TransportRig.Get(), default);

        var sent = Assert.Single(rig.Handler.Requests);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal(TransportRig.Key, sent.Header("X-API-Key"));
        var id = sent.Header("X-Request-ID");
        Assert.NotNull(id);
        Assert.Equal(32, id.Length);
        Assert.True(IsLowerHex(id));
        Assert.True(IsAcceptedByTheServer(id));
        var agent = sent.Header("User-Agent");
        Assert.NotNull(agent);
        Assert.StartsWith("Filemaster/", agent, StringComparison.Ordinal);
        Assert.Contains(" (", agent, StringComparison.Ordinal);
        Assert.EndsWith(")", agent, StringComparison.Ordinal);
        Assert.All(agent, c => Assert.InRange(c, ' ', '~'));
    }

    [Fact]
    public async Task OmitApiKey_drops_only_the_key_and_keeps_the_other_headers_on_every_attempt()
    {
        using var rig = new TransportRig();
        rig.Handler.ThenFail(new HttpRequestException("rete")).Then(Reply.Json(200, "{}"));
        var request = new TransportRequest(HttpMethod.Get, "readyz") { OmitApiKey = true };

        await rig.Transport.SendBufferedAsync(request, default);

        Assert.Equal(2, rig.Handler.Requests.Count);
        Assert.All(rig.Handler.Requests, sent =>
        {
            Assert.Null(sent.Header("X-API-Key"));
            Assert.False(sent.Message.Headers.Contains("X-API-Key"));
            Assert.Equal(32, sent.Header("X-Request-ID")!.Length);
            Assert.StartsWith("Filemaster/", sent.Header("User-Agent"), StringComparison.Ordinal);
        });
        Assert.Equal(rig.Handler.Requests[0].Header("X-Request-ID"), rig.Handler.Requests[1].Header("X-Request-ID"));
    }

    [Fact]
    public async Task OmitApiKey_is_false_by_default_and_then_the_key_is_sent()
    {
        using var rig = new TransportRig();
        rig.Handler.Then(Reply.Json(200, "{}"));
        var request = new TransportRequest(HttpMethod.Get, "readyz");

        Assert.False(request.OmitApiKey);
        await rig.Transport.SendBufferedAsync(request, default);

        Assert.Equal(TransportRig.Key, Assert.Single(rig.Handler.Requests).Header("X-API-Key"));
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task A_request_that_is_not_a_GET_and_has_no_body_is_sent_with_an_empty_body(string method)
    {
        // SocketsHttpHandler rimanda da solo una richiesta senza contenuto se la connessione si chiude prima della risposta
        // (provato col gestore vero in LoopbackResilienceTests): con un corpo vuoto non lo fa.
        using var rig = new TransportRig();
        long? declared = -1;
        rig.Handler.Then((request, _) =>
        {
            // Letta durante l'invio: dopo, il trasporto ha gia' smaltito il messaggio e il suo contenuto.
            declared = request.Content?.Headers.ContentLength;
            return Task.FromResult(Reply.Json(200, "{}"));
        });

        await rig.Transport.SendBufferedAsync(new TransportRequest(new HttpMethod(method), "documents/doc_X"), default);

        Assert.NotNull(Assert.Single(rig.Handler.Requests).Message.Content);
        Assert.Equal(0, declared);
    }

    [Fact]
    public async Task A_GET_is_sent_without_a_body_and_a_body_set_by_the_request_is_kept()
    {
        using var rig = new TransportRig();
        rig.Handler.Then(Reply.Json(200, "{}")).Then(Reply.Json(200, "{}"));
        using var body = new ByteArrayContent(new byte[] { 1, 2, 3 });
        var post = new TransportRequest(HttpMethod.Post, "documents/bulk/move") { Customize = message => message.Content = body };

        await rig.Transport.SendBufferedAsync(TransportRig.Get(), default);
        await rig.Transport.SendBufferedAsync(post, default);

        Assert.Null(rig.Handler.Requests[0].Message.Content);
        Assert.Same(body, rig.Handler.Requests[1].Message.Content);
    }

    [Fact]
    public async Task The_request_id_is_new_for_every_call()
    {
        using var rig = new TransportRig();
        rig.Handler.Then(Reply.Json(200, "{}")).Then(Reply.Json(200, "{}")).Then(Reply.Json(200, "{}"));

        await rig.Transport.SendBufferedAsync(TransportRig.Get(), default);
        await rig.Transport.SendBufferedAsync(TransportRig.Get(), default);
        await rig.Transport.SendBufferedAsync(TransportRig.Post(), default);

        var ids = rig.Handler.Requests.Select(r => r.Header("X-Request-ID")).ToArray();
        Assert.Equal(3, ids.Distinct().Count());
    }

    [Fact]
    public void NewRequestId_is_32_lowercase_hex_characters_and_never_repeats()
    {
        var ids = Enumerable.Range(0, 2000).Select(_ => FilemasterTransport.NewRequestId()).ToArray();

        Assert.All(ids, id => Assert.True(id.Length == 32 && IsLowerHex(id) && IsAcceptedByTheServer(id)));
        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    [Theory]
    [InlineData("https://filemaster.example.test", "documents", "https://filemaster.example.test/documents")]
    [InlineData("https://filemaster.example.test/", "documents/doc_X/content", "https://filemaster.example.test/documents/doc_X/content")]
    [InlineData("https://filemaster.example.test/proxy", "documents", "https://filemaster.example.test/proxy/documents")]
    [InlineData("https://filemaster.example.test/proxy/", "folders?parent_id=FATTURE", "https://filemaster.example.test/proxy/folders?parent_id=FATTURE")]
    [InlineData("https://filemaster.example.test/a/b", "healthz", "https://filemaster.example.test/a/b/healthz")]
    public async Task The_path_prefix_of_the_base_address_is_kept(string baseAddress, string relative, string expected)
    {
        using var rig = new TransportRig(o => o.BaseAddress = new Uri(baseAddress));
        rig.Handler.Then(Reply.Json(200, "{}"));

        await rig.Transport.SendBufferedAsync(TransportRig.Get(relative), default);

        Assert.Equal(new Uri(expected), Assert.Single(rig.Handler.Requests).Uri);
    }

    [Fact]
    public async Task The_HttpClient_is_neither_modified_nor_disposed()
    {
        using var rig = new TransportRig();
        rig.Handler.Then(Reply.Json(200, "{}")).Then(Reply.Json(200, "{}"));

        await rig.Transport.SendBufferedAsync(TransportRig.Get(), default);
        await rig.Transport.SendBufferedAsync(TransportRig.Get(), default); // un client smaltito lancerebbe

        Assert.Null(rig.Client.BaseAddress);
        Assert.Empty(rig.Client.DefaultRequestHeaders);
        Assert.Equal(Timeout.InfiniteTimeSpan, rig.Client.Timeout);
    }

    [Fact]
    public async Task The_transport_keeps_a_copy_of_the_options_so_changing_them_later_changes_nothing()
    {
        using var rig = new TransportRig();
        rig.Handler.Then(Reply.Json(200, "{}"));
        rig.Options.ApiKey = "bad\r\nX-Injected: 1";
        rig.Options.BaseAddress = new Uri("https://other.example.test/elsewhere/");
        rig.Options.RequestTimeout = TimeSpan.FromTicks(1);
        rig.Options.Retry.MaxAttempts = 1;

        await rig.Transport.SendBufferedAsync(TransportRig.Get(), default);

        var sent = Assert.Single(rig.Handler.Requests);
        Assert.Equal(TransportRig.Key, sent.Header("X-API-Key"));
        Assert.Equal("https://filemaster.example.test/documents", sent.Uri.AbsoluteUri);
        Assert.Null(sent.Header("X-Injected"));
    }

    [Fact]
    public void The_constructor_validates_the_options_and_the_arguments()
    {
        using var client = new HttpClient(new FakeHandler());

        var invalid = Assert.Throws<ArgumentException>(() => new FilemasterTransport(client, new FilemasterOptions()));
        Assert.Equal(nameof(FilemasterOptions.BaseAddress), invalid.ParamName);
        Assert.Throws<ArgumentNullException>(() => new FilemasterTransport(null!, new FilemasterOptions()));
        Assert.Throws<ArgumentNullException>(() => new FilemasterTransport(client, null!));
    }

    [Theory]
    [InlineData("http://filemaster.example.test/", true)]
    [InlineData("http://192.0.2.10:8080/", true)]
    [InlineData("https://filemaster.example.test/", false)]
    [InlineData("http://localhost:5000/", false)]
    [InlineData("http://127.0.0.1:5000/", false)]
    [InlineData("http://[::1]:5000/", false)]
    public void Plain_http_to_a_host_that_is_not_loopback_logs_one_warning_at_creation(string address, bool warns)
    {
        using var rig = new TransportRig(o => o.BaseAddress = new Uri(address));

        var warnings = rig.Log.Records.Where(r => r.Level == LogLevel.Warning).ToArray();

        Assert.Equal(warns ? 1 : 0, warnings.Length);
        Assert.DoesNotContain(TransportRig.Key, rig.Log.Everything, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_http_warning_is_written_once_and_not_for_every_call()
    {
        using var rig = new TransportRig(o => o.BaseAddress = new Uri("http://filemaster.example.test/"));
        rig.Handler.Then(Reply.Json(200, "{}")).Then(Reply.Json(200, "{}"));

        await rig.Transport.SendBufferedAsync(TransportRig.Get(), default);
        await rig.Transport.SendBufferedAsync(TransportRig.Get(), default);

        Assert.Single(rig.Log.Records);
    }

    [Fact]
    public void A_transport_without_a_logger_or_a_time_provider_or_hooks_can_be_created()
    {
        using var client = new HttpClient(new FakeHandler());
        var options = new FilemasterOptions { BaseAddress = new Uri("https://filemaster.example.test/"), ApiKey = TransportRig.Key };

        Assert.NotNull(new FilemasterTransport(client, options));
    }

    // ----- TransportRequest -----

    [Theory]
    [InlineData("/documents")]
    [InlineData("")]
    [InlineData("https://filemaster.example.test/documents")]
    [InlineData("mailto:someone@example.test")]
    public void A_request_path_must_be_relative_and_must_not_start_with_a_slash(string relative)
    {
        var exception = Assert.Throws<ArgumentException>(() => new TransportRequest(HttpMethod.Get, relative));

        Assert.Equal("relativeUri", exception.ParamName);
    }

    [Fact]
    public void A_request_needs_a_method_and_a_path()
    {
        Assert.Throws<ArgumentNullException>(() => new TransportRequest(null!, "documents"));
        Assert.Throws<ArgumentNullException>(() => new TransportRequest(HttpMethod.Get, null!));
        var request = new TransportRequest(new HttpMethod("PATCH"), "documents/doc_X/folder?x=1");
        Assert.Equal("PATCH", request.Method.Method);
        Assert.Equal("documents/doc_X/folder?x=1", request.RelativeUri);
        Assert.Null(request.Customize);
        Assert.Null(request.IsExpectedStatus);
    }

    // ----- argomenti e annullamento in partenza -----

    [Fact]
    public async Task A_null_request_is_an_ArgumentNullException_in_every_mode()
    {
        using var rig = new TransportRig();

        await Assert.ThrowsAsync<ArgumentNullException>(() => rig.Transport.SendBufferedAsync(null!, default));
        await Assert.ThrowsAsync<ArgumentNullException>(() => rig.Transport.SendDownloadAsync(null!, default));
        await Assert.ThrowsAsync<ArgumentNullException>(() => rig.Transport.SendUploadAsync(null!, default));
    }

    [Fact]
    public async Task A_token_already_cancelled_throws_OperationCanceledException_without_sending_anything_in_every_mode()
    {
        using var rig = new TransportRig();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Transport.SendBufferedAsync(TransportRig.Get(), cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Transport.SendDownloadAsync(TransportRig.Get(), cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Transport.SendUploadAsync(TransportRig.Post(), cts.Token));

        Assert.Empty(rig.Handler.Requests);
    }
}
