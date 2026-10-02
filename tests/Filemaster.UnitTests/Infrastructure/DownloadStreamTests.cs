// Su net8/net10 CA1835 preferisce gli overload con Memory, che su net48 non esistono: i test provano proprio la forma con array.
#pragma warning disable CA1835

using System.Net;
using System.Net.Sockets;
using Filemaster.Domain;
using Filemaster.Infrastructure;
using Filemaster.UnitTests.Application;
using Microsoft.Extensions.Time.Testing;

namespace Filemaster.UnitTests.Infrastructure;

/// <summary>
/// <see cref="DownloadStream"/>: conta i byte contro <c>Content-Length</c> (tutto arrivato con e senza lunghezza, troncato, piu'
/// lungo, ai bordi esatti), traduce gli errori di rete in <see cref="ContentIntegrityException"/> troncato, lascia l'annullamento
/// del chiamante com'e' e fa del tempo scaduto un <see cref="FilemasterTimeoutException"/>, ha un verdetto sticky, si smaltisce
/// una volta sola rilasciando stream, risposta e scadenza, e si legge a pezzi da un byte. Ogni prova che legge gira su tutte e
/// quattro le firme di lettura.
/// </summary>
public sealed class DownloadStreamTests
{
    private const string RequestId = "req-dl-0001";
    private static readonly TimeSpan Ten = TimeSpan.FromSeconds(10);

    public static TheoryData<ReadApi> Apis() => new()
    {
        ReadApi.Sync,
        ReadApi.Async,
        ReadApi.AsyncMemory,
        ReadApi.SyncSpan,
    };

    private static DownloadStream Make(
        Stream inner,
        long? expected,
        DisposeProbe? owner = null,
        Deadline? deadline = null,
        int status = 200,
        CancellationToken caller = default) =>
        new(inner, expected, status, RequestId, owner, deadline, caller);

    // Legge a pezzi finche' non arriva lo 0 o un'eccezione; restituisce i byte consegnati e l'eccezione (se c'e').
    private static async Task<(byte[] Delivered, Exception? Failure)> ReadUntilEndOrFailureAsync(Stream stream, ReadApi api, int chunk)
    {
        var all = new MemoryStream();
        var buffer = new byte[chunk];
        try
        {
            while (true)
            {
                var read = await StreamReading.ReadOnceAsync(stream, api, buffer, 0, chunk);
                if (read == 0)
                {
                    return (all.ToArray(), null);
                }

                all.Write(buffer, 0, read);
            }
        }
        catch (Exception exception)
        {
            return (all.ToArray(), exception);
        }
    }

