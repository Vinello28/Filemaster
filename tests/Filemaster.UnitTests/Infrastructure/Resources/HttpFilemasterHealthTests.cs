using Filemaster.Application;
using Filemaster.Domain;
using Filemaster.UnitTests.Infrastructure.Documents;
using Filemaster.UnitTests.Wire;

namespace Filemaster.UnitTests.Infrastructure.Resources;

/// <summary>
/// L'adapter delle sonde: <c>GET /healthz</c> e <c>GET /readyz</c> <b>senza chiave API</b> (come le catture 01, 02 e 223), il 503 di
/// <c>/readyz</c> come esito (mai eccezione, mai ritentato), e la mappatura delle risposte che la sonda non prevede: un corpo che non e'
/// quello della sonda con 200 e' <see cref="UnexpectedResponseException"/>, con 503 e' un errore del server
/// (<see cref="ServerErrorException"/>); gli altri 5xx seguono le regole comuni (502/503/504 di un proxy ritentati, 500 no).
/// </summary>
public sealed class HttpFilemasterHealthTests
{
    // ----- /healthz -----

    [Fact]
    public async Task Liveness_is_an_anonymous_GET_of_healthz_and_reads_capture_01()
    {
        using var rig = new ResourceRig();
        rig.Then(ResourceRig.Healthz);

        var result = await rig.Health.CheckLivenessAsync();

        var sent = rig.Single;
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal("https://filemaster.example.test/healthz", sent.Uri.AbsoluteUri);
        Assert.Equal(WireFixtures.RequestPath("01-healthz"), sent.PathAndQuery);
        Assert.Null(sent.Header("X-API-Key"));
        Assert.Null(sent.Header("Authorization"));
        Assert.NotNull(sent.Header("X-Request-ID"));
        Assert.NotNull(sent.Header("User-Agent"));
        Assert.Equal("text/plain", sent.Header("Accept"));
        Assert.Null(sent.Body);
        Assert.Equal(new HealthProbeResult(true, "ok", null), result);
    }

    [Fact]
    public async Task Liveness_is_retried_on_a_connection_error_and_the_retry_is_anonymous_too()
    {
        using var rig = new ResourceRig();
        rig.ThenFail(new HttpRequestException("rete"));
        rig.Then(ResourceRig.Healthz);

        var result = await rig.Health.CheckLivenessAsync();

        Assert.Equal(2, rig.Sent.Count);
        Assert.All(rig.Sent, sent => Assert.Null(sent.Header("X-API-Key")));
        Assert.True(result.IsHealthy);
    }

    [Fact]
    public async Task Liveness_is_retried_on_a_503_of_a_proxy_and_after_the_last_attempt_is_ServerErrorException()
    {
        using var rig = new ResourceRig();
        rig.Then(() => Reply.Text(503)).Then(() => Reply.Text(503)).Then(() => Reply.Text(503));

        var exception = await Assert.ThrowsAsync<ServerErrorException>(() => rig.Health.CheckLivenessAsync());

        Assert.Equal(503, exception.StatusCode);
        Assert.Equal(3, rig.Sent.Count);
    }

    [Fact]
    public async Task Liveness_accepts_only_200_a_201_with_ok_is_UnexpectedResponseException()
    {
        using var rig = new ResourceRig();
        rig.Then(() => Reply.Text(201, "ok", "text/plain"));

        var exception = await Assert.ThrowsAsync<UnexpectedResponseException>(() => rig.Health.CheckLivenessAsync());

        Assert.Equal(201, exception.StatusCode);
        Assert.Single(rig.Sent);
    }

    [Fact]
    public async Task Liveness_with_a_page_instead_of_a_status_text_is_UnexpectedResponseException()
    {
        using var rig = new ResourceRig();
        rig.Then(() => Reply.Text(200, "<html>\n<body>pagina del proxy</body>\n</html>"));

        var exception = await Assert.ThrowsAsync<UnexpectedResponseException>(() => rig.Health.CheckLivenessAsync());

        Assert.Equal(200, exception.StatusCode);
    }

    [Fact]
    public async Task Liveness_with_a_500_is_ServerErrorException_and_is_not_retried()
    {
        using var rig = new ResourceRig();
        rig.Then(() => Reply.Problem(500, "internal-error"));

        await Assert.ThrowsAsync<ServerErrorException>(() => rig.Health.CheckLivenessAsync());
        Assert.Single(rig.Sent);
    }

    // ----- /readyz -----

    [Fact]
    public async Task Readiness_is_an_anonymous_GET_of_readyz_and_reads_capture_02()
    {
        using var rig = new ResourceRig();
        rig.Then(() => FixtureReply.Json("02-readyz"));

        var result = await rig.Health.CheckReadinessAsync();

        var sent = rig.Single;
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal("https://filemaster.example.test/readyz", sent.Uri.AbsoluteUri);
        Assert.Equal(WireFixtures.RequestPath("02-readyz"), sent.PathAndQuery);
        Assert.Null(sent.Header("X-API-Key"));
        Assert.Null(sent.Header("Authorization"));
        Assert.NotNull(sent.Header("X-Request-ID"));
        Assert.Equal("application/json", sent.Header("Accept"));
        Assert.Null(sent.Body);
        Assert.Equal(new HealthProbeResult(true, "ready", null), result);
    }

