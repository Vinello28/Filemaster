using Filemaster.Domain;
using Filemaster.Infrastructure;
using Filemaster.UnitTests.Application;

namespace Filemaster.UnitTests.Infrastructure;

/// <summary>
/// Tempo scaduto e annullamento: <c>RequestTimeout</c> e' una scadenza per l'<b>intera chiamata</b> (tentativi e attese compresi),
/// misurata da un orologio finto; il tempo scaduto e' <see cref="FilemasterTimeoutException"/>, l'annullamento di chi chiama e'
/// <see cref="OperationCanceledException"/>, e i due si distinguono dal token di chi chiama anche quando il gestore lancia la stessa
/// eccezione. Il timeout non si ritenta. Ogni prova aspetta un segnale del gestore finto (mai un tempo) prima di far passare il tempo.
/// </summary>
public sealed class TransportTimeoutTests
{
    [Fact]
    public async Task When_RequestTimeout_passes_without_a_response_the_call_ends_in_a_FilemasterTimeoutException_and_is_not_retried()
    {
        using var rig = new TransportRig();
        var entered = Waiting.NewSignal();
        rig.Handler.Then(Waiting.Hang(entered)).Then(Reply.Json(200, "{}"));

        var call = rig.Transport.SendBufferedAsync(TransportRig.Get(), default);
        await entered.Task;
        rig.Time.Advance(rig.Options.RequestTimeout);

        var exception = await Assert.ThrowsAsync<FilemasterTimeoutException>(() => call);
        Assert.Equal(0, exception.StatusCode);
        Assert.Equal(rig.Handler.Requests[0].Header("X-Request-ID"), exception.RequestId);
        Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
        Assert.Single(rig.Handler.Requests); // un GET con 3 tentativi: il timeout non si ritenta
        Assert.Empty(rig.Delays.Delays);
    }

