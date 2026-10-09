// Su net8/net10 CA2016 chiede di inoltrare un token a ReadAsByteArrayAsync, ma su net48 l'overload con token non esiste.
#pragma warning disable CA2016

using Filemaster.Domain;
using Filemaster.Infrastructure;
using Filemaster.UnitTests.Application;

namespace Filemaster.UnitTests.Infrastructure;

/// <summary>
/// La modalita' "upload in streaming": il corpo e' quello che l'adapter mette nel messaggio (qui un <see cref="StreamContent"/> su uno
/// stream che non si riavvolge), il trasporto non lo legge ne' lo copia, non si ritenta mai, <c>TransferTimeout</c> vale sull'intera
/// chiamata (non <c>RequestTimeout</c>), l'annullamento a meta' trasferimento e' <see cref="OperationCanceledException"/>, e gli errori
/// (413 del server, connessione chiusa dal server web a meta' corpo) sono mappati. <b>Non verificato in locale: su .NET Framework
/// (net48) <c>HttpClientHandler</c> puo' bufferizzare il corpo della richiesta; si prova su Windows CI (T6.1).</b>
/// </summary>
public sealed class TransportUploadTests
{
    private static TransportRequest UploadRequest(Stream source)
    {
        var request = TransportRig.Post("documents");
        request.Customize = message => message.Content = new StreamContent(source);
        return request;
    }