    private static Exception NetworkFailure(string kind) => kind switch
    {
        "io" => new IOException("connessione interrotta"),
        "http" => new HttpRequestException("richiesta fallita"),
        "web" => new WebException("connessione chiusa"),
        "socket" => new SocketException((int)SocketError.ConnectionReset),
        "disposed" => new ObjectDisposedException("connessione"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    // ----- tutto arrivato -----

    [Theory]
    [MemberData(nameof(Apis))]
    public async Task A_complete_download_with_a_content_length_delivers_every_byte_then_zero(ReadApi api)
    {
        var data = StreamReading.Pattern(1000);
        foreach (var chunk in new[] { 1, 7, 64, 1000, 4096 })
        {
            var inner = new ScriptedStream(data, maxChunk: 100);
            using var stream = Make(inner, expected: 1000);

            var read = await StreamReading.ReadToEndAsync(stream, api, chunk);

            Assert.Equal(data, read);
            var callsAtEnd = inner.ReadCalls;
            Assert.Equal(0, await StreamReading.ReadOnceAsync(stream, api, new byte[8], 0, 8));
            Assert.Equal(0, await StreamReading.ReadOnceAsync(stream, api, new byte[8], 0, 8));
            Assert.Equal(callsAtEnd, inner.ReadCalls); // dopo la fine pulita non si legge piu' dallo stream sottostante
        }
    }

    [Theory]
    [MemberData(nameof(Apis))]
    public async Task A_download_without_a_content_length_ends_normally_at_the_end_of_the_stream(ReadApi api)
    {
        var data = StreamReading.Pattern(333);
        using var stream = Make(new ScriptedStream(data, maxChunk: 50), expected: null);

        var read = await StreamReading.ReadToEndAsync(stream, api, 64);

        Assert.Equal(data, read);
    }

    [Theory]
    [MemberData(nameof(Apis))]
    public async Task An_empty_body_with_a_zero_content_length_or_none_is_a_normal_end(ReadApi api)
    {
        foreach (long? expected in new long?[] { 0, null })
        {
            using var stream = Make(new ScriptedStream(Array.Empty<byte>()), expected);

            Assert.Empty(await StreamReading.ReadToEndAsync(stream, api, 16));
        }
    }

    // ----- troncato -----

    [Theory]
    [MemberData(nameof(Apis))]
    public async Task A_stream_that_ends_with_fewer_bytes_than_the_content_length_is_a_truncated_download(ReadApi api)
    {
        var data = StreamReading.Pattern(40);
        using var stream = Make(new ScriptedStream(data, maxChunk: 16), expected: 100, status: 206);

        var (delivered, failure) = await ReadUntilEndOrFailureAsync(stream, api, 64);

        var exception = Assert.IsType<ContentIntegrityException>(failure);
        Assert.True(exception.IsTruncated);
        Assert.Equal(100, exception.ExpectedLength);
        Assert.Equal(40, exception.ActualLength);
        Assert.Equal(206, exception.StatusCode);
        Assert.Equal(RequestId, exception.RequestId);
        Assert.Null(exception.InnerException);
        Assert.Contains("40", exception.Message, StringComparison.Ordinal);
        Assert.Contains("100", exception.Message, StringComparison.Ordinal);
        Assert.Equal(data, delivered); // i byte arrivati prima del verdetto sono stati consegnati
    }

    [Theory]
    [MemberData(nameof(Apis))]
    public async Task A_download_that_delivers_nothing_but_promised_bytes_is_truncated_with_zero_read(ReadApi api)
    {
        using var stream = Make(new ScriptedStream(Array.Empty<byte>()), expected: 1);

        var (delivered, failure) = await ReadUntilEndOrFailureAsync(stream, api, 8);

        var exception = Assert.IsType<ContentIntegrityException>(failure);
        Assert.True(exception.IsTruncated);
        Assert.Equal(0, exception.ActualLength);
        Assert.Empty(delivered);
    }

    [Theory]
    [MemberData(nameof(Apis))]
    public async Task The_boundaries_are_exact_one_byte_fewer_is_truncated_the_same_number_is_fine_one_more_is_too_long(ReadApi api)
    {
        const int Expected = 10;
        var data = StreamReading.Pattern(Expected + 1);

        using (var fewer = Make(new ScriptedStream(data.Take(Expected - 1).ToArray(), maxChunk: 3), Expected))
        {
            var (_, failure) = await ReadUntilEndOrFailureAsync(fewer, api, 4);
            var exception = Assert.IsType<ContentIntegrityException>(failure);
            Assert.True(exception.IsTruncated);
            Assert.Equal(Expected - 1, exception.ActualLength);
        }

        using (var same = Make(new ScriptedStream(data.Take(Expected).ToArray(), maxChunk: 3), Expected))
        {
            Assert.Equal(data.Take(Expected).ToArray(), await StreamReading.ReadToEndAsync(same, api, 4));
        }

        using (var more = Make(new ScriptedStream(data, maxChunk: 3), Expected))
        {
            var (_, failure) = await ReadUntilEndOrFailureAsync(more, api, 4);
            var exception = Assert.IsType<ContentIntegrityException>(failure);
            Assert.False(exception.IsTruncated);
            Assert.Equal(Expected, exception.ExpectedLength);
            Assert.Equal(Expected + 1, exception.ActualLength);
        }
    }

    [Theory]
    [MemberData(nameof(Apis))]
    public async Task More_bytes_than_declared_is_not_truncated_and_is_detected_without_delivering_the_excess(ReadApi api)
    {
        var data = StreamReading.Pattern(30);
        using var stream = Make(new ScriptedStream(data), expected: 20);

        var (delivered, failure) = await ReadUntilEndOrFailureAsync(stream, api, 64);

        var exception = Assert.IsType<ContentIntegrityException>(failure);
        Assert.False(exception.IsTruncated);
        Assert.Equal(20, exception.ExpectedLength);
        Assert.Equal(30, exception.ActualLength);
        Assert.Equal(RequestId, exception.RequestId);
        Assert.Contains("piu' lungo", exception.Message, StringComparison.Ordinal);
        Assert.Empty(delivered); // la lettura da 64 ha superato il dichiarato: nessun byte e' stato consegnato
    }

    [Fact]
    public async Task A_zero_content_length_with_a_byte_that_arrives_is_too_long()
    {
        using var stream = Make(new ScriptedStream(new byte[] { 1 }), expected: 0);

        var (_, failure) = await ReadUntilEndOrFailureAsync(stream, ReadApi.Async, 8);

        var exception = Assert.IsType<ContentIntegrityException>(failure);
        Assert.False(exception.IsTruncated);
        Assert.Equal(1, exception.ActualLength);
    }

    // ----- errori di rete a meta' -----

    [Theory]
    [InlineData("io")]
    [InlineData("http")]
    [InlineData("web")]
    [InlineData("socket")]
    [InlineData("disposed")]
    public async Task A_network_failure_in_the_middle_is_a_truncated_download_with_the_cause_inside(string kind)
    {
        foreach (var api in new[] { ReadApi.Sync, ReadApi.Async, ReadApi.AsyncMemory, ReadApi.SyncSpan })
        {
            foreach (long? expected in new long?[] { 100, null })
            {
                var cause = NetworkFailure(kind);
                var inner = new ScriptedStream(StreamReading.Pattern(100), maxChunk: 10);
                inner.FailAtRead[3] = cause; // tre letture riuscite (30 byte), poi la rete cade
                using var stream = Make(inner, expected, status: 200);

                var (delivered, failure) = await ReadUntilEndOrFailureAsync(stream, api, 64);

                var exception = Assert.IsType<ContentIntegrityException>(failure);
                Assert.True(exception.IsTruncated);
                Assert.Same(cause, exception.InnerException);
                Assert.Equal(expected, exception.ExpectedLength);
                Assert.Equal(30, exception.ActualLength);
                Assert.Equal(RequestId, exception.RequestId);
                Assert.Equal(200, exception.StatusCode);
                Assert.Equal(30, delivered.Length);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Apis))]
    public async Task An_exception_that_is_not_a_network_failure_passes_through_unchanged_and_is_not_a_verdict(ReadApi api)
    {
        var bug = new InvalidOperationException("errore di programmazione");
        var inner = new ScriptedStream(StreamReading.Pattern(20));
        inner.FailAtRead[0] = bug;
        using var stream = Make(inner, expected: 20);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => StreamReading.ReadOnceAsync(stream, api, new byte[8], 0, 8));

        Assert.Same(bug, thrown);
        Assert.Equal(StreamReading.Pattern(20), await StreamReading.ReadToEndAsync(stream, api, 8)); // e lo stream funziona ancora
    }

    // ----- verdetto sticky -----

    [Theory]
    [MemberData(nameof(Apis))]
    public async Task After_a_truncation_verdict_every_read_throws_again_without_touching_the_inner_stream(ReadApi api)
    {
        var inner = new ScriptedStream(StreamReading.Pattern(5));
        using var stream = Make(inner, expected: 50);
        var (_, first) = await ReadUntilEndOrFailureAsync(stream, api, 16);
        var firstFailure = Assert.IsType<ContentIntegrityException>(first);
        var callsAtVerdict = inner.ReadCalls;

        for (var i = 0; i < 3; i++)
        {
            var again = await Assert.ThrowsAsync<ContentIntegrityException>(() => StreamReading.ReadOnceAsync(stream, api, new byte[8], 0, 8));

            Assert.True(again.IsTruncated);
            Assert.Equal(50, again.ExpectedLength);
            Assert.Equal(5, again.ActualLength);
            Assert.Equal(RequestId, again.RequestId);
            Assert.Same(firstFailure, again.InnerException);
        }

        Assert.Equal(callsAtVerdict, inner.ReadCalls);
    }

    [Theory]
    [MemberData(nameof(Apis))]
    public async Task After_a_too_long_verdict_and_after_a_network_failure_the_verdict_is_sticky_too(ReadApi api)
    {
        var tooLong = Make(new ScriptedStream(StreamReading.Pattern(30)), expected: 20);
        var (_, failure) = await ReadUntilEndOrFailureAsync(tooLong, api, 64);
        Assert.IsType<ContentIntegrityException>(failure);
        var again = await Assert.ThrowsAsync<ContentIntegrityException>(() => StreamReading.ReadOnceAsync(tooLong, api, new byte[8], 0, 8));
        Assert.False(again.IsTruncated);
        tooLong.Dispose();

        var broken = new ScriptedStream(StreamReading.Pattern(30), maxChunk: 10);
        broken.FailAtRead[1] = new IOException("rete");
        using var network = Make(broken, expected: null);
        var (_, networkFailure) = await ReadUntilEndOrFailureAsync(network, api, 64);
        Assert.IsType<ContentIntegrityException>(networkFailure);
        var networkAgain = await Assert.ThrowsAsync<ContentIntegrityException>(() => StreamReading.ReadOnceAsync(network, api, new byte[8], 0, 8));
        Assert.True(networkAgain.IsTruncated);
        Assert.IsType<IOException>(Assert.IsType<ContentIntegrityException>(networkAgain.InnerException).InnerException);
    }

    // ----- annullamento -----

    [Theory]
    [InlineData(ReadApi.Async)]
    [InlineData(ReadApi.AsyncMemory)]
    public async Task A_read_cancelled_by_its_own_token_is_an_OperationCanceledException_and_not_a_verdict(ReadApi api)
    {
        var data = StreamReading.Pattern(20);
        using var stream = Make(new ScriptedStream(data), expected: 20);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => StreamReading.ReadOnceAsync(stream, api, new byte[8], 0, 8, cts.Token));

        Assert.Equal(data, await StreamReading.ReadToEndAsync(stream, api, 8)); // con un altro token si continua dal punto dove si era
    }

