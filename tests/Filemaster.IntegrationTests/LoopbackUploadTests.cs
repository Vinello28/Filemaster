using System.Text;
using System.Text.Json;
using Filemaster.Application;
using Filemaster.Domain;
using Filemaster.IntegrationTests.Loopback;
using static Filemaster.IntegrationTests.Loopback.LoopbackSupport;

namespace Filemaster.IntegrationTests;

/// <summary>
/// Upload col gestore HTTP vero su una connessione TCP vera: cosa arriva sul filo (multipart, ordine delle parti, nome non ASCII,
/// <c>Content-Length</c> o <c>chunked</c>) e cosa resta allo stream dell'utente.
/// </summary>
public sealed class LoopbackUploadTests
{
    // "perche' e'.pdf" con le lettere accentate vere (e acuta, e grave).
    private const string NonAsciiName = "perch\U000000E9 \U000000E8.pdf";

    private static LoopbackServer UploadServer() => LoopbackServer.Start(async exchange =>
    {
        await exchange.ReadBodyAsync().ConfigureAwait(false);
        await exchange.RespondAsync(201, UploadJson).ConfigureAwait(false);
    });

    private static UploadDocumentRequest Request(Stream content)
    {
        using var metadata = JsonDocument.Parse("""{"arxivar":{"docnumber":12345}}""");
        return new UploadDocumentRequest(content, NonAsciiName)
        {
            ContentType = "application/pdf",
            FolderId = new FolderCode("FATTURE"),
            Owner = "maria",
            Tag = "fattura",
            Metadata = metadata.RootElement.Clone(),
        };
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_multipart_body_arrives_whole_with_the_fields_in_order_and_the_file_last(bool seekable)
    {
        using var server = UploadServer();
        using var client = Client(server);
        using var source = new GeneratedStream(300_000, seekable);

        var result = await Within(client.Documents.UploadAsync(Request(source), TestContext.Current.CancellationToken));

        Assert.Equal("5000000001", result.Document.Id.Value);
        var request = Assert.Single(server.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/documents", request.Target);
        Assert.True(request.BodyComplete);
        var parts = MultipartParts.Parse(request.Header("Content-Type")!, request.Body!);
        Assert.Equal(new[] { "folder_id", "owner", "tag", "metadata", "file" }, parts.Select(p => p.Name));
        Assert.Equal("FATTURE", Encoding.UTF8.GetString(parts[0].Body));
        Assert.Equal("""{"arxivar":{"docnumber":12345}}""", Encoding.UTF8.GetString(parts[3].Body));
        var file = parts[4];
        Assert.Equal("form-data; name=\"file\"; filename=\"perch_ _.pdf\"; filename*=utf-8''perch%C3%A9%20%C3%A8.pdf", file.Header("Content-Disposition"));
        Assert.Equal("application/pdf", file.Header("Content-Type"));
        Assert.Equal(Bytes(300_000), file.Body);

        // Lo stream dell'utente e' stato letto fino alla fine e non e' stato chiuso.
        Assert.False(source.Disposed);
        Assert.Equal(300_000, source.Consumed);
    }

    [Fact]
    public async Task A_seekable_stream_is_sent_with_Content_Length_and_never_chunked()
    {
        using var server = UploadServer();
        using var client = Client(server);
        using var source = new GeneratedStream(100_000, seekable: true);

        await Within(client.Documents.UploadAsync(Request(source), TestContext.Current.CancellationToken));

        var request = Assert.Single(server.Requests);
        Assert.False(request.IsChunked);
        Assert.Equal(request.BodyLength, request.ContentLength);
        Assert.Equal(0, request.HeaderCount("Transfer-Encoding"));
    }

    [Fact]
    public async Task A_non_seekable_stream_is_sent_chunked_without_Content_Length()
    {
        using var server = UploadServer();
        using var client = Client(server);
        using var source = new GeneratedStream(100_000, seekable: false);

        await Within(client.Documents.UploadAsync(Request(source), TestContext.Current.CancellationToken));

        var request = Assert.Single(server.Requests);
        Assert.True(request.IsChunked);
        Assert.Null(request.ContentLength);
        Assert.True(request.BodyLength > 100_000);
    }

    [Fact]
    public async Task The_upload_starts_from_the_current_position_of_a_seekable_stream()
    {
        using var server = UploadServer();
        using var client = Client(server);
        using var source = new GeneratedStream(10_000, seekable: true) { Position = 4_000 };

        await Within(client.Documents.UploadAsync(Request(source), TestContext.Current.CancellationToken));

        var request = Assert.Single(server.Requests);
        var parts = MultipartParts.Parse(request.Header("Content-Type")!, request.Body!);
        var file = parts[parts.Count - 1];
        Assert.Equal(Bytes(10_000).Skip(4_000).ToArray(), file.Body);
        Assert.Equal(request.BodyLength, request.ContentLength);
    }
}