    [Fact]
    public async Task The_503_of_capture_223_is_an_outcome_not_an_exception_and_is_not_retried()
    {
        using var rig = new ResourceRig();
        rig.Then(() => FixtureReply.Json("223-readyz-503-db-down"));
        rig.Then(() => FixtureReply.Json("02-readyz"));
        rig.Then(() => FixtureReply.Json("02-readyz"));

        var result = await rig.Health.CheckReadinessAsync();

        Assert.Equal(503, WireFixtures.Status("223-readyz-503-db-down"));
        Assert.Equal(new HealthProbeResult(false, "unavailable", "database non raggiungibile"), result);
        Assert.Single(rig.Sent);
        Assert.Empty(rig.Rig.Delays.Delays);
    }

    [Fact]
    public async Task A_503_of_the_probe_without_error_has_no_detail()
    {
        using var rig = new ResourceRig();
        rig.Then(() => Reply.Json(503, "{\"status\":\"unavailable\"}"));

        var result = await rig.Health.CheckReadinessAsync();

        Assert.Equal(new HealthProbeResult(false, "unavailable", null), result);
    }

    public static TheoryData<string, string> NotAProbe503 => new()
    {
        { "<html>Service Unavailable</html>", "text/html" },
        { string.Empty, "text/plain" },
        { "{\"status\":503}", "application/json" },
        { "[\"unavailable\"]", "application/json" },
        { "{\"type\":\"/problems/error\",\"title\":\"errore\",\"status\":503,\"request_id\":\"srv-503\"}", "application/problem+json" },
    };

    [Theory]
    [MemberData(nameof(NotAProbe503))]
    public async Task A_503_whose_body_is_not_the_probe_is_ServerErrorException_and_is_not_retried(string body, string mediaType)
    {
        using var rig = new ResourceRig();
        rig.Then(() => Reply.Text(503, body, mediaType));
        rig.Then(() => FixtureReply.Json("02-readyz"));

        var exception = await Assert.ThrowsAsync<ServerErrorException>(() => rig.Health.CheckReadinessAsync());

        Assert.Equal(503, exception.StatusCode);
        Assert.Single(rig.Sent);
        Assert.Empty(rig.Rig.Delays.Delays);
        Assert.False(string.IsNullOrEmpty(exception.RequestId));
    }

    [Fact]
    public async Task A_503_problem_json_keeps_the_request_id_and_the_slug_of_its_body()
    {
        using var rig = new ResourceRig();
        rig.Then(() => Reply.Problem(503, "error", requestId: "srv-503"));

        var exception = await Assert.ThrowsAsync<ServerErrorException>(() => rig.Health.CheckReadinessAsync());

        Assert.Equal("srv-503", exception.RequestId);
        Assert.Equal("error", exception.ProblemType);
    }

    [Fact]
    public async Task A_503_that_is_not_the_probe_carries_the_request_id_that_was_sent()
    {
        using var rig = new ResourceRig();
        rig.Then(() => Reply.Text(503));

        var exception = await Assert.ThrowsAsync<ServerErrorException>(() => rig.Health.CheckReadinessAsync());

        Assert.Equal(rig.Single.Header("X-Request-ID"), exception.RequestId);
    }

    [Fact]
    public async Task A_200_whose_body_is_not_the_probe_is_UnexpectedResponseException_with_status_200()
    {
        using var rig = new ResourceRig();
        rig.Then(() => Reply.Text(200, "ready", "text/plain"));

        var exception = await Assert.ThrowsAsync<UnexpectedResponseException>(() => rig.Health.CheckReadinessAsync());

        Assert.Equal(200, exception.StatusCode);
    }

    [Fact]
    public async Task Another_2xx_is_not_an_outcome_of_the_probe()
    {
        using var rig = new ResourceRig();
        rig.Then(() => Reply.Json(202, "{\"status\":\"ready\"}"));

        var exception = await Assert.ThrowsAsync<UnexpectedResponseException>(() => rig.Health.CheckReadinessAsync());

        Assert.Equal(202, exception.StatusCode);
    }

    [Fact]
    public async Task A_500_is_ServerErrorException_and_is_not_retried()
    {
        using var rig = new ResourceRig();
        rig.Then(() => Reply.Problem(500, "internal-error"));
        rig.Then(() => FixtureReply.Json("02-readyz"));

        await Assert.ThrowsAsync<ServerErrorException>(() => rig.Health.CheckReadinessAsync());
        Assert.Single(rig.Sent);
    }

    [Theory]
    [InlineData(502)]
    [InlineData(504)]
    public async Task A_502_or_504_of_a_proxy_is_retried_like_any_GET(int status)
    {
        using var rig = new ResourceRig();
        rig.Then(() => Reply.Text(status));
        rig.Then(() => FixtureReply.Json("223-readyz-503-db-down"));

        var result = await rig.Health.CheckReadinessAsync();

        Assert.Equal(2, rig.Sent.Count);
        Assert.False(result.IsHealthy);
    }

    [Fact]
    public async Task Readiness_is_retried_on_a_connection_error_and_the_retry_is_anonymous_too()
    {
        using var rig = new ResourceRig();
        rig.ThenFail(new HttpRequestException("rete"));
        rig.Then(() => FixtureReply.Json("02-readyz"));

        var result = await rig.Health.CheckReadinessAsync();

        Assert.Equal(2, rig.Sent.Count);
        Assert.All(rig.Sent, sent => Assert.Null(sent.Header("X-API-Key")));
        Assert.True(result.IsHealthy);
    }

    [Fact]
    public async Task When_the_server_never_answers_readiness_is_ConnectionException()
    {
        using var rig = new ResourceRig();
        rig.ThenFail(new HttpRequestException("rete")).ThenFail(new HttpRequestException("rete")).ThenFail(new HttpRequestException("rete"));

        await Assert.ThrowsAsync<ConnectionException>(() => rig.Health.CheckReadinessAsync());
        Assert.Equal(3, rig.Sent.Count);
    }
}