    [Theory]
    [MemberData(nameof(Apis))]
    public async Task When_the_token_used_to_open_the_download_is_cancelled_a_failed_read_is_an_OperationCanceledException(ReadApi api)
    {
        using var open = new CancellationTokenSource();
        var time = new FakeTimeProvider();
        var deadline = new Deadline(time, Ten, open.Token);
        var owner = new DisposeProbe();
        var inner = new ScriptedStream(StreamReading.Pattern(50), maxChunk: 10);
        inner.FailAtRead[1] = new IOException("la connessione e' stata chiusa");
        using var stream = Make(inner, expected: 50, owner, deadline, caller: open.Token);

        open.Cancel(); // chiude la risposta (abort) e fa fallire la lettura

        var (_, failure) = await ReadUntilEndOrFailureAsync(stream, api, 64);
        Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.Equal(1, owner.Count);
    }

    [Fact]
    public async Task The_caller_token_wins_over_a_deadline_that_expired_as_well()
    {
        using var open = new CancellationTokenSource();
        var time = new FakeTimeProvider();
        var deadline = new Deadline(time, Ten, open.Token);
        var inner = new ScriptedStream(StreamReading.Pattern(50), maxChunk: 10);
        inner.FailAtRead[0] = new IOException("chiusa");
        using var stream = Make(inner, expected: 50, owner: null, deadline, caller: open.Token);
        time.Advance(Ten);
        open.Cancel();

        var (_, failure) = await ReadUntilEndOrFailureAsync(stream, ReadApi.Async, 16);

        Assert.IsAssignableFrom<OperationCanceledException>(failure);
    }

