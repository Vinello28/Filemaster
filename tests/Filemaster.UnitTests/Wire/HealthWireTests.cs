using System.Text;
using Filemaster.Application;
using Filemaster.Domain;
using Filemaster.Infrastructure;

namespace Filemaster.UnitTests.Wire;

/// <summary>
/// Le sonde di salute lette dalle risposte catturate (fixture 01 <c>/healthz</c>, 02 <c>/readyz</c> 200, 224 <c>/readyz</c> 503): il 503 non e'
/// problem+json e non e' un errore, e <see cref="HealthProbeResult.IsHealthy"/> dipende dallo status HTTP, non dal testo.
/// </summary>
public sealed class HealthWireTests
{
    private static HealthProbeResult Liveness(byte[] body, int status = 200) => HealthWire.ReadLiveness(body, WireTest.Context(status));

    private static HealthProbeResult Readiness(byte[] body, int status) => HealthWire.ReadReadiness(body, WireTest.Context(status));

    [Fact]
    public void The_captured_liveness_answer_is_ok_and_healthy()
    {
        // 01-healthz: 200, text/plain, corpo "ok"
        var result = Liveness(WireFixtures.Captured("01-healthz", "txt"));

        Assert.True(result.IsHealthy);
        Assert.Equal("ok", result.Status);
        Assert.Null(result.Detail);
        Assert.Equal(200, WireFixtures.Status("01-healthz"));
    }

    [Fact]
    public void The_captured_ready_answer_is_healthy_with_status_ready()
    {
        // 02-readyz: 200 {"status":"ready"}
        var result = Readiness(WireFixtures.Captured("02-readyz"), WireFixtures.Status("02-readyz"));

        Assert.True(result.IsHealthy);
        Assert.Equal("ready", result.Status);
        Assert.Null(result.Detail);
    }

    [Fact]
    public void The_captured_503_is_an_unhealthy_result_with_the_reason_and_not_an_error()
    {
        // 224-readyz-503-db-down: 503 {"status":"unavailable","error":"database non raggiungibile"} (application/json, non problem+json)
        Assert.Equal(503, WireFixtures.Status("224-readyz-503-db-down"));

        var result = Readiness(WireFixtures.Captured("224-readyz-503-db-down"), 503);

        Assert.False(result.IsHealthy);
        Assert.Equal("unavailable", result.Status);
        Assert.Equal("database non raggiungibile", result.Detail);
    }

    [Fact]
    public void Healthy_depends_on_the_HTTP_status_and_never_on_the_text()
    {
        var okButUnavailable = Readiness(WireTest.Utf8("{\"status\":\"unavailable\"}"), 200);
        var unavailableButReady = Readiness(WireTest.Utf8("{\"status\":\"ready\"}"), 503);

        Assert.True(okButUnavailable.IsHealthy);
        Assert.Equal("unavailable", okButUnavailable.Status);
        Assert.False(unavailableButReady.IsHealthy);
        Assert.Equal("ready", unavailableButReady.Status);
    }

    [Theory]
    [InlineData(204)]
    [InlineData(404)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(504)]
    [InlineData(0)]
    public void A_readiness_status_that_is_neither_200_nor_503_is_not_interpretable(int status)
    {
        WireTest.Unexpected(() => Readiness(WireTest.Utf8("{\"status\":\"ready\"}"), status), status);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"status\":null}")]
    [InlineData("{\"status\":5}")]
    [InlineData("{\"error\":\"x\"}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("<html>Service Unavailable</html>")]
    [InlineData("")]
    public void A_readiness_body_without_a_text_status_is_not_interpretable_even_with_a_503(string body)
    {
        WireTest.Unexpected(() => Readiness(WireTest.Utf8(body), 200));
        WireTest.Unexpected(() => Readiness(WireTest.Utf8(body), 503), 503);
    }

    [Fact]
    public void A_non_text_error_field_is_not_interpretable_and_a_missing_one_is_null()
    {
        WireTest.Unexpected(() => Readiness(WireTest.Utf8("{\"status\":\"unavailable\",\"error\":5}"), 503), 503);
        Assert.Null(Readiness(WireTest.Utf8("{\"status\":\"unavailable\"}"), 503).Detail);
        Assert.Null(Readiness(WireTest.Utf8("{\"status\":\"unavailable\",\"error\":null}"), 503).Detail);
    }

    [Fact]
    public void A_liveness_status_is_the_trimmed_body_whatever_the_server_writes()
    {
        Assert.Equal("ok", Liveness(WireTest.Utf8("ok\n")).Status);
        Assert.Equal("ok", Liveness(WireTest.Utf8("  ok  ")).Status);
        Assert.Equal("alive", Liveness(WireTest.Utf8("alive")).Status);
        Assert.Equal(new string('x', 64), Liveness(WireTest.Utf8(new string('x', 64))).Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n")]
    public void An_empty_liveness_body_is_not_a_probe_answer(string body)
    {
        WireTest.Unexpected(() => Liveness(WireTest.Utf8(body)));
    }

    [Fact]
    public void A_missing_liveness_body_is_not_a_probe_answer()
    {
        WireTest.Unexpected(() => HealthWire.ReadLiveness(null, WireTest.Context()));
    }

    [Fact]
    public void A_liveness_body_that_is_too_long_or_has_control_characters_or_invalid_UTF8_is_not_a_probe_answer()
    {
        // Una pagina di un proxy con status 200 non e' la risposta di /healthz.
        WireTest.Unexpected(() => Liveness(WireTest.Utf8(new string('x', 65))));
        WireTest.Unexpected(() => Liveness(WireTest.Utf8("<html>\n<body>ok</body>\n</html>")));
        WireTest.Unexpected(() => Liveness(WireTest.Utf8("o\0k")));
        WireTest.Unexpected(() => Liveness(WireTest.Utf8("o\U0000007Fk")));
        var invalid = WireTest.Unexpected(() => Liveness(new byte[] { 0x6F, 0xFF, 0x6B }));
        Assert.IsAssignableFrom<ArgumentException>(invalid.InnerException);
    }

    [Fact]
    public void A_liveness_with_a_byte_order_mark_is_read_without_it()
    {
        var body = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.ASCII.GetBytes("ok")).ToArray();

        Assert.Equal("ok", Liveness(body).Status);
    }
}
