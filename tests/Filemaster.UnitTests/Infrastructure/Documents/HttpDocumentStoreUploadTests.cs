using System.Text.Json;
using Filemaster.Application;
using Filemaster.Domain;
using Filemaster.UnitTests.Wire;

namespace Filemaster.UnitTests.Infrastructure.Documents;

/// <summary>
/// Il caricamento (<c>POST /documents</c>): corpo <c>multipart/form-data</c> letto byte per byte da un parser indipendente, campi di testo
/// nell'ordine del wire con i valori della cattura 47, file per ULTIMO con l'intestazione della cattura 54 (anche con un nome non ASCII),
/// contenuto dalla posizione corrente in streaming, lunghezza nota solo per uno stream riposizionabile, stream dell'utente MAI chiuso
/// (successo, errore del server, errore di rete, annullamento), mai ritentato, errori principali mappati.
/// </summary>
public sealed class HttpDocumentStoreUploadTests
{
    private static readonly byte[] Pdf = StreamReading.Pattern(5000);

    private static UploadDocumentRequest FullRequest(Stream content)
    {
        using var metadata = JsonDocument.Parse("{\"arxivar\":{\"docnumber\":12345,\"categoria\":\"X\"}}");
        return new UploadDocumentRequest(content, "fattura.pdf")
        {
            ContentType = "application/pdf",
            FolderId = new FolderCode("FATTURE"),
            Owner = "maria",
            Tag = "fattura",
            Sender = "Acme Srl",
            Recipient = "Beta Spa",
            Metadata = metadata.RootElement.Clone(),
        };
    }

    [Fact]
    public async Task The_upload_is_a_multipart_POST_with_the_fields_of_capture_47_in_wire_order_and_the_file_last()
    {
        using var rig = new StoreRig();
        rig.Then(() => FixtureReply.Json("47-doc-upload"));

        var result = await rig.Store.UploadAsync(FullRequest(new UserStream(Pdf)));

        var sent = rig.Single;
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal(WireFixtures.RequestPath("47-doc-upload"), sent.PathAndQuery);
        Assert.Equal("application/json", sent.Header("Accept"));
        var parts = Multipart.Parse(sent);
        Assert.Equal(
            new[]
            {
                "form-data; name=\"folder_id\"",
                "form-data; name=\"owner\"",
                "form-data; name=\"tag\"",
                "form-data; name=\"sender\"",
                "form-data; name=\"recipient\"",
                "form-data; name=\"metadata\"",
                "form-data; name=\"file\"; filename=\"fattura.pdf\"; filename*=utf-8''fattura.pdf",
            },
            parts.Select(p => p.Header("Content-Disposition")).ToArray());

        // I valori dei campi sono quelli che il server ha accettato nella cattura 47 (righe "-F nome=valore" del .request).
        var request47 = WireFixtures.CapturedText("47-doc-upload", "request");
        foreach (var part in parts.Take(6))
        {
            var name = part.Header("Content-Disposition")!.Split('"')[1];
            Assert.Contains("# curl: " + name + "=" + part.Text + "\n", request47, StringComparison.Ordinal);
            Assert.Null(part.Header("Content-Type"));
        }

        var file = parts[6];
        Assert.Equal("application/pdf", file.Header("Content-Type"));
        Assert.Equal(Pdf, file.Content);
        Assert.Equal(StoreRig.Id, result.Document.Id);
        Assert.True(result.Deduplicated); // in t64 i byte c'erano gia' da una corsa precedente: anche la 47 e' "deduplicated":true
    }

    [Fact]
    public async Task A_deduplicated_upload_reads_deduplicated_true_from_capture_48()
    {
        using var rig = new StoreRig();
        rig.Then(() => FixtureReply.Json("48-doc-upload-dedup"));

        var result = await rig.Store.UploadAsync(new UploadDocumentRequest(new UserStream(Pdf), "fattura.pdf"));

        Assert.True(result.Deduplicated);
    }