    // ----- tempo scaduto -----

    [Theory]
    [InlineData("io")]
    [InlineData("disposed")]
    [InlineData("http")]
    public async Task A_failed_read_after_the_transfer_time_expired_is_a_timeout_not_a_truncation(string kind)
    {
        var time = new FakeTimeProvider();
        var deadline = new Deadline(time, Ten, CancellationToken.None);
        var owner = new DisposeProbe();
        var cause = NetworkFailure(kind);
        var inner = new ScriptedStream(StreamReading.Pattern(50), maxChunk: 10);
        inner.FailAtRead[1] = cause;
        using var stream = Make(inner, expected: 50, owner, deadline);
        var buffer = new byte[10];
        Assert.Equal(10, await stream.ReadAsync(buffer, 0, 10));

        time.Advance(Ten);

        var exception = await Assert.ThrowsAsync<FilemasterTimeoutException>(() => stream.ReadAsync(buffer, 0, 10));
        Assert.Same(cause, exception.InnerException);
        Assert.Equal(RequestId, exception.RequestId);
        Assert.Equal(0, exception.StatusCode);
        Assert.Equal(1, owner.Count); // alla scadenza la risposta e' stata rilasciata
    }

    [Fact]
    public async Task A_cancellation_exception_with_an_expired_deadline_or_with_no_known_cause_is_a_timeout()
    {
        var time = new FakeTimeProvider();
        var deadline = new Deadline(time, Ten, CancellationToken.None);
        var inner = new ScriptedStream(StreamReading.Pattern(50));
        inner.FailAtRead[0] = new TaskCanceledException("annullata dal tempo");
        using var expired = Make(inner, expected: 50, owner: null, deadline);
        time.Advance(Ten);
        await Assert.ThrowsAsync<FilemasterTimeoutException>(() => expired.ReadAsync(new byte[8], 0, 8));

        // Nessun token scattato e nessuna scadenza: l'annullamento viene dal gestore HTTP (per esempio HttpClient.Timeout finito).
        var other = new ScriptedStream(StreamReading.Pattern(50));
        other.FailAtRead[0] = new TaskCanceledException("HttpClient.Timeout");
        using var handlerCancelled = Make(other, expected: 50);
        await Assert.ThrowsAsync<FilemasterTimeoutException>(() => handlerCancelled.ReadAsync(new byte[8], 0, 8));
    }

