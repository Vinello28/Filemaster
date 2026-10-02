using Filemaster.Infrastructure;
using Microsoft.Extensions.Time.Testing;

namespace Filemaster.UnitTests.Infrastructure;

/// <summary>
/// <see cref="Deadline"/>: la scadenza scatta quando l'orologio (finto, comandato dal test) passa il tempo concesso, non prima; non
/// scatta con tempo infinito; il token di chi chiama la fa scattare senza che <c>Expired</c> diventi vero; si puo' riarmare; lo
/// smaltimento ferma il timer e non lancia, e un callback che lancia non esce dal timer.
/// </summary>
public sealed class DeadlineTests
{
    private static readonly TimeSpan Ten = TimeSpan.FromSeconds(10);

    [Fact]
    public void It_fires_exactly_when_the_time_has_passed_and_not_before()
    {
        var time = new FakeTimeProvider();
        using var deadline = new Deadline(time, Ten, CancellationToken.None);

        time.Advance(Ten - TimeSpan.FromMilliseconds(1));
        Assert.False(deadline.Expired);
        Assert.False(deadline.Token.IsCancellationRequested);

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(deadline.Expired);
        Assert.True(deadline.Token.IsCancellationRequested);
    }

    [Fact]
    public void An_infinite_deadline_never_fires()
    {
        var time = new FakeTimeProvider();
        using var deadline = new Deadline(time, Timeout.InfiniteTimeSpan, CancellationToken.None);

        time.Advance(TimeSpan.FromDays(20));

        Assert.False(deadline.Expired);
        Assert.False(deadline.Token.IsCancellationRequested);
    }

    [Fact]
    public void The_token_of_the_caller_cancels_the_deadline_token_without_marking_it_expired()
    {
        var time = new FakeTimeProvider();
        using var caller = new CancellationTokenSource();
        using var deadline = new Deadline(time, Ten, caller.Token);

        caller.Cancel();

        Assert.True(deadline.Token.IsCancellationRequested);
        Assert.False(deadline.Expired);
    }

    [Fact]
    public void A_caller_token_already_cancelled_gives_a_cancelled_deadline_that_is_not_expired()
    {
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        using var deadline = new Deadline(new FakeTimeProvider(), Ten, caller.Token);

        Assert.True(deadline.Token.IsCancellationRequested);
        Assert.False(deadline.Expired);
    }

    [Fact]
    public void Restart_gives_a_new_time_counted_from_now()
    {
        var time = new FakeTimeProvider();
        using var deadline = new Deadline(time, Ten, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(9));

        deadline.Restart(TimeSpan.FromSeconds(30));
        time.Advance(TimeSpan.FromSeconds(29));
        Assert.False(deadline.Expired);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(deadline.Expired);
    }

    [Fact]
    public void Restart_with_an_infinite_time_disarms_the_timer()
    {
        var time = new FakeTimeProvider();
        using var deadline = new Deadline(time, Ten, CancellationToken.None);

        deadline.Restart(Timeout.InfiniteTimeSpan);
        time.Advance(TimeSpan.FromDays(20));

        Assert.False(deadline.Expired);
    }

    [Fact]
    public void Dispose_stops_the_timer_and_is_idempotent_and_Restart_afterwards_does_nothing()
    {
        var time = new FakeTimeProvider();
        var deadline = new Deadline(time, Ten, CancellationToken.None);

        deadline.Dispose();
        deadline.Dispose();
        deadline.Restart(TimeSpan.FromSeconds(1));
        time.Advance(TimeSpan.FromDays(1));

        Assert.True(deadline.IsDisposed);
        Assert.False(deadline.Expired);
    }

    [Fact]
    public void A_callback_registered_on_the_token_that_throws_does_not_escape_from_the_timer()
    {
        var time = new FakeTimeProvider();
        using var deadline = new Deadline(time, Ten, CancellationToken.None);
        var ran = 0;
        using var failing = deadline.Token.Register(() => throw new InvalidOperationException("callback che lancia"));
        using var healthy = deadline.Token.Register(() => ran++);

        time.Advance(Ten); // se l'eccezione uscisse, Advance la rilancerebbe

        Assert.True(deadline.Expired);
        Assert.Equal(1, ran);
    }

    [Fact]
    public async Task A_pending_wait_on_the_token_is_cancelled_when_the_time_passes()
    {
        var time = new FakeTimeProvider();
        using var deadline = new Deadline(time, Ten, CancellationToken.None);
        var waiting = Task.Delay(Timeout.InfiniteTimeSpan, deadline.Token);

        time.Advance(Ten);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }
}
