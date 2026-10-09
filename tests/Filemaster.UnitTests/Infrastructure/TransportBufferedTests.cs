using System.Net;
using System.Net.Sockets;
using System.Text;
using Filemaster.Domain;
using Filemaster.Infrastructure;
using Filemaster.UnitTests.Application;

namespace Filemaster.UnitTests.Infrastructure;

/// <summary>
/// La modalita' "JSON bufferizzato": la risposta intera in memoria (con un tetto), le intestazioni che restano leggibili, gli status
/// dichiarati attesi (il 503 di <c>/readyz</c>), la mappatura degli errori dopo il ciclo, il corpo di errore letto per al piu' 16 KiB,
/// gli errori di rete come <see cref="ConnectionException"/> e il ripiego di <c>RequestId</c> sull'id inviato.
/// </summary>
public sealed class TransportBufferedTests
{
    [Fact]
    public async Task A_200_comes_back_whole_with_status_body_headers_and_the_server_request_id()
    {
        using var rig = new TransportRig();
        var reply = Reply.Json(200, "{\"status\":\"ready\"}").WithHeader("X-Custom", "valore");
        rig.Handler.Then(reply);

        var response = await rig.Transport.SendBufferedAsync(TransportRig.Get("readyz"), default);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("{\"status\":\"ready\"}", Encoding.UTF8.GetString(response.Body));
        Assert.Equal("srv-req-1", response.RequestId);
        Assert.Equal("application/json", response.MediaType);
        Assert.Equal("valore", Assert.Single(response.Headers.GetValues("X-Custom")));
        Assert.Equal(18, response.ContentHeaders.ContentLength);
        Assert.Equal(1, reply.ContentOf().DisposeCount); // la risposta e' smaltita; le intestazioni restano leggibili
    }

    [Fact]
    public async Task The_response_is_read_with_ResponseHeadersRead_so_the_handler_content_is_never_buffered_by_HttpClient()
    {
        using var rig = new TransportRig();
        var reply = Reply.Json(200, "{}");
        rig.Handler.Then(reply);

        await rig.Transport.SendBufferedAsync(TransportRig.Get(), default);

        // Con ResponseContentRead HttpClient avrebbe chiamato LoadIntoBufferAsync, cioe' SerializeToStreamAsync del contenuto.
        Assert.Equal(0, reply.ContentOf().SerializeCalls);
    }

    [Fact]
    public async Task A_204_has_an_empty_body()
    {
        using var rig = new TransportRig();
        rig.Handler.Then(Reply.Empty(204));

        var response = await rig.Transport.SendBufferedAsync(TransportRig.Delete(), default);

        Assert.Equal(204, response.StatusCode);
        Assert.Empty(response.Body);
    }

    [Fact]
    public async Task The_request_id_falls_back_to_the_one_the_client_sent_when_the_response_has_no_header()
    {
        using var rig = new TransportRig();
        rig.Handler.Then(Reply.Json(200, "{}", requestId: null));

        var response = await rig.Transport.SendBufferedAsync(TransportRig.Get(), default);

        Assert.Equal(rig.Handler.Requests[0].Header("X-Request-ID"), response.RequestId);
    }