    [Theory]
    [InlineData(ReadApi.Async)]
    [InlineData(ReadApi.AsyncMemory)]
    [InlineData(ReadApi.Sync)]
    [InlineData(ReadApi.SyncSpan)]
    public async Task A_read_stuck_on_a_slow_connection_is_released_when_the_transfer_time_expires(ReadApi api)
    {
        var time = new FakeTimeProvider();
        var deadline = new Deadline(time, Ten, CancellationToken.None);
        var hanging = new HangingStream(StreamReading.Pattern(4));
        var owner = new DisposeProbe { OnDispose = () => hanging.Fail(new ObjectDisposedException("risposta")) };
        using var stream = Make(hanging, expected: 100, owner, deadline);
        var buffer = new byte[16];
        Assert.Equal(4, await StreamReading.ReadOnceAsync(stream, api, buffer, 0, 16)); // i primi byte arrivano

        var stuck = Task.Run(() => StreamReading.ReadOnceAsync(stream, api, buffer, 0, 16));
        await hanging.ReadStarted; // la lettura e' in attesa: solo adesso passa il tempo
        time.Advance(Ten);

        var exception = await Assert.ThrowsAsync<FilemasterTimeoutException>(() => stuck);
        Assert.IsType<ObjectDisposedException>(exception.InnerException);
        Assert.Equal(1, owner.Count);
        Assert.True(deadline.Expired);
    }