    [Fact]
    public async Task One_millisecond_before_RequestTimeout_the_call_is_still_waiting()
    {
        using var rig = new TransportRig();
        var entered = Waiting.NewSignal();
        var release = Waiting.NewSignal();
        rig.Handler.Then(Waiting.UntilReleased(entered, release, () => Reply.Json(200, "{\"ok\":true}")));

        var call = rig.Transport.SendBufferedAsync(TransportRig.Get(), default);
        await entered.Task;
        rig.Time.Advance(rig.Options.RequestTimeout - TimeSpan.FromMilliseconds(1));
        Assert.False(call.IsCompleted);
        release.SetResult(true);

        var response = await call;
        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public async Task RequestTimeout_is_taken_from_the_options_not_a_fixed_value()
    {
        using var rig = new TransportRig(o => o.RequestTimeout = TimeSpan.FromSeconds(2));
        var entered = Waiting.NewSignal();
        rig.Handler.Then(Waiting.Hang(entered));

        var call = rig.Transport.SendBufferedAsync(TransportRig.Get(), default);
        await entered.Task;
        rig.Time.Advance(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<FilemasterTimeoutException>(() => call);
    }

    [Fact]
    public async Task Cancelling_the_caller_token_is_an_OperationCanceledException_and_not_a_timeout()
    {
        using var rig = new TransportRig();
        using var cts = new CancellationTokenSource();
        var entered = Waiting.NewSignal();
        rig.Handler.Then(Waiting.Hang(entered));

        var call = rig.Transport.SendBufferedAsync(TransportRig.Get(), cts.Token);
        await entered.Task;
        cts.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.IsNotType<FilemasterTimeoutException>(exception);
        Assert.False(rig.Time.GetUtcNow() > new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task The_two_cases_are_told_apart_even_when_the_handler_throws_the_same_exception_for_both()
    {
        // Il gestore, quando il token scatta, lancia sempre lo stesso TaskCanceledException senza token: a decidere e' il token del chiamante.
        using var rig = new TransportRig();
        using var cts = new CancellationTokenSource();
        var enteredTimeout = Waiting.NewSignal();
        var enteredCancel = Waiting.NewSignal();
        var never = Waiting.NewSignal();
        rig.Handler
            .Then(Waiting.UntilReleased(enteredTimeout, never, () => Reply.Json(200, "{}")))
            .Then(Waiting.UntilReleased(enteredCancel, never, () => Reply.Json(200, "{}")));

        var byTime = rig.Transport.SendBufferedAsync(TransportRig.Get(), default);
        await enteredTimeout.Task;
        rig.Time.Advance(rig.Options.RequestTimeout);
        var timeout = await Assert.ThrowsAsync<FilemasterTimeoutException>(() => byTime);

        var byCaller = rig.Transport.SendBufferedAsync(TransportRig.Get(), cts.Token);
        await enteredCancel.Task;
        cts.Cancel();
        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => byCaller);

        Assert.IsType<TaskCanceledException>(timeout.InnerException);
        Assert.IsNotType<FilemasterTimeoutException>(cancelled);
    }

    [Fact]
    public async Task When_the_caller_cancels_and_the_time_runs_out_together_the_caller_wins()
    {
        // Il token del chiamante scatta, poi passa il tempo prima che la chiamata finisca: si guarda prima il token del chiamante.
        using var rig = new TransportRig();
        using var cts = new CancellationTokenSource();
        var entered = Waiting.NewSignal();
        var never = Waiting.NewSignal();
        rig.Handler.Then(Waiting.UntilReleased(entered, never, () => Reply.Json(200, "{}")));

        var call = rig.Transport.SendBufferedAsync(TransportRig.Get(), cts.Token);
        await entered.Task;
        cts.Cancel();
        rig.Time.Advance(rig.Options.RequestTimeout);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.IsNotType<FilemasterTimeoutException>(exception);
    }

    [Fact]
    public async Task A_cancellation_nobody_asked_for_is_a_timeout_because_it_comes_from_the_HttpClient_own_Timeout()
    {
        using var rig = new TransportRig();
        rig.Handler.ThenFail(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing."));

        var exception = await Assert.ThrowsAsync<FilemasterTimeoutException>(() => rig.Transport.SendBufferedAsync(TransportRig.Get(), default));

        Assert.IsType<TaskCanceledException>(exception.InnerException);
        Assert.Equal(rig.Handler.Requests[0].Header("X-Request-ID"), exception.RequestId);
    }

    [Fact]
    public async Task RequestTimeout_covers_the_whole_call_including_the_waits_between_attempts_not_each_attempt()
    {
        // Il primo tentativo consuma 20 s e fallisce (503), l'attesa ne consuma altri 15: in totale 35 s su 30. Con una scadenza per
        // tentativo la chiamata riuscirebbe al secondo tentativo; con quella per l'intera chiamata il tempo e' finito.
        TransportRig? created = null;
        using var rig = new TransportRig(delay: (_, token) =>
        {
            created!.Time.Advance(TimeSpan.FromSeconds(15));
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });
        created = rig;
        rig.Handler
            .Then((_, _) =>
            {
                rig.Time.Advance(TimeSpan.FromSeconds(20));
                return Task.FromResult(Reply.Text(503));
            })
            .Then(Reply.Json(200, "{}"));

        var exception = await Assert.ThrowsAsync<FilemasterTimeoutException>(() => rig.Transport.SendBufferedAsync(TransportRig.Get(), default));

        Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
        Assert.Single(rig.Handler.Requests);
    }

    [Fact]
    public async Task A_deadline_that_passes_during_the_backoff_is_a_timeout_not_a_cancellation()
    {
        TransportRig? created = null;
        using var rig = new TransportRig(delay: async (_, token) =>
        {
            created!.Time.Advance(created.Options.RequestTimeout);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        created = rig;
        rig.Handler.Then(Reply.Text(503)).Then(Reply.Json(200, "{}"));

        var exception = await Assert.ThrowsAsync<FilemasterTimeoutException>(() => rig.Transport.SendBufferedAsync(TransportRig.Get(), default));

        Assert.Equal(rig.Handler.Requests[0].Header("X-Request-ID"), exception.RequestId);
        Assert.Single(rig.Handler.Requests);
    }

    [Fact]
    public async Task A_response_body_that_stalls_is_released_when_the_time_runs_out()
    {
        using var rig = new TransportRig();
        var hanging = new HangingStream(new byte[] { 1, 2, 3 });
        var reply = Reply.Streamed(200, hanging, declaredLength: null);
        reply.ContentOf().OnDispose = () => hanging.Fail(new ObjectDisposedException("risposta"));
        rig.Handler.Then(reply);

        var call = rig.Transport.SendBufferedAsync(TransportRig.Get(), default);
        await hanging.ReadStarted;
        rig.Time.Advance(rig.Options.RequestTimeout);

        var exception = await Assert.ThrowsAsync<FilemasterTimeoutException>(() => call);
        Assert.Equal(1, reply.ContentOf().DisposeCount);
        Assert.Single(rig.Handler.Requests);
        Assert.NotNull(exception.InnerException);
    }

    [Fact]
    public async Task An_error_body_that_stalls_is_released_when_the_time_runs_out_even_if_the_stream_ignores_the_token()
    {
        // Il server manda le intestazioni di un 500 e poi si ferma: lo stream (come quello di .NET Framework) ignora il token delle
        // letture, quindi a sbloccarlo deve essere il rilascio della risposta alla scadenza.
        using var rig = new TransportRig(o => o.Retry.MaxAttempts = 1);
        var hanging = new HangingStream(Array.Empty<byte>());
        var reply = Reply.Streamed(500, hanging, declaredLength: null);
        reply.ContentOf().OnDispose = () => hanging.Fail(new ObjectDisposedException("risposta"));
        rig.Handler.Then(reply);

        var call = rig.Transport.SendBufferedAsync(TransportRig.Get(), default);
        await Waiting.Within(hanging.ReadStarted);
        rig.Time.Advance(rig.Options.RequestTimeout);

        var exception = await Assert.ThrowsAsync<FilemasterTimeoutException>(() => Waiting.Within(call));
        Assert.Equal(1, reply.ContentOf().DisposeCount);
        Assert.Equal(rig.Handler.Requests[0].Header("X-Request-ID"), exception.RequestId);
    }

    [Fact]
    public async Task An_error_body_that_stalls_and_a_caller_that_cancels_is_a_cancellation()
    {
        using var rig = new TransportRig(o => o.Retry.MaxAttempts = 1);
        using var cts = new CancellationTokenSource();
        var hanging = new HangingStream(Array.Empty<byte>());
        var reply = Reply.Streamed(500, hanging, declaredLength: null);
        reply.ContentOf().OnDispose = () => hanging.Fail(new IOException("risposta chiusa"));
        rig.Handler.Then(reply);

        var call = rig.Transport.SendBufferedAsync(TransportRig.Get(), cts.Token);
        await Waiting.Within(hanging.ReadStarted);
        cts.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Waiting.Within(call));
        Assert.IsNotType<FilemasterTimeoutException>(exception);
    }

    [Fact]
    public async Task An_upload_whose_error_body_stalls_ends_in_a_timeout_after_TransferTimeout()
    {
        using var rig = new TransportRig(o => o.TransferTimeout = TimeSpan.FromSeconds(60));
        var hanging = new HangingStream(Array.Empty<byte>());
        var reply = Reply.Streamed(413, hanging, declaredLength: null);
        reply.ContentOf().OnDispose = () => hanging.Fail(new ObjectDisposedException("risposta"));
        rig.Handler.Then(reply);
        var request = TransportRig.Post("documents");
        request.Customize = message => message.Content = new StreamContent(new MemoryStream(new byte[3]));

        var call = rig.Transport.SendUploadAsync(request, default);
        await Waiting.Within(hanging.ReadStarted);
        rig.Time.Advance(TimeSpan.FromSeconds(60));

        await Assert.ThrowsAsync<FilemasterTimeoutException>(() => Waiting.Within(call));
    }

    [Fact]
    public async Task The_timer_is_stopped_when_the_call_ends_so_later_time_changes_nothing()
    {
        using var rig = new TransportRig();
        rig.Handler.Then(Reply.Json(200, "{}"));

        await rig.Transport.SendBufferedAsync(TransportRig.Get(), default);
        rig.Time.Advance(TimeSpan.FromDays(1)); // se il timer fosse ancora armato, la sua callback girerebbe su un token smaltito

        Assert.Single(rig.Handler.Requests);
    }

    [Fact]
    public async Task Cancelling_after_the_call_has_finished_does_nothing()
    {
        using var rig = new TransportRig();
        using var cts = new CancellationTokenSource();
        rig.Handler.Then(Reply.Json(200, "{}"));

        await rig.Transport.SendBufferedAsync(TransportRig.Get(), cts.Token);
        cts.Cancel();

        Assert.Single(rig.Handler.Requests);
    }
}