    [Fact]
    public async Task Only_the_file_part_is_sent_when_no_optional_field_is_set_and_without_ContentType_the_part_has_none()
    {
        using var rig = new StoreRig();
        rig.Then(() => FixtureReply.Json("49-doc-upload-no-metadata"));

        await rig.Store.UploadAsync(new UploadDocumentRequest(new UserStream(Pdf), "a.pdf"));

        var part = Assert.Single(Multipart.Parse(rig.Single));
        Assert.Equal("form-data; name=\"file\"; filename=\"a.pdf\"; filename*=utf-8''a.pdf", part.Header("Content-Disposition"));
        Assert.Null(part.Header("Content-Type"));
        Assert.Equal(Pdf, part.Content);
    }

    [Fact]
    public async Task A_non_ascii_file_name_has_the_ascii_fallback_and_the_utf8_percent_form_and_text_fields_are_raw_utf8()
    {
        using var rig = new StoreRig();
        rig.Then(() => FixtureReply.Json("52-doc-upload-nonascii-raw-filename"));
        var request = new UploadDocumentRequest(new UserStream(Pdf), "perch\U000000E9 \U000000E8.pdf") { Owner = "Jos\U000000E9" };

        await rig.Store.UploadAsync(request);

        var parts = Multipart.Parse(rig.Single);
        Assert.Equal(2, parts.Count);
        Assert.Equal("form-data; name=\"owner\"", parts[0].Header("Content-Disposition"));
        Assert.Equal(new byte[] { 0x4A, 0x6F, 0x73, 0xC3, 0xA9 }, parts[0].Content);
        Assert.Equal(
            "form-data; name=\"file\"; filename=\"perch_ _.pdf\"; filename*=utf-8''perch%C3%A9%20%C3%A8.pdf",
            parts[1].Header("Content-Disposition"));
    }

    [Fact]
    public async Task A_file_name_with_a_lone_surrogate_is_ArgumentException_on_FileName_and_nothing_is_sent()
    {
        using var rig = new StoreRig();
        var request = new UploadDocumentRequest(new UserStream(Pdf), "a" + new string((char)0xD800, 1) + ".pdf");

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => rig.Store.UploadAsync(request));

