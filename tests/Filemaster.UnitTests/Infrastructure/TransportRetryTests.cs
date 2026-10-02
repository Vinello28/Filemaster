using System.Net.Sockets;
using Filemaster.Domain;
using Filemaster.Infrastructure;
using Filemaster.UnitTests.Application;
using Microsoft.Extensions.Logging;

namespace Filemaster.UnitTests.Infrastructure;

/// <summary>
/// I ritentativi del trasporto, a ogni punto della regola: solo <c>GET</c>; solo su errori di connessione, 408, 429 (con
/// <c>Retry-After</c> in secondi e come data) e 502/503/504 che non sono problem+json; mai su POST/PATCH/DELETE, mai su 500, 409 e gli
/// altri 4xx, mai sul 503 di <c>/readyz</c>, mai con <c>MaxAttempts = 1</c>; un messaggio nuovo e lo stesso <c>X-Request-ID</c> a ogni
/// tentativo; backoff e jitter con valori iniettati; l'annullamento durante l'attesa. I casi con 429 e <c>Retry-After</c> sono
/// <b>derivati</b>, non osservati: le API di Sharp-a-File non emettono mai un 429 ne' un <c>Retry-After</c>, ma un reverse proxy o un
/// gateway davanti al server puo' farlo.
/// </summary>
public sealed class TransportRetryTests
{
    private static readonly TimeSpan First = TimeSpan.FromMilliseconds(375); // 500 ms, jitter 0.5

    private static readonly TimeSpan Second = TimeSpan.FromMilliseconds(750); // 1000 ms, jitter 0.5

    // ----- GET: riesce al secondo tentativo -----

    [Theory]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task A_proxy_502_503_or_504_that_is_not_problem_json_is_retried_once_and_succeeds(int status)
    {
        using var rig = new TransportRig();
        rig.Handler.Then(Reply.Text(status)).Then(Reply.Json(200, "{\"ok\":true}"));

        var response = await rig.Transport.SendBufferedAsync(TransportRig.Get(), default);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(2, rig.Handler.Requests.Count);
        Assert.Equal(new[] { First }, rig.Delays.Delays);
    }

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    public async Task A_408_or_429_is_retried_even_when_the_body_is_problem_json(int status)
    {
        using var rig = new TransportRig();
        rig.Handler.Then(Reply.Problem(status, "error")).Then(Reply.Json(200, "{}"));

        var response = await rig.Transport.SendBufferedAsync(TransportRig.Get(), default);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(2, rig.Handler.Requests.Count);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("io")]
    [InlineData("socket")]
    public async Task A_connection_error_is_retried_and_the_second_attempt_can_succeed(string kind)
    {
        using var rig = new TransportRig();
        Exception cause = kind switch
        {
            "http" => new HttpRequestException("connessione rifiutata"),
            "io" => new IOException("connessione interrotta"),
            _ => new SocketException((int)SocketError.ConnectionReset),
        };
        rig.Handler.ThenFail(cause).Then(Reply.Json(200, "{}"));

        var response = await rig.Transport.SendBufferedAsync(TransportRig.Get(), default);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(2, rig.Handler.Requests.Count);
        Assert.Equal(new[] { First }, rig.Delays.Delays);
    }

    [Fact]
    public async Task A_connection_that_drops_while_the_GET_body_is_read_is_retried()
    {
        using var rig = new TransportRig();
        var dropping = new ScriptedStream(new byte[100], maxChunk: 10);
        dropping.FailAtRead[1] = new IOException("connessione chiusa a meta' corpo");
        rig.Handler.Then(Reply.Streamed(200, dropping, declaredLength: null)).Then(Reply.Json(200, "{\"ok\":true}"));

        var response = await rig.Transport.SendBufferedAsync(TransportRig.Get(), default);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(2, rig.Handler.Requests.Count);
    }

    [Fact]
    public async Task Two_failures_then_a_success_waits_the_doubled_backoff_between_them()
    {
        using var rig = new TransportRig();
        rig.Handler.Then(Reply.Text(503)).ThenFail(new HttpRequestException("rete")).Then(Reply.Json(200, "{}"));

        var response = await rig.Transport.SendBufferedAsync(TransportRig.Get(), default);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(3, rig.Handler.Requests.Count);
        Assert.Equal(new[] { First, Second }, rig.Delays.Delays);
    }