    [Fact]
    public async Task The_body_is_streamed_and_not_read_or_buffered_before_the_handler_asks_for_it()
    {
        using var rig = new TransportRig();
        var data = StreamReading.Pattern(100_000);
        var source = new ScriptedStream(data, maxChunk: 8192);
        var readsBefore = -1;
        long? declaredLength = -1;
        byte[]? received = null;
        rig.Handler.Then(async (request, _) =>
        {
            readsBefore = source.ReadCalls;
            declaredLength = request.Content!.Headers.ContentLength;
            received = await request.Content.ReadAsByteArrayAsync();
            return Reply.Json(201, "{\"id\":42,\"deduplicated\":false}");
        });

        var response = await rig.Transport.SendUploadAsync(UploadRequest(source), default);

        Assert.Equal(201, response.StatusCode);
        Assert.Equal(0, readsBefore); // il trasporto non ha toccato lo stream
        Assert.Null(declaredLength); // nessuna lunghezza: il corpo non e' stato misurato leggendolo
        Assert.Equal(data, received);
        Assert.Contains("42", System.Text.Encoding.UTF8.GetString(response.Body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_transport_headers_are_on_the_upload_too_and_the_method_is_kept()
    {
        using var rig = new TransportRig();
        rig.Handler.Then(Reply.Json(201, "{}"));

        await rig.Transport.SendUploadAsync(UploadRequest(new MemoryStream(new byte[3])), default);

        var sent = Assert.Single(rig.Handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal(TransportRig.Key, sent.Header("X-API-Key"));
        Assert.Equal(32, sent.Header("X-Request-ID")!.Length);
    }

    [Fact]
    public async Task The_request_message_and_so_its_content_is_disposed_after_the_call_the_adapter_must_not_hand_over_the_user_stream()
    {
        // Documenta un obbligo per gli adapter (T4.3): il trasporto smaltisce il messaggio, quindi il contenuto, quindi uno
        // StreamContent smaltisce lo stream che avvolge. Lo stream dell'utente non va chiuso dalla libreria: va in un contenuto che non lo chiude.
        using var rig = new TransportRig();
        var source = new MemoryStream(new byte[3]);
        rig.Handler.Then(Reply.Json(201, "{}"));

        await rig.Transport.SendUploadAsync(UploadRequest(source), default);

        Assert.False(source.CanRead);
    }

    // ----- mai ritentato -----

    [Theory]
    [InlineData(503)]
    [InlineData(502)]
    [InlineData(504)]
    [InlineData(429)]
    [InlineData(408)]
    public async Task An_upload_is_never_retried_on_a_transient_status(int status)
    {
        using var rig = new TransportRig();
        var customized = 0;
        var request = TransportRig.Post("documents");
        request.Customize = message =>
        {
            customized++;
            message.Content = new StreamContent(new MemoryStream(new byte[3]));
        };
        rig.Handler.Then(Reply.Text(status)).Then(Reply.Json(201, "{}"));

        await Assert.ThrowsAnyAsync<FilemasterException>(() => rig.Transport.SendUploadAsync(request, default));

        Assert.Single(rig.Handler.Requests);
        Assert.Equal(1, customized);
        Assert.Empty(rig.Delays.Delays);
    }

    [Fact]
    public async Task An_upload_is_never_retried_on_a_connection_error()
    {
        using var rig = new TransportRig();
        rig.Handler.ThenFail(new HttpRequestException("rete")).Then(Reply.Json(201, "{}"));

        var exception = await Assert.ThrowsAsync<ConnectionException>(() => rig.Transport.SendUploadAsync(UploadRequest(new MemoryStream(new byte[3])), default));

        Assert.IsType<HttpRequestException>(exception.InnerException);
        Assert.Single(rig.Handler.Requests);
    }

    [Fact]
    public async Task Even_an_upload_sent_with_GET_as_method_is_not_retried_because_only_the_buffered_and_download_modes_retry()
    {
        using var rig = new TransportRig();
        rig.Handler.Then(Reply.Text(503)).Then(Reply.Json(200, "{}"));

        await Assert.ThrowsAsync<ServerErrorException>(() => rig.Transport.SendUploadAsync(TransportRig.Get("documents"), default));

        Assert.Single(rig.Handler.Requests);
    }

    // ----- errori del server -----

    [Fact]
    public async Task A_413_from_the_store_limit_is_RequestTooLargeException()
    {
        using var rig = new TransportRig();
        rig.Handler.Then(Reply.Problem(413, "request-too-large", "contenuto troppo grande"));

        var exception = await Assert.ThrowsAsync<RequestTooLargeException>(() => rig.Transport.SendUploadAsync(UploadRequest(new MemoryStream(new byte[3])), default));

        Assert.Equal(413, exception.StatusCode);
        Assert.Equal("contenuto troppo grande", exception.Detail);
    }

    [Theory]
    [InlineData(400, "validation-error", typeof(InvalidRequestException))]
    [InlineData(404, "not-found", typeof(NotFoundException))]
    [InlineData(403, "forbidden", typeof(ForbiddenException))]
    [InlineData(415, "error", typeof(UnsupportedMediaTypeException))]
    [InlineData(503, "storage-not-configured", typeof(StorageNotConfiguredException))]
    public async Task The_other_errors_of_an_upload_are_mapped(int status, string slug, Type expected)
    {
        using var rig = new TransportRig();
        rig.Handler.Then(Reply.Problem(status, slug));

        var exception = await Assert.ThrowsAnyAsync<FilemasterException>(() => rig.Transport.SendUploadAsync(UploadRequest(new MemoryStream(new byte[3])), default));

        Assert.IsType(expected, exception);
    }

    [Fact]
    public async Task A_connection_closed_by_the_web_server_in_the_middle_of_the_body_is_a_ConnectionException_that_cannot_be_told_from_a_network_error()
    {
        // Il 413 del limite del server web chiude la connessione: il client vede un errore di rete, non un 413 (non distinguibile).
        using var rig = new TransportRig();
        var source = new ScriptedStream(StreamReading.Pattern(100_000), maxChunk: 4096);
        rig.Handler.Then(async (request, _) =>
        {
            await request.Content!.ReadAsByteArrayAsync();
            throw new IOException("Unable to write data to the transport connection: connection reset by peer.");
        });

        var exception = await Assert.ThrowsAsync<ConnectionException>(() => rig.Transport.SendUploadAsync(UploadRequest(source), default));

        Assert.IsType<IOException>(exception.InnerException);
        Assert.Equal(0, exception.StatusCode);
    }

    // ----- tempo -----

    [Fact]
    public async Task The_whole_upload_has_TransferTimeout_not_RequestTimeout()
    {
        using var rig = new TransportRig(o =>
        {
            o.RequestTimeout = TimeSpan.FromSeconds(5);
            o.TransferTimeout = TimeSpan.FromSeconds(60);
        });
        var entered = Waiting.NewSignal();
        var release = Waiting.NewSignal();
        rig.Handler.Then(Waiting.UntilReleased(entered, release, () => Reply.Json(201, "{}")));

        var call = rig.Transport.SendUploadAsync(UploadRequest(new MemoryStream(new byte[3])), default);
        await entered.Task;
        rig.Time.Advance(TimeSpan.FromSeconds(59)); // molto oltre RequestTimeout, ancora dentro TransferTimeout
        Assert.False(call.IsCompleted);
        release.SetResult(true);

        Assert.Equal(201, (await call).StatusCode);
    }

    [Fact]
    public async Task When_TransferTimeout_passes_the_upload_ends_in_a_FilemasterTimeoutException()
    {
        using var rig = new TransportRig(o => o.TransferTimeout = TimeSpan.FromSeconds(60));
        var entered = Waiting.NewSignal();
        rig.Handler.Then(Waiting.Hang(entered));

        var call = rig.Transport.SendUploadAsync(UploadRequest(new MemoryStream(new byte[3])), default);
        await entered.Task;
        rig.Time.Advance(TimeSpan.FromSeconds(60));

        var exception = await Assert.ThrowsAsync<FilemasterTimeoutException>(() => call);
        Assert.Equal(rig.Handler.Requests[0].Header("X-Request-ID"), exception.RequestId);
        Assert.Single(rig.Handler.Requests);
    }

    [Fact]
    public async Task An_infinite_TransferTimeout_never_expires_for_an_upload()
    {
        using var rig = new TransportRig(o => o.TransferTimeout = Timeout.InfiniteTimeSpan);
        var entered = Waiting.NewSignal();
        var release = Waiting.NewSignal();
        rig.Handler.Then(Waiting.UntilReleased(entered, release, () => Reply.Json(201, "{}")));

        var call = rig.Transport.SendUploadAsync(UploadRequest(new MemoryStream(new byte[3])), default);
        await entered.Task;
        rig.Time.Advance(TimeSpan.FromDays(20));
        Assert.False(call.IsCompleted);
        release.SetResult(true);

        Assert.Equal(201, (await call).StatusCode);
    }

    [Fact]
    public async Task Cancelling_in_the_middle_of_the_transfer_is_an_OperationCanceledException()
    {
        using var rig = new TransportRig();
        using var cts = new CancellationTokenSource();
        var entered = Waiting.NewSignal();
        rig.Handler.Then(Waiting.Hang(entered));

        var call = rig.Transport.SendUploadAsync(UploadRequest(new MemoryStream(new byte[3])), cts.Token);
        await entered.Task;
        cts.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.IsNotType<FilemasterTimeoutException>(exception);
    }
}