    [Fact]
    public async Task After_a_timeout_the_next_reads_are_timeouts_too()
    {
        var time = new FakeTimeProvider();
        var deadline = new Deadline(time, Ten, CancellationToken.None);
        var inner = new ScriptedStream(StreamReading.Pattern(50), maxChunk: 10);
        inner.FailAtRead[0] = new IOException("chiusa");
        inner.FailAtRead[1] = new ObjectDisposedException("risposta");
        using var stream = Make(inner, expected: 50, owner: null, deadline);
        time.Advance(Ten);

        await Assert.ThrowsAsync<FilemasterTimeoutException>(() => stream.ReadAsync(new byte[8], 0, 8));
        await Assert.ThrowsAsync<FilemasterTimeoutException>(() => stream.ReadAsync(new byte[8], 0, 8));
    }

    // ----- il token della scadenza chiude la risposta -----

    [Fact]
    public void When_the_deadline_token_fires_the_response_is_released()
    {
        var time = new FakeTimeProvider();
        var deadline = new Deadline(time, Ten, CancellationToken.None);
        var owner = new DisposeProbe();
        using var stream = Make(new ScriptedStream(Array.Empty<byte>()), expected: null, owner, deadline);
        Assert.Equal(0, owner.Count);

        time.Advance(Ten);

        Assert.Equal(1, owner.Count);
    }

    [Fact]
    public void A_deadline_already_fired_releases_the_response_as_soon_as_the_stream_is_built()
    {
        using var open = new CancellationTokenSource();
        var deadline = new Deadline(new FakeTimeProvider(), Ten, open.Token);
        open.Cancel();
        var owner = new DisposeProbe();

        using var stream = Make(new ScriptedStream(Array.Empty<byte>()), expected: null, owner, deadline, caller: open.Token);

        Assert.Equal(1, owner.Count);
    }

    [Fact]
    public void An_exception_from_releasing_the_response_on_abort_does_not_escape_from_Cancel()
    {
        using var open = new CancellationTokenSource();
        var deadline = new Deadline(new FakeTimeProvider(), Ten, open.Token);
        var owner = new DisposeProbe { Throws = new InvalidOperationException("dispose che lancia") };
        using var stream = Make(new ScriptedStream(Array.Empty<byte>()), expected: null, owner, deadline, caller: open.Token);

        open.Cancel();

        Assert.Equal(1, owner.Count);
        owner.Throws = null; // lo smaltimento dello stream, a fine test, rilascia di nuovo la risposta: stavolta senza lanciare
    }

    // ----- smaltimento -----

    [Fact]
    public void Dispose_releases_the_stream_then_the_response_and_the_deadline_once_and_is_idempotent()
    {
        var log = new List<string>();
        var inner = new ScriptedStream(StreamReading.Pattern(10)) { Log = log };
        var owner = new DisposeProbe("response", log);
        var deadline = new Deadline(new FakeTimeProvider(), Ten, CancellationToken.None);
        var stream = Make(inner, expected: 10, owner, deadline);

        stream.Dispose();
        stream.Dispose();
        stream.Dispose();

        Assert.Equal(new[] { "stream", "response" }, log);
        Assert.Equal(1, inner.DisposeCount);
        Assert.Equal(1, owner.Count);
        Assert.True(deadline.IsDisposed);
        Assert.False(stream.CanRead);
    }