    [Fact]
    public async Task With_five_attempts_the_waits_double_each_time_with_the_injected_jitter()
    {
        using var rig = new TransportRig(o => o.Retry.MaxAttempts = 5);
        rig.Jitter = 0.0;
        for (var i = 0; i < 4; i++)
        {
            rig.Handler.Then(Reply.Text(503));
        }

        rig.Handler.Then(Reply.Json(200, "{}"));

        await rig.Transport.SendBufferedAsync(TransportRig.Get(), default);

        Assert.Equal(
            new[] { 250, 500, 1000, 2000 }.Select(ms => TimeSpan.FromMilliseconds(ms)),
            rig.Delays.Delays);
    }

    [Fact]
    public async Task The_jitter_is_asked_for_each_wait_and_changes_the_delay()
    {
        using var rig = new TransportRig(o => o.Retry.MaxAttempts = 3);
        rig.Handler.Then(Reply.Text(503)).Then(Reply.Text(503)).Then(Reply.Json(200, "{}"));
        rig.Jitter = 1.0;

        await rig.Transport.SendBufferedAsync(TransportRig.Get(), default);

        Assert.Equal(new[] { TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1000) }, rig.Delays.Delays);
    }

    // ----- GET: tentativi esauriti, l'ultimo errore viene mappato -----

    [Fact]
    public async Task When_the_attempts_run_out_the_last_error_is_mapped_and_not_the_first()
    {
        using var rig = new TransportRig();
        rig.Handler.Then(Reply.Text(503)).Then(Reply.Text(504)).Then(Reply.Text(502));

        var exception = await Assert.ThrowsAsync<ServerErrorException>(() => rig.Transport.SendBufferedAsync(TransportRig.Get(), default));

        Assert.Equal(502, exception.StatusCode);
        Assert.Equal(3, rig.Handler.Requests.Count);
        Assert.Equal(2, rig.Delays.Delays.Count); // nessuna attesa dopo l'ultimo tentativo
    }

    [Fact]
    public async Task When_the_attempts_run_out_on_connection_errors_the_last_cause_is_inside_the_ConnectionException()
    {
        using var rig = new TransportRig();
        var last = new IOException("terzo");
        rig.Handler.ThenFail(new HttpRequestException("primo")).ThenFail(new HttpRequestException("secondo")).ThenFail(last);

        var exception = await Assert.ThrowsAsync<ConnectionException>(() => rig.Transport.SendBufferedAsync(TransportRig.Get(), default));

        Assert.Same(last, exception.InnerException);
        Assert.Equal(3, rig.Handler.Requests.Count);
        Assert.Equal(rig.Handler.Requests[0].Header("X-Request-ID"), exception.RequestId);
    }

    [Fact]
    public async Task A_429_that_never_goes_away_is_an_unexpected_response_after_the_attempts()
    {
        using var rig = new TransportRig();
        rig.Handler.Then(Reply.Text(429)).Then(Reply.Text(429)).Then(Reply.Text(429));

        var exception = await Assert.ThrowsAsync<UnexpectedResponseException>(() => rig.Transport.SendBufferedAsync(TransportRig.Get(), default));

        Assert.Equal(429, exception.StatusCode);
        Assert.Equal(3, rig.Handler.Requests.Count);
    }

    [Fact]
    public async Task MaxAttempts_one_switches_the_retry_off_for_statuses_and_connection_errors()
    {
        using var rig = new TransportRig(o => o.Retry.MaxAttempts = 1);
        rig.Handler.Then(Reply.Text(503)).ThenFail(new HttpRequestException("rete"));

        await Assert.ThrowsAsync<ServerErrorException>(() => rig.Transport.SendBufferedAsync(TransportRig.Get(), default));
        await Assert.ThrowsAsync<ConnectionException>(() => rig.Transport.SendBufferedAsync(TransportRig.Get(), default));

        Assert.Equal(2, rig.Handler.Requests.Count);
        Assert.Empty(rig.Delays.Delays);
    }

    // ----- Retry-After (derivato: il server non lo emette) -----

    [Fact]
    public async Task Retry_After_in_seconds_sets_the_wait_when_it_is_longer_than_the_backoff()
    {
        using var rig = new TransportRig();
        rig.Handler.Then(Reply.Text(429).WithHeader("Retry-After", "3")).Then(Reply.Json(200, "{}"));

        await rig.Transport.SendBufferedAsync(TransportRig.Get(), default);

        Assert.Equal(new[] { TimeSpan.FromSeconds(3) }, rig.Delays.Delays);
    }

    [Fact]
    public async Task Retry_After_as_an_HTTP_date_is_counted_from_the_clock_of_the_time_provider()
    {
        using var rig = new TransportRig();
        var date = rig.Time.GetUtcNow().AddSeconds(7).ToString("r", System.Globalization.CultureInfo.InvariantCulture);
        rig.Handler.Then(Reply.Text(503).WithHeader("Retry-After", date)).Then(Reply.Json(200, "{}"));

        await rig.Transport.SendBufferedAsync(TransportRig.Get(), default);

        Assert.Equal(new[] { TimeSpan.FromSeconds(7) }, rig.Delays.Delays);
    }

    [Fact]
    public async Task Retry_After_beyond_MaxDelay_is_capped_at_MaxDelay()
    {
        using var rig = new TransportRig(o => o.Retry.MaxDelay = TimeSpan.FromSeconds(4));
        rig.Handler.Then(Reply.Text(429).WithHeader("Retry-After", "3600")).Then(Reply.Json(200, "{}"));

        await rig.Transport.SendBufferedAsync(TransportRig.Get(), default);

        Assert.Equal(new[] { TimeSpan.FromSeconds(4) }, rig.Delays.Delays);
    }

    [Theory]
    [InlineData("soon")]
    [InlineData("-5")]
    [InlineData("")]
    public async Task An_unreadable_Retry_After_falls_back_to_the_backoff(string value)
    {
        using var rig = new TransportRig();
        rig.Handler.Then(Reply.Text(429).WithHeader("Retry-After", value)).Then(Reply.Json(200, "{}"));

        await rig.Transport.SendBufferedAsync(TransportRig.Get(), default);

        Assert.Equal(new[] { First }, rig.Delays.Delays);
    }

    [Fact]
    public async Task A_Retry_After_shorter_than_the_backoff_does_not_shorten_the_wait()
    {
        using var rig = new TransportRig();
        rig.Handler.Then(Reply.Text(429).WithHeader("Retry-After", "0")).Then(Reply.Json(200, "{}"));

        await rig.Transport.SendBufferedAsync(TransportRig.Get(), default);

        Assert.Equal(new[] { First }, rig.Delays.Delays);
    }

    // ----- GET: cio' che non si ritenta -----

    [Theory]
    [InlineData(400, "validation-error")]
    [InlineData(401, "unauthorized")]
    [InlineData(403, "forbidden")]
    [InlineData(404, "not-found")]
    [InlineData(405, "method-not-allowed")]
    [InlineData(409, "conflict")]
    [InlineData(409, "content-unavailable")]
    [InlineData(413, "request-too-large")]
    [InlineData(415, "unsupported-media-type")]
    [InlineData(500, "internal-error")]
    [InlineData(503, "storage-not-configured")]
    [InlineData(502, "error")]
    [InlineData(504, "error")]
    public async Task These_responses_are_never_retried_even_for_a_GET(int status, string slug)
    {
        using var rig = new TransportRig();
        rig.Handler.Then(Reply.Problem(status, slug)).Then(Reply.Json(200, "{}"));

        await Assert.ThrowsAnyAsync<FilemasterException>(() => rig.Transport.SendBufferedAsync(TransportRig.Get(), default));

        Assert.Single(rig.Handler.Requests);
        Assert.Empty(rig.Delays.Delays);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(501)]
    [InlineData(409)]
    [InlineData(404)]
    [InlineData(416)]
    public async Task A_500_a_409_and_the_other_statuses_without_a_body_are_not_retried(int status)
    {
        using var rig = new TransportRig();
        rig.Handler.Then(Reply.Empty(status)).Then(Reply.Json(200, "{}"));

        await Assert.ThrowsAnyAsync<FilemasterException>(() => rig.Transport.SendBufferedAsync(TransportRig.Get(), default));

        Assert.Single(rig.Handler.Requests);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("PUT")]
    [InlineData("HEAD")]
    public async Task A_write_or_any_method_other_than_GET_is_never_retried_on_a_transient_status(string method)
    {
        foreach (var status in new[] { 408, 429, 502, 503, 504 })
        {
            using var rig = new TransportRig();
            rig.Handler.Then(Reply.Text(status)).Then(Reply.Json(200, "{}"));

            await Assert.ThrowsAnyAsync<FilemasterException>(() => rig.Transport.SendBufferedAsync(new TransportRequest(new HttpMethod(method), "documents/doc_X"), default));

            Assert.Single(rig.Handler.Requests);
            Assert.Empty(rig.Delays.Delays);
        }
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task A_write_that_hits_a_connection_error_is_not_retried_because_its_outcome_may_be_unknown(string method)
    {
        using var rig = new TransportRig();
        rig.Handler.ThenFail(new HttpRequestException("rete")).Then(Reply.Json(200, "{}"));

        await Assert.ThrowsAsync<ConnectionException>(() => rig.Transport.SendBufferedAsync(new TransportRequest(new HttpMethod(method), "documents/doc_X"), default));

        Assert.Single(rig.Handler.Requests);
        Assert.Empty(rig.Delays.Delays);
    }

    [Fact]
    public async Task A_download_that_is_not_a_GET_is_never_retried_whatever_goes_wrong()
    {
        using var transient = new TransportRig();
        transient.Handler.Then(Reply.Text(503)).Then(Reply.Bytes(200, new byte[3], 3));
        using var dropped = new TransportRig();
        dropped.Handler.ThenFail(new HttpRequestException("rete")).Then(Reply.Bytes(200, new byte[3], 3));

        await Assert.ThrowsAsync<ServerErrorException>(() => transient.Transport.SendDownloadAsync(TransportRig.Post("documents/doc_X/content"), default));
        await Assert.ThrowsAsync<ConnectionException>(() => dropped.Transport.SendDownloadAsync(TransportRig.Post("documents/doc_X/content"), default));

        Assert.Single(transient.Handler.Requests);
        Assert.Single(dropped.Handler.Requests);
        Assert.Empty(transient.Delays.Delays);
        Assert.Empty(dropped.Delays.Delays);
    }

    [Fact]
    public async Task A_write_whose_response_body_drops_is_not_retried_either()
    {
        using var rig = new TransportRig();
        var dropping = new ScriptedStream(new byte[100], maxChunk: 10);
        dropping.FailAtRead[1] = new IOException("rete");
        rig.Handler.Then(Reply.Streamed(200, dropping, declaredLength: null)).Then(Reply.Json(200, "{}"));

        await Assert.ThrowsAsync<ConnectionException>(() => rig.Transport.SendBufferedAsync(TransportRig.Delete(), default));

        Assert.Single(rig.Handler.Requests);
    }

    [Fact]
    public async Task The_readyz_503_is_not_retried_because_it_is_the_outcome_of_the_probe()
    {
        using var rig = new TransportRig();
        rig.Handler.Then(Reply.Json(503, "{\"status\":\"unavailable\"}")).Then(Reply.Json(200, "{\"status\":\"ready\"}"));
        var request = TransportRig.Get("readyz");
        request.IsExpectedStatus = status => status == 200 || status == 503;

        var response = await rig.Transport.SendBufferedAsync(request, default);

        Assert.Equal(503, response.StatusCode);
        Assert.Single(rig.Handler.Requests);
        Assert.Empty(rig.Delays.Delays);
    }

    [Fact]
    public async Task The_healthz_probe_is_a_GET_and_is_retried_like_any_other()
    {
        using var rig = new TransportRig();
        rig.Handler.Then(Reply.Text(503)).Then(Reply.Json(200, "ok", "text/plain"));
        var request = TransportRig.Get("healthz");

        var response = await rig.Transport.SendBufferedAsync(request, default);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(2, rig.Handler.Requests.Count);
    }

    // ----- il tentativo ritentato -----

    [Fact]
    public async Task Every_attempt_uses_a_new_request_message_but_the_same_request_id()
    {
        using var rig = new TransportRig();
        rig.Handler.Then(Reply.Text(503)).ThenFail(new HttpRequestException("rete")).Then(Reply.Json(200, "{}"));
        var customized = new List<HttpRequestMessage>();
        var request = TransportRig.Get();
        request.Customize = message =>
        {
            customized.Add(message);
            message.Headers.TryAddWithoutValidation("X-Attempt", customized.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        };

        await rig.Transport.SendBufferedAsync(request, default);

        Assert.Equal(3, rig.Handler.Requests.Count);
        Assert.Equal(3, customized.Count); // Customize su ogni messaggio nuovo
        Assert.Equal(3, rig.Handler.Requests.Select(r => r.Message).Distinct().Count());
        Assert.Equal(new[] { "1", "2", "3" }, rig.Handler.Requests.Select(r => r.Header("X-Attempt")));
        var ids = rig.Handler.Requests.Select(r => r.Header("X-Request-ID")).Distinct().ToArray();
        Assert.Single(ids);
        Assert.Equal(32, ids[0]!.Length);
        Assert.All(rig.Handler.Requests, r => Assert.Equal(TransportRig.Key, r.Header("X-API-Key")));
    }

    [Fact]
    public async Task The_response_of_a_failed_attempt_is_released_before_the_wait_and_the_next_attempt()
    {
        using var rig = new TransportRig();
        var first = Reply.Text(503);
        first.ContentOf().OnDispose = () => rig.Handler.Events.Add("dispose1");
        rig.Delays.OnDelay = _ => rig.Handler.Events.Add("delay");
        rig.Handler.Then(first).Then(Reply.Json(200, "{}"));

        await rig.Transport.SendBufferedAsync(TransportRig.Get(), default);

        Assert.Equal(new[] { "send1", "dispose1", "delay", "send2" }, rig.Handler.Events);
    }

    [Fact]
    public async Task Each_retry_writes_a_warning_without_the_key_and_with_the_attempt_and_the_request_id()
    {
        using var rig = new TransportRig();
        rig.Handler.Then(Reply.Text(503)).ThenFail(new HttpRequestException("rete")).Then(Reply.Json(200, "{}"));

        await rig.Transport.SendBufferedAsync(TransportRig.Get("documents?folder_id=FATTURE"), default);

        var warnings = rig.Log.Records.Where(r => r.Level == LogLevel.Warning).ToArray();
        Assert.Equal(2, warnings.Length);
        Assert.Contains("503", warnings[0].Text, StringComparison.Ordinal);
        Assert.Contains("connessione", warnings[1].Text, StringComparison.Ordinal);
        Assert.Contains("GET documents", warnings[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("FATTURE", warnings[0].Text, StringComparison.Ordinal); // la query non si logga
        Assert.Contains(rig.Handler.Requests[0].Header("X-Request-ID")!, warnings[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain(TransportRig.Key, rig.Log.Everything, StringComparison.Ordinal);
    }

    // ----- annullamento durante l'attesa -----

    [Fact]
    public async Task Cancelling_during_the_backoff_throws_OperationCanceledException_and_sends_nothing_more()
    {
        var entered = Waiting.NewSignal();
        using var cts = new CancellationTokenSource();
        using var rig = new TransportRig(delay: async (_, token) =>
        {
            entered.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        rig.Handler.Then(Reply.Text(503)).Then(Reply.Json(200, "{}"));

        var call = rig.Transport.SendBufferedAsync(TransportRig.Get(), cts.Token);
        await entered.Task;
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Single(rig.Handler.Requests);
    }

    [Fact]
    public async Task Cancelling_the_caller_token_when_the_delay_hook_ignores_it_still_ends_in_cancellation()
    {
        using var cts = new CancellationTokenSource();
        using var rig = new TransportRig(delay: (_, _) =>
        {
            cts.Cancel(); // il chiamante annulla durante l'attesa; il gancio completa comunque
            return Task.CompletedTask;
        });
        rig.Handler.Then(Reply.Text(503)).Then(Reply.Json(200, "{}"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Transport.SendBufferedAsync(TransportRig.Get(), cts.Token));

        Assert.Single(rig.Handler.Requests); // il secondo tentativo parte con il token gia' scattato: il gestore non lo invia
    }

    // ----- il tempo scaduto non si ritenta -----

    [Fact]
    public async Task A_client_side_timeout_is_not_retried()
    {
        using var rig = new TransportRig();
        rig.Handler.ThenFail(new TaskCanceledException("HttpClient.Timeout")).Then(Reply.Json(200, "{}"));

        var exception = await Assert.ThrowsAsync<FilemasterTimeoutException>(() => rig.Transport.SendBufferedAsync(TransportRig.Get(), default));

        Assert.Single(rig.Handler.Requests);
        Assert.Equal(0, exception.StatusCode);
    }
}
