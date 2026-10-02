// Su net8/net10 CA1835 preferisce gli overload con Memory, che su net48 non esistono: i test provano proprio la forma con array.
#pragma warning disable CA1835

using System.Net.Http.Headers;
using Filemaster.Domain;
using Filemaster.Infrastructure;
using Filemaster.UnitTests.Application;

namespace Filemaster.UnitTests.Infrastructure;

/// <summary>
/// La modalita' "download in streaming": <c>ResponseHeadersRead</c> (il contenuto non viene bufferizzato), la risposta consegnata come
/// <see cref="DownloadStream"/> che possiede risposta HTTP e scadenza, <c>RequestTimeout</c> fino alle intestazioni e poi
/// <c>TransferTimeout</c> sul resto, il troncamento rilevato dal conteggio dei byte, il ritentativo solo prima della consegna, e la
/// mappatura degli errori (con la risposta smaltita).
/// </summary>
public sealed class TransportDownloadTests
{
    [Fact]
    public async Task A_200_is_delivered_as_a_stream_without_buffering_the_body()
    {
        using var rig = new TransportRig();
        var data = StreamReading.Pattern(2000);
        var reply = Reply.Bytes(200, data, declaredLength: 2000, "application/pdf").WithHeader("Last-Modified", "Thu, 01 Oct 2026 09:59:11 GMT");
        rig.Handler.Then(reply);

        using var download = await rig.Transport.SendDownloadAsync(TransportRig.Get("documents/doc_X/content"), default).AsDisposable();

        Assert.Equal(200, download.Response.StatusCode);
        Assert.Equal(2000, download.Response.ContentLength);
        Assert.Equal("application/pdf", download.Response.ContentHeaders.ContentType!.MediaType);
        Assert.NotNull(download.Response.Headers);
        Assert.Equal(0, reply.ContentOf().SerializeCalls); // ResponseHeadersRead: niente bufferizzazione
        Assert.Equal(0, reply.ContentOf().DisposeCount); // la risposta resta aperta finche' lo stream non e' smaltito
        Assert.IsType<DownloadStream>(download.Response.Content);
        Assert.Equal(data, await StreamReading.ReadToEndAsync(download.Response.Content, ReadApi.Async, 256));
    }

    [Fact]
    public async Task Disposing_the_stream_releases_the_response_and_the_deadline_and_is_idempotent()
    {
        using var rig = new TransportRig();
        var reply = Reply.Bytes(200, StreamReading.Pattern(10), declaredLength: 10);
        rig.Handler.Then(reply);
        var download = await rig.Transport.SendDownloadAsync(TransportRig.Get("documents/doc_X/content"), default);

        download.Content.Dispose();
        download.Content.Dispose();

        Assert.Equal(1, reply.ContentOf().DisposeCount);
        rig.Time.Advance(TimeSpan.FromDays(1)); // la scadenza e' smaltita: il timer non gira piu'
    }

    [Fact]
    public async Task The_request_id_of_the_download_is_the_one_in_the_response_header_or_else_the_one_sent()
    {
        using var rig = new TransportRig();
        rig.Handler.Then(Reply.Bytes(200, new byte[3], 3, requestId: "srv-dl-1")).Then(Reply.Bytes(200, new byte[3], 3, requestId: null));

        using var withHeader = await rig.Transport.SendDownloadAsync(TransportRig.Get("documents/doc_X/content"), default).AsDisposable();
        using var without = await rig.Transport.SendDownloadAsync(TransportRig.Get("documents/doc_X/content"), default).AsDisposable();

        Assert.Equal("srv-dl-1", withHeader.Response.RequestId);
        Assert.Equal(rig.Handler.Requests[1].Header("X-Request-ID"), without.Response.RequestId);
    }