    [Fact]
    public async Task Disposing_early_with_unread_or_missing_bytes_gives_no_verdict_and_does_not_throw()
    {
        var owner = new DisposeProbe();
        var inner = new ScriptedStream(StreamReading.Pattern(10));
        var stream = Make(inner, expected: 1000, owner);
        Assert.Equal(4, await stream.ReadAsync(new byte[4], 0, 4));

        var exception = Record.Exception(stream.Dispose);

        Assert.Null(exception);
        Assert.Equal(1, owner.Count);
        Assert.Equal(1, inner.DisposeCount);
    }

    [Fact]
    public void Disposing_a_stream_after_a_verdict_does_not_throw_either()
    {
        var stream = Make(new ScriptedStream(Array.Empty<byte>()), expected: 5);
        Assert.Throws<ContentIntegrityException>(() => stream.Read(new byte[4], 0, 4));

        Assert.Null(Record.Exception(stream.Dispose));
    }

    [Fact]
    public void Every_resource_is_released_even_if_an_earlier_one_throws_when_disposed()
    {
        var inner = new ThrowingDisposeStream();
        var owner = new DisposeProbe();
        var deadline = new Deadline(new FakeTimeProvider(), Ten, CancellationToken.None);
        var stream = Make(inner, expected: null, owner, deadline);

        Assert.Throws<InvalidOperationException>(stream.Dispose);

        Assert.Equal(1, owner.Count);
        Assert.True(deadline.IsDisposed);
        Assert.Null(Record.Exception(stream.Dispose)); // la seconda volta non fa nulla e non lancia
    }