    [Fact]
    public async Task Customize_sets_what_is_specific_to_the_call_after_the_transport_headers()
    {
        using var rig = new TransportRig();
        rig.Handler.Then(Reply.Json(200, "{}"));
        var request = TransportRig.Post("documents/bulk/verify");
        request.Customize = message =>
        {
            message.Headers.TryAddWithoutValidation("Accept", "application/json");
            message.Content = new StringContent("{\"document_ids\":[]}", Encoding.UTF8, "application/json");
        };

        await rig.Transport.SendBufferedAsync(request, default);

        var sent = Assert.Single(rig.Handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("application/json", sent.Header("Accept"));
        Assert.Equal(TransportRig.Key, sent.Header("X-API-Key"));
    }

    // ----- il tetto sul corpo in memoria -----

    [Fact]
    public async Task A_response_at_the_buffer_limit_is_read_and_one_byte_over_is_an_unexpected_response()
    {
        using var rig = new TransportRig(maxBufferedBytes: 10);
        rig.Handler.Then(Reply.Bytes(200, new byte[10], declaredLength: null)).Then(Reply.Bytes(200, new byte[11], declaredLength: null));

        var atLimit = await rig.Transport.SendBufferedAsync(TransportRig.Get(), default);
        var over = await Assert.ThrowsAsync<UnexpectedResponseException>(() => rig.Transport.SendBufferedAsync(TransportRig.Get(), default));

        Assert.Equal(10, atLimit.Body.Length);
        Assert.Equal(200, over.StatusCode);
        Assert.Contains("10", over.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_declared_length_over_the_limit_is_refused_without_reading_the_body()
    {
        using var rig = new TransportRig(maxBufferedBytes: 10);
        var inner = new ScriptedStream(new byte[1000]);
        rig.Handler.Then(Reply.Streamed(200, inner, declaredLength: 1000));

        var exception = await Assert.ThrowsAsync<UnexpectedResponseException>(() => rig.Transport.SendBufferedAsync(TransportRig.Get(), default));

        Assert.Equal(200, exception.StatusCode);
        Assert.Equal(0, inner.ReadCalls);
    }

    // ----- status attesi (il 503 di /readyz) -----

    [Fact]
    public async Task A_status_the_request_declares_expected_is_returned_not_mapped_and_not_retried()
    {
        using var rig = new TransportRig();
        rig.Handler.Then(Reply.Json(503, "{\"status\":\"unavailable\",\"error\":\"database non raggiungibile\"}"));
        var request = TransportRig.Get("readyz");
        request.IsExpectedStatus = status => status == 200 || status == 503;

        var response = await rig.Transport.SendBufferedAsync(request, default);

        Assert.Equal(503, response.StatusCode);
        Assert.Contains("unavailable", Encoding.UTF8.GetString(response.Body), StringComparison.Ordinal);
        Assert.Single(rig.Handler.Requests); // un solo tentativo anche se un 503 sarebbe transitorio
        Assert.Empty(rig.Delays.Delays);
    }

    [Fact]
    public async Task A_status_outside_the_expected_ones_is_still_mapped()
    {
        using var rig = new TransportRig(o => o.Retry.MaxAttempts = 1);
        rig.Handler.Then(Reply.Problem(500, "internal-error"));
        var request = TransportRig.Get("readyz");
        request.IsExpectedStatus = status => status == 200 || status == 503;

        await Assert.ThrowsAsync<ServerErrorException>(() => rig.Transport.SendBufferedAsync(request, default));
    }

    [Fact]
    public async Task With_a_custom_predicate_a_2xx_that_is_not_in_it_is_mapped_as_unexpected()
    {
        using var rig = new TransportRig();
        rig.Handler.Then(Reply.Json(200, "{}"));
        var request = TransportRig.Get();
        request.IsExpectedStatus = status => status == 206;

        var exception = await Assert.ThrowsAsync<UnexpectedResponseException>(() => rig.Transport.SendBufferedAsync(request, default));

        Assert.Equal(200, exception.StatusCode);
    }

    // ----- errori del server -----

    [Fact]
    public async Task A_problem_json_404_becomes_NotFoundException_with_slug_detail_and_request_id_from_the_body()
    {
        using var rig = new TransportRig();
        var reply = Reply.Problem(404, "not-found", "documento abc non trovato", "body-id-7");
        rig.Handler.Then(reply);

        var exception = await Assert.ThrowsAsync<NotFoundException>(() => rig.Transport.SendBufferedAsync(TransportRig.Get("documents/abc"), default));

        Assert.Equal(404, exception.StatusCode);
        Assert.Equal("not-found", exception.ProblemType);
        Assert.Equal("documento abc non trovato", exception.Detail);
        Assert.Equal("body-id-7", exception.RequestId);
        Assert.Equal(1, reply.ContentOf().DisposeCount);
        Assert.Single(rig.Handler.Requests); // un 404 non si ritenta
    }

    [Theory]
    [InlineData(400, "validation-error", typeof(InvalidRequestException))]
    [InlineData(401, "unauthorized", typeof(UnauthorizedException))]
    [InlineData(403, "forbidden", typeof(ForbiddenException))]
    [InlineData(409, "conflict", typeof(ConflictException))]
    [InlineData(409, "content-unavailable", typeof(ContentUnavailableException))]
    [InlineData(413, "request-too-large", typeof(RequestTooLargeException))]
    [InlineData(415, "unsupported-media-type", typeof(UnsupportedMediaTypeException))]
    [InlineData(415, "error", typeof(UnsupportedMediaTypeException))]
    [InlineData(500, "internal-error", typeof(ServerErrorException))]
    [InlineData(503, "storage-not-configured", typeof(StorageNotConfiguredException))]
    [InlineData(405, "method-not-allowed", typeof(UnexpectedResponseException))]
    public async Task Each_error_the_server_emits_is_mapped_after_a_single_attempt(int status, string slug, Type expected)
    {
        using var rig = new TransportRig();
        rig.Handler.Then(Reply.Problem(status, slug, "motivo"));

        var exception = await Assert.ThrowsAnyAsync<FilemasterException>(() => rig.Transport.SendBufferedAsync(TransportRig.Get(), default));

        Assert.IsType(expected, exception);
        Assert.Equal(status, exception.StatusCode);
        Assert.Equal(slug, exception.ProblemType);
        Assert.Single(rig.Handler.Requests);
    }

    [Fact]
    public async Task A_proxy_error_without_a_body_or_header_gets_the_request_id_the_client_sent()
    {
        using var rig = new TransportRig(o => o.Retry.MaxAttempts = 1);
        rig.Handler.Then(Reply.Empty(502, requestId: null));

        var exception = await Assert.ThrowsAsync<ServerErrorException>(() => rig.Transport.SendBufferedAsync(TransportRig.Get(), default));

        Assert.Equal(rig.Handler.Requests[0].Header("X-Request-ID"), exception.RequestId);
    }

    [Fact]
    public async Task A_response_request_id_that_is_too_long_is_not_trusted_and_the_one_the_client_sent_is_used_instead()
    {
        using var rig = new TransportRig();
        var longest = new string('a', 128);
        rig.Handler.Then(Reply.Json(200, "{}", requestId: longest))
            .Then(Reply.Json(200, "{}", requestId: longest + "a"))
            .Then(Reply.Bytes(200, new byte[3], 3, requestId: longest + "a"));

        var accepted = await rig.Transport.SendBufferedAsync(TransportRig.Get(), default);
        var buffered = await rig.Transport.SendBufferedAsync(TransportRig.Get(), default);
        var download = await rig.Transport.SendDownloadAsync(TransportRig.Get(), default);
        using var content = download.Content;

        Assert.Equal(longest, accepted.RequestId);
        Assert.Equal(rig.Handler.Requests[1].Header("X-Request-ID"), buffered.RequestId);
        Assert.Equal(rig.Handler.Requests[2].Header("X-Request-ID"), download.RequestId);
    }

    [Fact]
    public async Task The_header_request_id_is_the_fallback_when_the_body_has_none()
    {
        using var rig = new TransportRig();
        rig.Handler.Then(Reply.Problem(404, "not-found", requestId: "ignored").WithHeader("X-Request-ID", "header-id-3"));
        // il corpo ha request_id: vince il corpo; poi un caso senza request_id nel corpo
        rig.Handler.Then(Reply.Json(404, "{\"type\":\"/problems/not-found\"}", "application/problem+json", requestId: "header-id-4"));

        var first = await Assert.ThrowsAsync<NotFoundException>(() => rig.Transport.SendBufferedAsync(TransportRig.Get(), default));
        var second = await Assert.ThrowsAsync<NotFoundException>(() => rig.Transport.SendBufferedAsync(TransportRig.Get(), default));

        Assert.Equal("ignored", first.RequestId);
        Assert.Equal("header-id-4", second.RequestId);
    }

    [Fact]
    public async Task An_error_body_is_read_for_at_most_16_KiB_even_when_the_server_sends_a_huge_one()
    {
        using var rig = new TransportRig(o => o.Retry.MaxAttempts = 1);
        var huge = new ScriptedStream(new byte[1024 * 1024], maxChunk: 4096);
        rig.Handler.Then(Reply.Streamed(500, huge, declaredLength: null, mediaType: "application/problem+json"));

        var exception = await Assert.ThrowsAsync<ServerErrorException>(() => rig.Transport.SendBufferedAsync(TransportRig.Get(), default));

        Assert.Null(exception.ProblemType);
        Assert.True(huge.ReadCalls <= 5, $"troppe letture: {huge.ReadCalls}"); // 16 KiB + 1 byte a pezzi da 4096
    }

    [Fact]
    public async Task An_error_body_with_a_declared_length_over_the_limit_is_not_read_at_all()
    {
        using var rig = new TransportRig(o => o.Retry.MaxAttempts = 1);
        var huge = new ScriptedStream(new byte[1024 * 1024]);
        rig.Handler.Then(Reply.Streamed(404, huge, declaredLength: 1024 * 1024, mediaType: "application/problem+json"));

        var exception = await Assert.ThrowsAsync<NotFoundException>(() => rig.Transport.SendBufferedAsync(TransportRig.Get(), default));

        Assert.Equal(0, huge.ReadCalls);
        Assert.Null(exception.ProblemType);
    }

    [Fact]
    public async Task An_error_body_that_cannot_be_read_is_mapped_on_the_status_alone()
    {
        using var rig = new TransportRig(o => o.Retry.MaxAttempts = 1);
        var broken = new ScriptedStream(new byte[100]);
        broken.FailAtRead[0] = new IOException("connessione caduta nel corpo");
        rig.Handler.Then(Reply.Streamed(500, broken, declaredLength: null));

        var exception = await Assert.ThrowsAsync<ServerErrorException>(() => rig.Transport.SendBufferedAsync(TransportRig.Get(), default));

        Assert.Equal(500, exception.StatusCode);
    }

    [Fact]
    public async Task A_garbage_error_body_never_surfaces_a_JSON_exception()
    {
        using var rig = new TransportRig(o => o.Retry.MaxAttempts = 1);
        rig.Handler.Then(Reply.Text(404, "{ this is not json", "application/problem+json"));

        var exception = await Assert.ThrowsAsync<NotFoundException>(() => rig.Transport.SendBufferedAsync(TransportRig.Get(), default));

        Assert.Null(exception.ProblemType);
    }

    // ----- errori di rete -----

    public static TheoryData<string> NetworkFailures() => new()
    {
        "http", "io", "socket", "web", "http-wrapping-web", "http-wrapping-socket",
    };

    private static Exception NetworkFailure(string kind) => kind switch
    {
        "http" => new HttpRequestException("richiesta fallita"),
        "io" => new IOException("connessione interrotta"),
        "socket" => new SocketException((int)SocketError.ConnectionRefused),
        "web" => new WebException("rete caduta"),
        "http-wrapping-web" => new HttpRequestException("avvolge", new WebException("rete caduta")),
        "http-wrapping-socket" => new HttpRequestException("avvolge", new SocketException((int)SocketError.HostNotFound)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    [Theory]
    [MemberData(nameof(NetworkFailures))]
    public async Task A_network_failure_is_a_ConnectionException_with_the_cause_inside_and_the_sent_request_id(string kind)
    {
        using var rig = new TransportRig(o => o.Retry.MaxAttempts = 1);
        var cause = NetworkFailure(kind);
        rig.Handler.ThenFail(cause);

        var exception = await Assert.ThrowsAsync<ConnectionException>(() => rig.Transport.SendBufferedAsync(TransportRig.Get(), default));

        Assert.Same(cause, exception.InnerException);
        Assert.Equal(0, exception.StatusCode);
        Assert.Equal(rig.Handler.Requests[0].Header("X-Request-ID"), exception.RequestId);
    }

    [Fact]
    public async Task A_network_failure_raised_asynchronously_is_the_same()
    {
        using var rig = new TransportRig(o => o.Retry.MaxAttempts = 1);
        rig.Handler.ThenFailAsync(new HttpRequestException("dopo un await"));

        var exception = await Assert.ThrowsAsync<ConnectionException>(() => rig.Transport.SendBufferedAsync(TransportRig.Get(), default));

        Assert.IsType<HttpRequestException>(exception.InnerException);
    }

    [Fact]
    public async Task An_error_that_is_not_a_network_failure_is_a_programming_error_and_passes_through()
    {
        using var rig = new TransportRig();
        rig.Handler.ThenFail(new InvalidOperationException("la richiesta e' gia' stata inviata"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Transport.SendBufferedAsync(TransportRig.Get(), default));
        Assert.Single(rig.Handler.Requests);
    }

    [Fact]
    public async Task A_connection_that_drops_while_the_success_body_is_read_is_a_network_failure()
    {
        using var rig = new TransportRig(o => o.Retry.MaxAttempts = 1);
        var dropping = new ScriptedStream(new byte[100], maxChunk: 10);
        dropping.FailAtRead[2] = new IOException("connessione chiusa a meta' corpo");
        rig.Handler.Then(Reply.Streamed(200, dropping, declaredLength: null));

        var exception = await Assert.ThrowsAsync<ConnectionException>(() => rig.Transport.SendBufferedAsync(TransportRig.Get(), default));

        Assert.IsType<IOException>(exception.InnerException);
    }

    [Fact]
    public async Task ObjectDisposedException_from_a_closed_connection_while_reading_the_body_is_a_network_failure_too()
    {
        using var rig = new TransportRig(o => o.Retry.MaxAttempts = 1);
        var closed = new ScriptedStream(new byte[100], maxChunk: 10);
        closed.FailAtRead[1] = new ObjectDisposedException("connessione");
        rig.Handler.Then(Reply.Streamed(200, closed, declaredLength: null));

        var exception = await Assert.ThrowsAsync<ConnectionException>(() => rig.Transport.SendBufferedAsync(TransportRig.Get(), default));

        Assert.IsType<IOException>(exception.InnerException);
        Assert.IsType<ObjectDisposedException>(exception.InnerException!.InnerException);
    }
}