    [Fact]
    public async Task A_range_request_and_its_206_answer_pass_through()
    {
        using var rig = new TransportRig();
        var reply = Reply.Bytes(206, StreamReading.Pattern(10), declaredLength: 10);
        reply.Content.Headers.ContentRange = new ContentRangeHeaderValue(10, 19, 100);
        rig.Handler.Then(reply);
        var request = TransportRig.Get("documents/doc_X/content");
        request.Customize = message => message.Headers.Range = new RangeHeaderValue(10, 19);

        using var download = await rig.Transport.SendDownloadAsync(request, default).AsDisposable();

        Assert.Equal("bytes=10-19", rig.Handler.Requests[0].Header("Range"));
        Assert.Equal(206, download.Response.StatusCode);
        Assert.Equal(new ContentRangeHeaderValue(10, 19, 100), download.Response.ContentHeaders.ContentRange);
        Assert.Equal(StreamReading.Pattern(10), await StreamReading.ReadToEndAsync(download.Response.Content, ReadApi.Sync, 4));
    }

    // ----- troncamento -----

    [Fact]
    public async Task A_body_shorter_than_its_content_length_is_a_truncated_download_when_read_to_the_end()
    {
        using var rig = new TransportRig();
        rig.Handler.Then(Reply.Streamed(200, new ScriptedStream(StreamReading.Pattern(40)), declaredLength: 100, requestId: "srv-dl-9"));
        using var download = await rig.Transport.SendDownloadAsync(TransportRig.Get("documents/doc_X/content"), default).AsDisposable();

        var exception = await Assert.ThrowsAsync<ContentIntegrityException>(() => StreamReading.ReadToEndAsync(download.Response.Content, ReadApi.Async, 16));

        Assert.True(exception.IsTruncated);
        Assert.Equal(100, exception.ExpectedLength);
        Assert.Equal(40, exception.ActualLength);
        Assert.Equal("srv-dl-9", exception.RequestId);
        Assert.Equal(200, exception.StatusCode);
    }

    [Fact]
    public async Task A_chunked_body_has_no_length_to_compare_and_a_dropped_connection_is_the_only_signal()
    {
        using var rig = new TransportRig();
        var dropping = new ScriptedStream(StreamReading.Pattern(100), maxChunk: 10);
        dropping.FailAtRead[3] = new IOException("connessione chiusa dal server");
        rig.Handler.Then(Reply.Streamed(200, dropping, declaredLength: null));
        using var download = await rig.Transport.SendDownloadAsync(TransportRig.Get("documents/doc_X/content"), default).AsDisposable();

        Assert.Null(download.Response.ContentLength);
        var exception = await Assert.ThrowsAsync<ContentIntegrityException>(() => StreamReading.ReadToEndAsync(download.Response.Content, ReadApi.Async, 64));

        Assert.True(exception.IsTruncated);
        Assert.Null(exception.ExpectedLength);
        Assert.Equal(30, exception.ActualLength);
        Assert.IsType<IOException>(exception.InnerException);
    }

    [Fact]
    public async Task A_chunked_body_that_ends_normally_is_a_normal_end()
    {
        using var rig = new TransportRig();
        var data = StreamReading.Pattern(77);
        rig.Handler.Then(Reply.Streamed(200, new ScriptedStream(data, maxChunk: 10), declaredLength: null));
        using var download = await rig.Transport.SendDownloadAsync(TransportRig.Get("documents/doc_X/content"), default).AsDisposable();

        Assert.Equal(data, await StreamReading.ReadToEndAsync(download.Response.Content, ReadApi.Async, 64));
    }

    // ----- ritentativo: solo prima della consegna -----

    [Fact]
    public async Task A_download_is_retried_before_the_headers_arrive_in_a_usable_form()
    {
        using var rig = new TransportRig();
        var failed = Reply.Text(503);
        rig.Handler.ThenFail(new HttpRequestException("rete")).Then(failed).Then(Reply.Bytes(200, new byte[5], 5));

        using var download = await rig.Transport.SendDownloadAsync(TransportRig.Get("documents/doc_X/content"), default).AsDisposable();

        Assert.Equal(3, rig.Handler.Requests.Count);
        Assert.Equal(2, rig.Delays.Delays.Count);
        Assert.Equal(1, failed.ContentOf().DisposeCount);
        Assert.Single(rig.Handler.Requests.Select(r => r.Header("X-Request-ID")).Distinct());
    }