    [Theory]
    [MemberData(nameof(Apis))]
    public async Task Reading_after_dispose_throws_ObjectDisposedException(ReadApi api)
    {
        var stream = Make(new ScriptedStream(StreamReading.Pattern(10)), expected: 10);
        stream.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => StreamReading.ReadOnceAsync(stream, api, new byte[4], 0, 4));
    }

    [Fact]
    public async Task Disposing_while_a_read_is_pending_turns_the_failure_into_ObjectDisposedException()
    {
        var hanging = new HangingStream(Array.Empty<byte>());
        var owner = new DisposeProbe { OnDispose = () => hanging.Fail(new IOException("risposta chiusa")) };
        var stream = Make(hanging, expected: 100, owner);
        var pending = stream.ReadAsync(new byte[4], 0, 4);
        await hanging.ReadStarted;

        stream.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => pending);
    }

    // ----- altro -----

    [Theory]
    [MemberData(nameof(Apis))]
    public async Task A_read_of_zero_bytes_returns_zero_without_touching_the_inner_stream_or_concluding_anything(ReadApi api)
    {
        var inner = new ScriptedStream(Array.Empty<byte>());
        using var stream = Make(inner, expected: 10);

        var read = await StreamReading.ReadOnceAsync(stream, api, new byte[4], 0, 0);

        Assert.Equal(0, read);
        Assert.Equal(0, inner.ReadCalls);
        var (_, failure) = await ReadUntilEndOrFailureAsync(stream, api, 4); // il verdetto arriva solo con una lettura vera
        Assert.IsType<ContentIntegrityException>(failure);
    }

    // Read(Span) della base Stream copia da un array preso a prestito e lancia IOException da sola con un conteggio impossibile: il
    // controllo di DownloadStream si prova con le tre firme che inoltrano il conteggio cosi' com'e'.
    [Theory]
    [InlineData(ReadApi.Sync)]
    [InlineData(ReadApi.Async)]
    [InlineData(ReadApi.AsyncMemory)]
    public async Task An_inner_stream_that_returns_an_impossible_count_is_a_programming_error(ReadApi api)
    {
        var inner = new ScriptedStream(StreamReading.Pattern(10)) { ReadResult = count => count + 1 };
        using var stream = Make(inner, expected: 10);

        await Assert.ThrowsAsync<InvalidOperationException>(() => StreamReading.ReadOnceAsync(stream, api, new byte[4], 0, 4));
    }

    [Theory]
    [MemberData(nameof(Apis))]
    public async Task Reading_one_byte_at_a_time_delivers_everything_and_the_verdict_comes_only_at_the_end(ReadApi api)
    {
        var data = StreamReading.Pattern(300);
        var complete = new ScriptedStream(data);
        using (var stream = Make(complete, expected: 300))
        {
            Assert.Equal(data, await StreamReading.ReadToEndAsync(stream, api, 1));
            Assert.Equal(301, complete.ReadCalls); // 300 letture da un byte piu' quella che trova la fine
        }

        var truncated = new ScriptedStream(data.Take(299).ToArray());
        using (var stream = Make(truncated, expected: 300))
        {
            var (delivered, failure) = await ReadUntilEndOrFailureAsync(stream, api, 1);

            var exception = Assert.IsType<ContentIntegrityException>(failure);
            Assert.True(exception.IsTruncated);
            Assert.Equal(299, exception.ActualLength);
            Assert.Equal(299, delivered.Length); // 299 letture da un byte senza errori, poi la fine con il verdetto
        }

        var tooLong = new ScriptedStream(data);
        using (var stream = Make(tooLong, expected: 299))
        {
            var (delivered, failure) = await ReadUntilEndOrFailureAsync(stream, api, 1);

            var exception = Assert.IsType<ContentIntegrityException>(failure);
            Assert.False(exception.IsTruncated);
            Assert.Equal(299, delivered.Length); // il byte 300 e' quello che supera: non si consegna
            Assert.Equal(300, exception.ActualLength);
        }
    }

    [Fact]
    public void The_stream_is_read_only_forward_only_and_has_no_length()
    {
        using var stream = Make(new ScriptedStream(StreamReading.Pattern(4)), expected: 4);

        Assert.True(stream.CanRead);
        Assert.False(stream.CanSeek);
        Assert.False(stream.CanWrite);
        Assert.Throws<NotSupportedException>(() => stream.Length);
        Assert.Throws<NotSupportedException>(() => stream.Position);
        Assert.Throws<NotSupportedException>(() => stream.Position = 0);
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(0));
        Assert.Throws<NotSupportedException>(() => stream.Write(new byte[1], 0, 1));
        stream.Flush();
    }

    [Fact]
    public void Constructor_and_read_arguments_are_checked()
    {
        Assert.Throws<ArgumentNullException>(() => Make(null!, expected: null));
        Assert.Throws<ArgumentException>(() => Make(new WriteOnlyStream(), expected: null));
        Assert.Throws<ArgumentOutOfRangeException>(() => Make(new ScriptedStream(Array.Empty<byte>()), expected: -1));

        using var stream = Make(new ScriptedStream(StreamReading.Pattern(4)), expected: 4);
        Assert.Throws<ArgumentNullException>(() => stream.Read(null!, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Read(new byte[4], -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Read(new byte[4], 0, -1));
        Assert.Throws<ArgumentException>(() => stream.Read(new byte[4], 2, 3));
    }

    [Fact]
    public async Task CopyToAsync_copies_a_complete_download_and_fails_on_a_truncated_one()
    {
        var data = StreamReading.Pattern(5000);
        using (var complete = Make(new ScriptedStream(data, maxChunk: 700), expected: 5000))
        {
            var target = new MemoryStream();
            await complete.CopyToAsync(target);
            Assert.Equal(data, target.ToArray());
        }

        using var truncated = Make(new ScriptedStream(data.Take(4000).ToArray(), maxChunk: 700), expected: 5000);
        await Assert.ThrowsAsync<ContentIntegrityException>(() => truncated.CopyToAsync(new MemoryStream()));
    }

    private sealed class ThrowingDisposeStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => 0;

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                throw new InvalidOperationException("dispose che lancia");
            }

            base.Dispose(disposing);
        }
    }

    private sealed class WriteOnlyStream : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
        }
    }
}