        Assert.Equal(nameof(UploadDocumentRequest.FileName), exception.ParamName);
        Assert.Empty(rig.Handler.Requests);
    }

    [Fact]
    public async Task The_content_is_read_from_the_current_position_and_the_length_is_known_for_a_seekable_stream()
    {
        using var rig = new StoreRig();
        rig.Then(() => FixtureReply.Json("49-doc-upload-no-metadata"));
        var source = new UserStream(Pdf) { Position = 1000 };

        await rig.Store.UploadAsync(new UploadDocumentRequest(source, "a.pdf"));

        var sent = rig.Single;
        var file = Assert.Single(Multipart.Parse(sent));
        Assert.Equal(Pdf.Skip(1000).ToArray(), file.Content);
        Assert.Equal(sent.Body!.Length, sent.DeclaredLength);
        Assert.Equal(Pdf.Length, source.Position); // letto fino alla fine, mai riavvolto
    }

    [Fact]
    public async Task A_seekable_stream_positioned_past_its_end_sends_an_empty_file_with_a_consistent_length()
    {
        using var rig = new StoreRig();
        rig.Then(() => FixtureReply.Json("49-doc-upload-no-metadata"));
        var source = new UserStream(Pdf) { Position = Pdf.Length + 10 };

        await rig.Store.UploadAsync(new UploadDocumentRequest(source, "a.pdf"));

        var sent = rig.Single;
        Assert.Empty(Assert.Single(Multipart.Parse(sent)).Content);
        Assert.Equal(sent.Body!.Length, sent.DeclaredLength);
    }

    [Fact]
    public async Task A_non_seekable_stream_is_sent_chunked_without_being_read_before_the_handler_asks_for_it()
    {
        using var rig = new StoreRig();
        var source = new UserStream(Pdf, canSeek: false);
        var readsBefore = -1;
        rig.Handler.Then(async (request, _) =>
        {
            readsBefore = source.ReadCalls;
            var declared = request.Content!.Headers.ContentLength;
            Assert.Null(declared);
#pragma warning disable CA2016 // net48 non ha l'overload con il token.
            var body = await request.Content.ReadAsByteArrayAsync();
#pragma warning restore CA2016
            Assert.Equal(Pdf, Multipart.Parse(body, Boundary(request)).Single().Content);
            return FixtureReply.Json("49-doc-upload-no-metadata");
        });

        await rig.Store.UploadAsync(new UploadDocumentRequest(source, "a.pdf"));

        Assert.Equal(0, readsBefore);
        Assert.True(source.ReadCalls > 0);
    }

    [Fact]
    public async Task A_large_non_seekable_stream_is_copied_in_pieces_not_in_one_read()
    {
        using var rig = new StoreRig();
        var data = StreamReading.Pattern(1_000_000);
        var source = new UserStream(data, canSeek: false);
        rig.Then(() => FixtureReply.Json("49-doc-upload-no-metadata"));

        await rig.Store.UploadAsync(new UploadDocumentRequest(source, "a.bin"));

        Assert.Equal(data, Assert.Single(Multipart.Parse(rig.Single)).Content);
        Assert.True(source.ReadCalls > 2, "lo stream va copiato a pezzi");
    }

    // ----- lo stream dell'utente non si chiude mai -----

    [Fact]
    public async Task The_user_stream_is_not_closed_after_a_successful_upload()
    {
        using var rig = new StoreRig();
        rig.Then(() => FixtureReply.Json("47-doc-upload"));
        var source = new UserStream(Pdf);

        await rig.Store.UploadAsync(new UploadDocumentRequest(source, "fattura.pdf"));

        Assert.Equal(0, source.DisposeCount);
        Assert.True(source.CanRead);
    }

    [Fact]
    public async Task The_user_stream_is_not_closed_after_a_server_error()
    {
        using var rig = new StoreRig();
        rig.Then(() => Reply.Problem(413, "request-too-large"));
        var source = new UserStream(Pdf);

        await Assert.ThrowsAsync<RequestTooLargeException>(() => rig.Store.UploadAsync(new UploadDocumentRequest(source, "a.pdf")));

        Assert.Equal(0, source.DisposeCount);
    }

    [Fact]
    public async Task The_user_stream_is_not_closed_after_a_network_error_in_the_middle_of_the_body()
    {
        using var rig = new StoreRig();
        var source = new UserStream(Pdf, canSeek: false);
        rig.Handler.Then(async (request, _) =>
        {
#pragma warning disable CA2016 // net48 non ha l'overload con il token.
            await request.Content!.ReadAsByteArrayAsync();
#pragma warning restore CA2016
            throw new IOException("connection reset by peer");
        });

        var exception = await Assert.ThrowsAsync<ConnectionException>(() => rig.Store.UploadAsync(new UploadDocumentRequest(source, "a.pdf")));

        Assert.IsType<IOException>(exception.InnerException);
        Assert.Equal(0, source.DisposeCount);
        Assert.Single(rig.Handler.Requests);
    }

    [Fact]
    public async Task The_user_stream_is_not_closed_after_a_cancellation_in_the_middle_of_the_transfer()
    {
        using var rig = new StoreRig();
        using var cts = new CancellationTokenSource();
        var entered = Waiting.NewSignal();
        rig.Handler.Then(Waiting.Hang(entered));
        var source = new UserStream(Pdf);

        var call = rig.Store.UploadAsync(new UploadDocumentRequest(source, "a.pdf"), cts.Token);
        await Waiting.Within(entered.Task);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Waiting.Within(call));
        Assert.Equal(0, source.DisposeCount);
    }

    [Fact]
    public async Task The_user_stream_is_not_read_when_the_request_is_invalid()
    {
        using var rig = new StoreRig();
        var source = new UserStream(Pdf);

        await Assert.ThrowsAsync<ArgumentException>(() => rig.Store.UploadAsync(new UploadDocumentRequest(source, "cartella/")));

        Assert.Equal(0, source.ReadCalls);
        Assert.Equal(0, source.Position);
        Assert.Equal(0, source.DisposeCount);
        Assert.Empty(rig.Handler.Requests);
    }

    // ----- mai ritentato, errori -----

    [Theory]
    [InlineData(503)]
    [InlineData(502)]
    [InlineData(429)]
    public async Task An_upload_is_never_retried(int status)
    {
        using var rig = new StoreRig();
        rig.Then(() => Reply.Text(status));
        rig.Then(() => FixtureReply.Json("47-doc-upload"));

        await Assert.ThrowsAnyAsync<FilemasterException>(() => rig.Store.UploadAsync(new UploadDocumentRequest(new UserStream(Pdf), "a.pdf")));

        Assert.Single(rig.Sent);
        Assert.Empty(rig.Rig.Delays.Delays);
    }

    [Theory]
    [InlineData(400, "validation-error", typeof(InvalidRequestException))]
    [InlineData(404, "not-found", typeof(NotFoundException))]
    [InlineData(413, "request-too-large", typeof(RequestTooLargeException))]
    [InlineData(415, "error", typeof(UnsupportedMediaTypeException))]
    [InlineData(503, "storage-not-configured", typeof(StorageNotConfiguredException))]
    [InlineData(403, "forbidden", typeof(ForbiddenException))]
    public async Task The_errors_of_an_upload_are_mapped(int status, string slug, Type expected)
    {
        using var rig = new StoreRig();
        rig.Then(() => Reply.Problem(status, slug));

        var exception = await Assert.ThrowsAnyAsync<FilemasterException>(() => rig.Store.UploadAsync(new UploadDocumentRequest(new UserStream(Pdf), "a.pdf")));

        Assert.IsType(expected, exception);
        Assert.Equal(status, exception.StatusCode);
    }

    [Fact]
    public async Task An_upload_answer_without_deduplicated_is_UnexpectedResponseException_201()
    {
        using var rig = new StoreRig();
        rig.Then(() => FixtureReply.Json("98-doc-get", status: 201));

        var exception = await Assert.ThrowsAsync<UnexpectedResponseException>(() => rig.Store.UploadAsync(new UploadDocumentRequest(new UserStream(Pdf), "a.pdf")));

        Assert.Equal(201, exception.StatusCode);
    }

    // ----- tempo -----

    [Fact]
    public async Task The_upload_has_TransferTimeout_not_RequestTimeout()
    {
        using var rig = new StoreRig(o =>
        {
            o.RequestTimeout = TimeSpan.FromSeconds(5);
            o.TransferTimeout = TimeSpan.FromSeconds(60);
        });
        var entered = Waiting.NewSignal();
        var release = Waiting.NewSignal();
        rig.Handler.Then(Waiting.UntilReleased(entered, release, () => FixtureReply.Json("47-doc-upload")));

        var call = rig.Store.UploadAsync(new UploadDocumentRequest(new UserStream(Pdf), "a.pdf"));
        await Waiting.Within(entered.Task);
        rig.Rig.Time.Advance(TimeSpan.FromSeconds(59)); // molto oltre RequestTimeout, ancora dentro TransferTimeout
        Assert.False(call.IsCompleted);
        release.SetResult(true);

        await Waiting.Within(call);
        Assert.Equal(StoreRig.Id, (await call).Document.Id);
    }

    [Fact]
    public async Task When_TransferTimeout_passes_the_upload_is_FilemasterTimeoutException_and_the_stream_stays_open()
    {
        using var rig = new StoreRig(o => o.TransferTimeout = TimeSpan.FromSeconds(60));
        var entered = Waiting.NewSignal();
        rig.Handler.Then(Waiting.Hang(entered));
        var source = new UserStream(Pdf);

        var call = rig.Store.UploadAsync(new UploadDocumentRequest(source, "a.pdf"));
        await Waiting.Within(entered.Task);
        rig.Rig.Time.Advance(TimeSpan.FromSeconds(60));

        await Assert.ThrowsAsync<FilemasterTimeoutException>(() => Waiting.Within(call));
        Assert.Equal(0, source.DisposeCount);
    }

    private static string Boundary(HttpRequestMessage request) =>
        request.Content!.Headers.ContentType!.Parameters.Single(p => p.Name == "boundary").Value!.Trim('"');
}