    [Fact]
    public async Task Once_the_response_has_been_handed_over_a_failure_in_the_stream_is_never_retried()
    {
        using var rig = new TransportRig();
        var dropping = new ScriptedStream(StreamReading.Pattern(100), maxChunk: 10);
        dropping.FailAtRead[2] = new IOException("rete");
        rig.Handler.Then(Reply.Streamed(200, dropping, declaredLength: 100)).Then(Reply.Bytes(200, StreamReading.Pattern(100), 100));
        using var download = await rig.Transport.SendDownloadAsync(TransportRig.Get("documents/doc_X/content"), default).AsDisposable();

        await Assert.ThrowsAsync<ContentIntegrityException>(() => StreamReading.ReadToEndAsync(download.Response.Content, ReadApi.Async, 64));

        Assert.Single(rig.Handler.Requests);
        Assert.Empty(rig.Delays.Delays);
    }

    // ----- errori -----

    [Theory]
    [InlineData(404, "not-found", typeof(NotFoundException))]
    [InlineData(409, "content-unavailable", typeof(ContentUnavailableException))]
    [InlineData(415, "unsupported-media-type", typeof(UnsupportedMediaTypeException))]
    [InlineData(401, "unauthorized", typeof(UnauthorizedException))]
    [InlineData(500, "internal-error", typeof(ServerErrorException))]
    public async Task An_error_status_is_mapped_and_the_response_is_released(int status, string slug, Type expected)
    {
        using var rig = new TransportRig();
        var reply = Reply.Problem(status, slug);
        rig.Handler.Then(reply);

        var exception = await Assert.ThrowsAnyAsync<FilemasterException>(() => rig.Transport.SendDownloadAsync(TransportRig.Get("documents/doc_X/content"), default));

        Assert.IsType(expected, exception);
        Assert.Equal(1, reply.ContentOf().DisposeCount);
        Assert.Single(rig.Handler.Requests);
    }

    [Fact]
    public async Task A_416_without_a_body_is_an_unexpected_response_with_status_416()
    {
        using var rig = new TransportRig();
        var reply = Reply.Empty(416);
        rig.Handler.Then(reply);

        var exception = await Assert.ThrowsAsync<UnexpectedResponseException>(() => rig.Transport.SendDownloadAsync(TransportRig.Get("documents/doc_X/content"), default));

        Assert.Equal(416, exception.StatusCode);
        Assert.Equal(1, reply.ContentOf().DisposeCount);
    }

    [Fact]
    public async Task A_network_failure_before_the_headers_is_a_ConnectionException()
    {
        using var rig = new TransportRig(o => o.Retry.MaxAttempts = 1);
        rig.Handler.ThenFail(new HttpRequestException("rete"));

        await Assert.ThrowsAsync<ConnectionException>(() => rig.Transport.SendDownloadAsync(TransportRig.Get("documents/doc_X/content"), default));
    }

    [Fact]
    public async Task A_timeout_before_the_headers_is_a_FilemasterTimeoutException()
    {
        using var rig = new TransportRig();
        var entered = Waiting.NewSignal();
        rig.Handler.Then(Waiting.Hang(entered));

        var call = rig.Transport.SendDownloadAsync(TransportRig.Get("documents/doc_X/content"), default);
        await entered.Task;
        rig.Time.Advance(rig.Options.RequestTimeout);

        await Assert.ThrowsAsync<FilemasterTimeoutException>(() => call);
    }

    // ----- tempo del trasferimento -----

