using System.IO.Compression;
using Filemaster.Application;
using Filemaster.Domain;
using Filemaster.IntegrationTests.Loopback;
using static Filemaster.IntegrationTests.Loopback.LoopbackSupport;

namespace Filemaster.IntegrationTests;

/// <summary>
/// Download col gestore HTTP vero: contenuto intero e parziale, troncamento sul filo (il gestore vero, non uno finto, decide che
/// eccezione esce dalla lettura), nessuna decompressione, connessioni concorrenti.
/// </summary>
public sealed class LoopbackDownloadTests
{
    private const int Size = 50_000;

    private static readonly string[] ContentHeaders =
    {
        "Accept-Ranges: bytes",
        "Last-Modified: Thu, 01 Oct 2026 09:59:15 GMT",
        "Content-Disposition: attachment; filename=\"fattura.pdf\"; filename*=UTF-8''fattura.pdf",
    };

    [Fact]
    public async Task A_whole_download_is_read_to_the_end_with_its_headers()
    {
        using var server = LoopbackServer.Start(exchange => exchange.RespondAsync(200, Bytes(Size), "application/pdf", ContentHeaders));
        using var client = Client(server);

        using var content = await Within(client.Documents.OpenContentAsync(DocId, cancellationToken: TestContext.Current.CancellationToken));
        var bytes = await Within(ReadAllAsync(content.Content));

        Assert.Equal(Bytes(Size), bytes);
        Assert.Equal(Size, content.ContentLength);
        Assert.Equal("application/pdf", content.ContentType);
        Assert.Equal("fattura.pdf", content.FileName);
        Assert.False(content.IsPartial);
        var request = Assert.Single(server.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal("/documents/doc_01M3VEESG5KBYR5PYAJ0TDT4B2/content", request.Target);
        Assert.Equal(0, request.HeaderCount("Range"));
    }

    [Fact]
    public async Task A_range_is_sent_and_a_206_with_Content_Range_is_a_partial_content()
    {
        using var server = LoopbackServer.Start(exchange => exchange.RespondAsync(
            206,
            Bytes(Size).Skip(10).Take(10).ToArray(),
            "application/pdf",
            ContentHeaders.Concat(new[] { "Content-Range: bytes 10-19/50000" }).ToArray()));
        using var client = Client(server);

        using var content = await Within(client.Documents.OpenContentAsync(DocId, ByteRange.Between(10, 19), TestContext.Current.CancellationToken));
        var bytes = await Within(ReadAllAsync(content.Content));

        Assert.Equal("bytes=10-19", Assert.Single(server.Requests).Header("Range"));
        Assert.True(content.IsPartial);
        Assert.Equal(10, content.Range!.FirstByte);
        Assert.Equal(19, content.Range.LastByte);
        Assert.Equal(Size, content.Range.TotalLength);
        Assert.Equal(Bytes(Size).Skip(10).Take(10).ToArray(), bytes);
    }

    [Fact]
    public async Task A_server_that_ignores_the_range_gives_the_whole_content_with_200()
    {
        using var server = LoopbackServer.Start(exchange => exchange.RespondAsync(200, Bytes(Size), "application/pdf", ContentHeaders));
        using var client = Client(server);

        using var content = await Within(client.Documents.OpenContentAsync(DocId, ByteRange.From(100), TestContext.Current.CancellationToken));
        var bytes = await Within(ReadAllAsync(content.Content));

        Assert.Equal("bytes=100-", Assert.Single(server.Requests).Header("Range"));
        Assert.False(content.IsPartial);
        Assert.Equal(Bytes(Size), bytes);
    }

    [Fact]
    public async Task A_body_truncated_on_the_wire_fails_the_read_with_ContentIntegrityException()
    {
        using var server = LoopbackServer.Start(exchange => exchange.RespondTruncatedAsync(200, Bytes(Size), 20_000, "application/pdf", ContentHeaders));
        using var client = Client(server);

        // Le intestazioni arrivano intere: l'apertura riesce, e' la lettura a scoprire il troncamento.
        using var content = await Within(client.Documents.OpenContentAsync(DocId, cancellationToken: TestContext.Current.CancellationToken));
        var exception = await ThrowsWithin<ContentIntegrityException>(() => ReadAllAsync(content.Content));

        Assert.True(exception.IsTruncated);
        Assert.Equal(200, exception.StatusCode);
        Assert.Equal(Size, exception.ExpectedLength);
        Assert.Equal(20_000, exception.ActualLength);

        // Una lettura successiva ripete il verdetto invece di restituire 0 (fine apparente).
        var again = await ThrowsWithin<ContentIntegrityException>(() => ReadAllAsync(content.Content));
        Assert.True(again.IsTruncated);
    }

    [Fact]
    public async Task A_chunked_download_without_Content_Length_is_read_to_the_end()
    {
        var whole = Bytes(Size);
        using var server = LoopbackServer.Start(exchange => exchange.RespondChunkedAsync(200, "application/pdf", whole.Take(1000).ToArray(), whole.Skip(1000).ToArray()));
        using var client = Client(server);

        using var content = await Within(client.Documents.OpenContentAsync(DocId, cancellationToken: TestContext.Current.CancellationToken));
        var bytes = await Within(ReadAllAsync(content.Content));

        Assert.Null(content.ContentLength);
        Assert.Equal(whole, bytes);
    }

    [Fact]
    public async Task A_chunked_download_cut_before_the_last_chunk_is_truncated_without_an_expected_length()
    {
        using var server = LoopbackServer.Start(async exchange =>
        {
            await exchange.WriteHeadAsync(200, "application/pdf", contentLength: null, "Transfer-Encoding: chunked").ConfigureAwait(false);
            await exchange.WriteAsync(Utf8("3e8\r\n")).ConfigureAwait(false);
            await exchange.WriteAsync(Bytes(1000)).ConfigureAwait(false);
            await exchange.WriteAsync(Utf8("\r\n")).ConfigureAwait(false);
            exchange.Close();
        });
        using var client = Client(server);

        using var content = await Within(client.Documents.OpenContentAsync(DocId, cancellationToken: TestContext.Current.CancellationToken));
        var exception = await ThrowsWithin<ContentIntegrityException>(() => ReadAllAsync(content.Content));

        Assert.True(exception.IsTruncated);
        Assert.Null(exception.ExpectedLength);
        Assert.Equal(1000, exception.ActualLength);
    }

    [Fact]
    public async Task A_gzip_response_is_not_decompressed_and_none_is_asked_for()
    {
        var plain = Bytes(Size);
        var gzip = Gzip(plain);
        using var server = LoopbackServer.Start(exchange => exchange.RespondAsync(200, gzip, "application/pdf", "Content-Encoding: gzip"));
        using var client = Client(server);

        using var content = await Within(client.Documents.OpenContentAsync(DocId, cancellationToken: TestContext.Current.CancellationToken));
        var bytes = await Within(ReadAllAsync(content.Content));

        // Il client non chiede compressione (nessun Accept-Encoding) e, se un server la applica lo stesso, consegna i byte come sono
        // arrivati: lunghezza = Content-Length del filo, nessun ContentIntegrityException (FilemasterHandlers: niente decompressione).
        Assert.Equal(0, Assert.Single(server.Requests).HeaderCount("Accept-Encoding"));
        Assert.Equal(gzip.Length, content.ContentLength);
        Assert.Equal(gzip, bytes);
        Assert.NotEqual(plain.Length, bytes.Length);
    }

    [Fact]
    public async Task A_gzip_JSON_response_is_not_decompressed_and_is_an_unexpected_response()
    {
        using var server = LoopbackServer.Start(exchange => exchange.RespondAsync(200, Gzip(Utf8(PageJson)), "application/json; charset=utf-8", "Content-Encoding: gzip"));
        using var client = Client(server);

        var exception = await ThrowsWithin<UnexpectedResponseException>(() => client.Documents.ListAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(200, exception.StatusCode);
        Assert.Equal(0, Assert.Single(server.Requests).HeaderCount("Accept-Encoding"));
    }

    [Fact]
    public async Task Several_open_unread_downloads_do_not_block_a_list_call()
    {
        // Quattro download aperti e fermi (intestazioni e un pezzo di corpo, poi il server aspetta) piu' un elenco: con il limite di
        // .NET Framework (2 connessioni per server) l'elenco resterebbe in coda; su .NET 5+ il limite di default e' gia' illimitato.
        const int Downloads = 4;
        var release = LoopbackServer.NewSignal();
        using var server = LoopbackServer.Start(async exchange =>
        {
            if (exchange.Path.EndsWith("/content", StringComparison.Ordinal))
            {
                var body = Bytes(Size);
                await exchange.WriteHeadAsync(200, "application/pdf", Size).ConfigureAwait(false);
                await exchange.WriteAsync(body.Take(1000).ToArray()).ConfigureAwait(false);
                await exchange.WaitAsync(release.Task).ConfigureAwait(false);
                await exchange.WriteAsync(body.Skip(1000).ToArray()).ConfigureAwait(false);
            }
            else
            {
                await exchange.RespondAsync(200, PageJson).ConfigureAwait(false);
            }
        });
        using var client = Client(server);
        var token = TestContext.Current.CancellationToken;

        var contents = new List<DocumentContent>();
        try
        {
            for (var i = 0; i < Downloads; i++)
            {
                contents.Add(await Within(client.Documents.OpenContentAsync(DocId, cancellationToken: token)));
            }

            var page = await Within(client.Documents.ListAsync(cancellationToken: token));
            Assert.Single(page.Items);
            Assert.Equal(Downloads + 1, server.ConnectionCount);

            release.TrySetResult(true);
            foreach (var content in contents)
            {
                Assert.Equal(Bytes(Size), await Within(ReadAllAsync(content.Content)));
            }
        }
        finally
        {
            release.TrySetResult(true);
            foreach (var content in contents)
            {
                content.Dispose();
            }
        }
    }

    private static byte[] Gzip(byte[] plain)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress, leaveOpen: true))
        {
            gzip.Write(plain, 0, plain.Length);
        }

        return output.ToArray();
    }
}
