using Filemaster.Infrastructure;

namespace Filemaster.UnitTests.Infrastructure;

/// <summary>
/// <see cref="FilemasterRetryOptions"/>: i default (3 tentativi, 500 ms, 10 s), i bordi di <c>MaxAttempts</c> (1 spegne il
/// ritentativo, il massimo e' 10), le attese non negative e non oltre int.MaxValue millisecondi, e <c>MaxDelay</c> non minore di
/// <c>InitialDelay</c>.
/// </summary>
public sealed class FilemasterRetryOptionsTests
{
    private static ArgumentOutOfRangeException Rejected(FilemasterRetryOptions options, string paramName)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
        Assert.Equal(paramName, exception.ParamName);
        return exception;
    }

    [Fact]
    public void Defaults_are_3_attempts_500_milliseconds_and_10_seconds_and_they_are_valid()
    {
        var options = new FilemasterRetryOptions();

        Assert.Equal(3, options.MaxAttempts);
        Assert.Equal(TimeSpan.FromMilliseconds(500), options.InitialDelay);
        Assert.Equal(TimeSpan.FromSeconds(10), options.MaxDelay);
        options.Validate();
    }

    [Fact]
    public void The_maximum_number_of_attempts_is_10()
    {
        Assert.Equal(10, FilemasterRetryOptions.MaxAllowedAttempts);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(10)]
    public void From_1_to_10_attempts_are_accepted_and_1_means_no_retry(int attempts)
    {
        new FilemasterRetryOptions { MaxAttempts = attempts }.Validate();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(11)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void Fewer_than_1_or_more_than_10_attempts_are_rejected(int attempts)
    {
        var exception = Rejected(new FilemasterRetryOptions { MaxAttempts = attempts }, nameof(FilemasterRetryOptions.MaxAttempts));

        Assert.Equal(attempts, exception.ActualValue);
    }

    [Fact]
    public void Zero_delays_are_accepted_for_an_immediate_retry()
    {
        new FilemasterRetryOptions { InitialDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero }.Validate();
    }

    [Fact]
    public void Equal_initial_and_maximum_delays_are_accepted()
    {
        new FilemasterRetryOptions { InitialDelay = TimeSpan.FromSeconds(2), MaxDelay = TimeSpan.FromSeconds(2) }.Validate();
    }

    [Fact]
    public void The_longest_accepted_delay_is_int_MaxValue_milliseconds()
    {
        var longest = TimeSpan.FromMilliseconds(int.MaxValue);

        new FilemasterRetryOptions { InitialDelay = longest, MaxDelay = longest }.Validate();
        Rejected(
            new FilemasterRetryOptions { MaxDelay = longest + TimeSpan.FromTicks(1) },
            nameof(FilemasterRetryOptions.MaxDelay));
    }

    [Fact]
    public void A_negative_or_infinite_initial_delay_is_rejected()
    {
        foreach (var value in new[] { TimeSpan.FromTicks(-1), TimeSpan.FromSeconds(-1), Timeout.InfiniteTimeSpan })
        {
            var exception = Rejected(new FilemasterRetryOptions { InitialDelay = value }, nameof(FilemasterRetryOptions.InitialDelay));

            Assert.Equal(value, exception.ActualValue);
        }
    }

    [Fact]
    public void A_negative_infinite_or_too_long_maximum_delay_is_rejected()
    {
        foreach (var value in new[] { TimeSpan.FromTicks(-1), Timeout.InfiniteTimeSpan, TimeSpan.MaxValue })
        {
            var options = new FilemasterRetryOptions { InitialDelay = TimeSpan.Zero, MaxDelay = value };

            var exception = Rejected(options, nameof(FilemasterRetryOptions.MaxDelay));

            Assert.Equal(value, exception.ActualValue);
        }
    }

    [Fact]
    public void A_maximum_delay_smaller_than_the_initial_one_is_rejected()
    {
        var options = new FilemasterRetryOptions { InitialDelay = TimeSpan.FromSeconds(2), MaxDelay = TimeSpan.FromSeconds(2) - TimeSpan.FromTicks(1) };

        Rejected(options, nameof(FilemasterRetryOptions.MaxDelay));
    }

    [Fact]
    public void ToString_lists_the_three_values()
    {
        var text = new FilemasterRetryOptions().ToString();

        Assert.Contains("MaxAttempts = 3", text, StringComparison.Ordinal);
        Assert.Contains("InitialDelay = 00:00:00.5000000", text, StringComparison.Ordinal);
        Assert.Contains("MaxDelay = 00:00:10", text, StringComparison.Ordinal);
    }
}