    [Fact]
    public async Task After_the_headers_the_time_allowed_is_TransferTimeout_not_RequestTimeout()
    {
        using var rig = new TransportRig(o =>
        {
            o.RequestTimeout = TimeSpan.FromSeconds(30);
            o.TransferTimeout = TimeSpan.FromSeconds(120);
        });
        var hanging = new HangingStream(StreamReading.Pattern(4));
        var reply = Reply.Streamed(200, hanging, declaredLength: 100);
        reply.ContentOf().OnDispose = () => hanging.Fail(new ObjectDisposedException("risposta"));
        rig.Handler.Then(reply);
        var download = await rig.Transport.SendDownloadAsync(TransportRig.Get("documents/doc_X/content"), default);
        var buffer = new byte[16];
        Assert.Equal(4, await download.Content.ReadAsync(buffer, 0, 16));

        var stuck = Task.Run(() => download.Content.ReadAsync(buffer, 0, 16));
        await hanging.ReadStarted;
        rig.Time.Advance(TimeSpan.FromSeconds(30)); // il RequestTimeout non vale piu'
        Assert.False(stuck.IsCompleted);
        rig.Time.Advance(TimeSpan.FromSeconds(89));
        Assert.False(stuck.IsCompleted);
        rig.Time.Advance(TimeSpan.FromSeconds(1)); // 120 s dalle intestazioni

        var exception = await Assert.ThrowsAsync<FilemasterTimeoutException>(() => stuck);
        Assert.Equal("srv-req-1", exception.RequestId); // l'id della risposta (il test ne ha messo uno), poi quello inviato
        Assert.Equal(1, reply.ContentOf().DisposeCount);
        download.Content.Dispose();
    }

    [Fact]
    public async Task An_infinite_TransferTimeout_never_expires()
    {
        using var rig = new TransportRig(o => o.TransferTimeout = Timeout.InfiniteTimeSpan);
        var data = StreamReading.Pattern(10);
        var reply = Reply.Streamed(200, new ScriptedStream(data), declaredLength: 10);
        rig.Handler.Then(reply);
        using var download = await rig.Transport.SendDownloadAsync(TransportRig.Get("documents/doc_X/content"), default).AsDisposable();

        rig.Time.Advance(TimeSpan.FromDays(20));

        Assert.Equal(0, reply.ContentOf().DisposeCount);
        Assert.Equal(data, await StreamReading.ReadToEndAsync(download.Response.Content, ReadApi.Async, 16));
    }

    [Fact]
    public async Task Cancelling_the_token_given_to_the_call_after_delivery_cancels_the_reads_of_the_stream()
    {
        using var rig = new TransportRig();
        using var cts = new CancellationTokenSource();
        var hanging = new HangingStream(StreamReading.Pattern(4));
        var reply = Reply.Streamed(200, hanging, declaredLength: 100);
        reply.ContentOf().OnDispose = () => hanging.Fail(new IOException("risposta chiusa"));
        rig.Handler.Then(reply);
        var download = await rig.Transport.SendDownloadAsync(TransportRig.Get("documents/doc_X/content"), cts.Token);
        var buffer = new byte[16];
        Assert.Equal(4, await download.Content.ReadAsync(buffer, 0, 16));

        var stuck = Task.Run(() => download.Content.ReadAsync(buffer, 0, 16));
        await hanging.ReadStarted;
        cts.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stuck);
        Assert.IsNotType<FilemasterTimeoutException>(exception);
        Assert.Equal(1, reply.ContentOf().DisposeCount);
        download.Content.Dispose();
    }
}

/// <summary>Per smaltire un <see cref="DownloadResponse"/> con <c>using</c>: smaltisce il suo stream (che possiede risposta e scadenza).</summary>
internal static class DownloadResponseExtensions
{
    internal static async Task<DisposableDownload> AsDisposable(this Task<DownloadResponse> download) => new(await download.ConfigureAwait(false));
}

internal sealed class DisposableDownload : IDisposable
{
    internal DisposableDownload(DownloadResponse response)
    {
        Response = response;
    }

    internal DownloadResponse Response { get; }

    public void Dispose() => Response.Content.Dispose();
}
