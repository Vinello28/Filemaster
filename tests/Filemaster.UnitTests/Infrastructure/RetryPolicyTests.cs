using System.Net.Http.Headers;
using Filemaster.Infrastructure;

namespace Filemaster.UnitTests.Infrastructure;

/// <summary>
/// <see cref="RetryPolicy"/>: quali status sono transitori (408, 429 e 502/503/504 che non sono problem+json; mai 500, 409, gli altri
/// 4xx), il backoff esponenziale con jitter calcolato su valori iniettati, il tetto <c>MaxDelay</c>, e come si legge <c>Retry-After</c>
/// (secondi, data HTTP, valori illeggibili). I casi con <c>429</c> e <c>Retry-After</c> sono <b>derivati</b>, non osservati sul
/// server: le API di Sharp-a-File non emettono mai un 429 ne' un <c>Retry-After</c> (esiste un 429 solo sul login della dashboard,
/// fuori dall'API); li puo' emettere un reverse proxy o un gateway davanti al server.
/// </summary>
public sealed class RetryPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    // ----- status -----

    [Theory]
    [InlineData(408, null)]
    [InlineData(408, "application/problem+json")]
    [InlineData(429, null)]
    [InlineData(429, "application/problem+json")]
    [InlineData(429, "text/html")]
    [InlineData(502, null)]
    [InlineData(502, "text/html")]
    [InlineData(503, "text/html")]
    [InlineData(503, "text/plain")]
    [InlineData(503, null)]
    [InlineData(503, "application/json")]
    [InlineData(504, "text/html")]
    [InlineData(504, null)]
    public void Transient_statuses_are_retryable(int status, string? mediaType)
    {
        Assert.True(RetryPolicy.IsRetryableStatus(status, mediaType));
    }

    [Theory]
    [InlineData(502, "application/problem+json")]
    [InlineData(503, "application/problem+json")]
    [InlineData(503, "Application/Problem+JSON")]
    [InlineData(504, "application/problem+json")]
    public void A_502_503_or_504_that_is_problem_json_is_the_server_itself_and_is_not_retried(int status, string mediaType)
    {
        Assert.False(RetryPolicy.IsRetryableStatus(status, mediaType));
    }

    [Theory]
    [InlineData(200)]
    [InlineData(204)]
    [InlineData(301)]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(405)]
    [InlineData(409)]
    [InlineData(413)]
    [InlineData(415)]
    [InlineData(416)]
    [InlineData(422)]
    [InlineData(500)]
    [InlineData(501)]
    [InlineData(505)]
    [InlineData(599)]
    public void Every_other_status_is_never_retried_whatever_the_body(int status)
    {
        Assert.False(RetryPolicy.IsRetryableStatus(status, null));
        Assert.False(RetryPolicy.IsRetryableStatus(status, "text/html"));
        Assert.False(RetryPolicy.IsRetryableStatus(status, "application/problem+json"));
    }

    [Fact]
    public void IsProblemJson_ignores_case_and_requires_the_exact_media_type()
    {
        Assert.True(RetryPolicy.IsProblemJson("application/problem+json"));
        Assert.True(RetryPolicy.IsProblemJson("APPLICATION/PROBLEM+JSON"));
        Assert.False(RetryPolicy.IsProblemJson("application/json"));
        Assert.False(RetryPolicy.IsProblemJson("application/problem+xml"));
        Assert.False(RetryPolicy.IsProblemJson("application/problem+json; charset=utf-8")); // il tipo arriva gia' senza parametri
        Assert.False(RetryPolicy.IsProblemJson(null));
    }

    // ----- backoff -----

    // initial 500 ms, max 10 s: la parte fissa e' meta' del backoff, il jitter aggiunge da 0 a un'altra meta'.
    public static TheoryData<int, double, long> BackoffTable() => new()
    {
        { 1, 0.0, 250 },
        { 1, 0.5, 375 },
        { 1, 1.0, 500 },
        { 2, 0.0, 500 },
        { 2, 0.5, 750 },
        { 3, 0.0, 1000 },
        { 3, 0.5, 1500 },
        { 4, 0.5, 3000 },
        { 5, 0.0, 4000 },
        { 5, 0.5, 6000 },
        { 6, 0.0, 5000 },    // 500 * 2^5 = 16000 supera il tetto: base 10000
        { 6, 0.5, 7500 },
        { 6, 1.0, 10000 },
        { 10, 0.5, 7500 },
        { 1, -3.0, 250 },    // un jitter fuori da [0, 1] si porta agli estremi
        { 1, 7.0, 500 },
        { 1, double.NaN, 250 },
    };

    [Theory]
    [MemberData(nameof(BackoffTable))]
    public void The_backoff_doubles_with_every_attempt_up_to_the_cap_and_the_jitter_adds_up_to_half(int failedAttempt, double jitter, long expectedMilliseconds)
    {
        var delay = RetryPolicy.ComputeDelay(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(10), failedAttempt, jitter, retryAfter: null);

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), delay);
    }

    [Fact]
    public void A_zero_initial_delay_means_an_immediate_retry_unless_the_server_asked_for_more()
    {
        Assert.Equal(TimeSpan.Zero, RetryPolicy.ComputeDelay(TimeSpan.Zero, TimeSpan.Zero, 3, 0.7, null));
        Assert.Equal(TimeSpan.Zero, RetryPolicy.ComputeDelay(TimeSpan.Zero, TimeSpan.FromSeconds(5), 3, 0.7, null));
        Assert.Equal(TimeSpan.FromSeconds(2), RetryPolicy.ComputeDelay(TimeSpan.Zero, TimeSpan.FromSeconds(5), 3, 0.7, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void The_longest_accepted_delays_do_not_overflow()
    {
        var longest = TimeSpan.FromMilliseconds(int.MaxValue);

        var delay = RetryPolicy.ComputeDelay(longest, longest, FilemasterRetryOptions.MaxAllowedAttempts, 1.0, retryAfter: null);

        Assert.Equal(longest, delay);
    }

    [Fact]
    public void Retry_After_longer_than_the_backoff_wins_but_never_beyond_MaxDelay()
    {
        var initial = TimeSpan.FromMilliseconds(500);
        var max = TimeSpan.FromSeconds(10);

        // backoff dopo il primo tentativo con jitter 0.5: 375 ms
        Assert.Equal(TimeSpan.FromSeconds(3), RetryPolicy.ComputeDelay(initial, max, 1, 0.5, TimeSpan.FromSeconds(3)));
        Assert.Equal(TimeSpan.FromSeconds(10), RetryPolicy.ComputeDelay(initial, max, 1, 0.5, TimeSpan.FromSeconds(10)));
        Assert.Equal(TimeSpan.FromSeconds(10), RetryPolicy.ComputeDelay(initial, max, 1, 0.5, TimeSpan.FromSeconds(120)));
        Assert.Equal(TimeSpan.FromSeconds(10), RetryPolicy.ComputeDelay(initial, max, 1, 0.5, TimeSpan.MaxValue));
    }

    [Fact]
    public void A_Retry_After_shorter_than_the_backoff_does_not_shorten_it()
    {
        var initial = TimeSpan.FromMilliseconds(500);
        var max = TimeSpan.FromSeconds(10);

        Assert.Equal(TimeSpan.FromMilliseconds(375), RetryPolicy.ComputeDelay(initial, max, 1, 0.5, TimeSpan.FromMilliseconds(100)));
        Assert.Equal(TimeSpan.FromMilliseconds(375), RetryPolicy.ComputeDelay(initial, max, 1, 0.5, TimeSpan.Zero));
    }

    // ----- Retry-After -----

    [Fact]
    public void Retry_After_in_seconds_is_the_delta()
    {
        var header = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));

        Assert.Equal(TimeSpan.FromSeconds(7), RetryPolicy.ReadRetryAfter(header, Now));
        Assert.Equal(TimeSpan.Zero, RetryPolicy.ReadRetryAfter(new RetryConditionHeaderValue(TimeSpan.Zero), Now));
    }

    [Fact]
    public void Retry_After_as_an_HTTP_date_is_counted_from_now_and_a_past_date_is_zero()
    {
        Assert.Equal(TimeSpan.FromSeconds(7), RetryPolicy.ReadRetryAfter(new RetryConditionHeaderValue(Now.AddSeconds(7)), Now));
        Assert.Equal(TimeSpan.Zero, RetryPolicy.ReadRetryAfter(new RetryConditionHeaderValue(Now.AddSeconds(-30)), Now));
        Assert.Equal(TimeSpan.Zero, RetryPolicy.ReadRetryAfter(new RetryConditionHeaderValue(Now), Now));
    }

    [Fact]
    public void A_missing_Retry_After_is_null()
    {
        Assert.Null(RetryPolicy.ReadRetryAfter(null, Now));
    }

    [Theory]
    [InlineData("3", 3)]
    [InlineData("0", 0)]
    [InlineData("120", 120)]
    [InlineData("Fri, 02 Oct 2026 12:00:07 GMT", 7)]
    [InlineData("Fri, 02 Oct 2026 11:59:00 GMT", 0)]
    public void A_real_Retry_After_header_is_read_in_seconds_or_as_a_date(string value, int expectedSeconds)
    {
        using var response = new HttpResponseMessage();
        response.Headers.TryAddWithoutValidation("Retry-After", value);

        var wait = RetryPolicy.ReadRetryAfter(response.Headers.RetryAfter, Now);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), wait);
    }

    [Theory]
    [InlineData("soon")]
    [InlineData("-5")]
    [InlineData("1.5")]
    [InlineData("")]
    [InlineData("3 seconds")]
    [InlineData("99999999999999999999")]
    [InlineData("tomorrow, 25 Foo 2026 99:99:99 GMT")]
    public void An_unreadable_Retry_After_header_is_ignored_without_throwing(string value)
    {
        using var response = new HttpResponseMessage();
        response.Headers.TryAddWithoutValidation("Retry-After", value);

        Assert.Null(RetryPolicy.ReadRetryAfter(response.Headers.RetryAfter, Now));
    }
}
