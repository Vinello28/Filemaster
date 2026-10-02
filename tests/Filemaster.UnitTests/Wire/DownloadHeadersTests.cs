using System.Globalization;
using System.Net;
using Filemaster.Application;
using Filemaster.Domain;
using Filemaster.Infrastructure;

namespace Filemaster.UnitTests.Wire;

/// <summary>
/// Le intestazioni di un download verso <see cref="DocumentContent"/>: tipo, nome del file (da <c>filename*</c>), lunghezza, ultima modifica e,
/// solo per un 206, quale parte e' arrivata. Le risposte sono costruite con le intestazioni vere delle catture 108-122 (file <c>.headers</c>),
/// aggiunte senza validazione come fa un gestore HTTP; i valori attesi sono ricopiati dai file.
/// </summary>
public sealed class DownloadHeadersTests
{
    private static readonly string[] ContentHeaderNames =
    {
        "Content-Type", "Content-Disposition", "Content-Range", "Content-Length", "Content-Encoding", "Content-Language", "Last-Modified", "Expires", "Allow",
    };

    // Una risposta di download con le intestazioni di una cattura (stato compreso) o fatte a mano.
    private static DownloadResponse FromCapture(string name, int? status = null) =>
        Build(status ?? WireFixtures.Status(name), WireFixtures.Headers(name));

    private static DownloadResponse Build(int status, IReadOnlyDictionary<string, string> headers, long? contentLength = null)
    {
        var message = new HttpResponseMessage((HttpStatusCode)status);
        var content = new ByteArrayContent(Array.Empty<byte>());
        foreach (var header in headers)
        {
            if (header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                contentLength ??= long.Parse(header.Value, CultureInfo.InvariantCulture);
            }
            else if (ContentHeaderNames.Contains(header.Key, StringComparer.OrdinalIgnoreCase))
            {
                Assert.True(content.Headers.TryAddWithoutValidation(header.Key, header.Value), header.Key);
            }
            else
            {
                Assert.True(message.Headers.TryAddWithoutValidation(header.Key, header.Value), header.Key);
            }
        }

        return new DownloadResponse(status, "req-dl-0001", new MemoryStream(), contentLength, message.Headers, content.Headers);
    }

    private static DownloadResponse Manual(int status, long? contentLength, params string[] headers)
    {
        var dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < headers.Length; i += 2)
        {
            dictionary[headers[i]] = headers[i + 1];
        }

        return Build(status, dictionary, contentLength);
    }

    private static DownloadHeaders Read(DownloadResponse response) => DownloadHeaders.Read(response);

    // ----- catture -----

    [Fact]
    public void The_captured_full_download_has_type_name_length_and_modification_time_and_no_range()
    {
        // 108-doc-content: 200, Content-Length 590, application/pdf, Last-Modified Thu, 01 Oct 2026 09:59:15 GMT
        var headers = Read(FromCapture("108-doc-content"));

        Assert.Equal("application/pdf", headers.ContentType);
        Assert.Equal("fattura.pdf", headers.FileName);
        Assert.Equal(590L, headers.ContentLength);
        Assert.Equal(WireTest.Utc(2026, 10, 1, 9, 59, 15), headers.LastModified);
        Assert.Null(headers.Range);
    }

    [Theory]
    [InlineData("109-doc-content-range-0-9", 0L, 9L, 10L)]
    [InlineData("110-doc-content-range-suffix", 580L, 589L, 10L)]
    [InlineData("111-doc-content-range-open", 5L, 589L, 585L)]
    public void The_captured_206_responses_say_which_part_arrived_and_the_length_is_the_part_length(string name, long first, long last, long length)
    {
        var headers = Read(FromCapture(name));

        Assert.Equal(new ContentRange(first, last, 590), headers.Range);
        Assert.Equal(length, headers.ContentLength);
        Assert.Equal(last - first + 1, headers.ContentLength);
        Assert.Equal("fattura.pdf", headers.FileName);
    }

    [Fact]
    public void A_multi_range_request_is_answered_with_200_and_the_whole_content_so_there_is_no_range()
    {
        // 113: il server ignora i range multipli e risponde 200 con tutto (Content-Length 590, nessun Content-Range)
        var headers = Read(FromCapture("113-doc-content-range-multi"));

        Assert.Null(headers.Range);
        Assert.Equal(590L, headers.ContentLength);
    }

    [Fact]
    public void The_captured_non_ASCII_names_come_from_filename_star()
    {
        Assert.Equal("perch\U000000E9 \U000000E8.pdf", Read(FromCapture("115-doc-content-nonascii-name")).FileName);
        Assert.Equal("perch\U000000E9 \U000000E8 star.pdf", Read(FromCapture("116-doc-content-star-name")).FileName);
        Assert.Equal(591L, Read(FromCapture("116-doc-content-star-name")).ContentLength);
    }

    [Fact]
    public void The_captured_text_download_and_the_inline_preview_are_read()
    {
        var text = Read(FromCapture("118-doc-content-text"));
        var preview = Read(FromCapture("121-doc-preview-pdf"));
        var previewRange = Read(FromCapture("122-doc-preview-range"));

        Assert.Equal("text/plain", text.ContentType);
        Assert.Equal("note.txt", text.FileName);
        Assert.Equal(17L, text.ContentLength);
        Assert.Equal("application/pdf", preview.ContentType);
        Assert.Equal("fattura.pdf", preview.FileName);
        Assert.Equal(new ContentRange(0, 9, 590), previewRange.Range);
    }

    // ----- 206: Content-Range obbligatorio e coerente -----

    [Fact]
    public void A_206_without_a_Content_Range_is_not_interpretable_and_carries_the_206_status()
    {
        var response = Manual(206, 10, "Content-Type", "application/pdf");

        var exception = Assert.Throws<UnexpectedResponseException>(() => Read(response));

        Assert.Equal(206, exception.StatusCode);
        Assert.Equal("req-dl-0001", exception.RequestId);
        Assert.Contains("Content-Range", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("bytes */590")]
    [InlineData("bytes 0-9/*")]
    [InlineData("bytes 9-0/590")]
    [InlineData("bytes 0-590/590")]
    [InlineData("0-9/590")]
    [InlineData("garbage")]
    [InlineData("")]
    public void A_206_with_a_Content_Range_that_is_not_bytes_first_last_total_is_not_interpretable(string value)
    {
        // "bytes */590" e' la forma del 416: accettarla come intervallo darebbe un contenuto parziale senza dire quale.
        var response = Manual(206, 10, "Content-Type", "application/pdf", "Content-Range", value);

        var exception = Assert.Throws<UnexpectedResponseException>(() => Read(response));

        Assert.Equal(206, exception.StatusCode);
    }

    [Fact]
    public void The_captured_416_headers_read_as_a_206_are_refused_because_of_the_star_form()
    {
        var response = FromCapture("112-doc-content-range-unsatisfiable", status: 206);

        Assert.Throws<UnexpectedResponseException>(() => Read(response));
    }

    [Fact]
    public void A_206_whose_length_does_not_match_the_range_is_not_interpretable()
    {
        // Il trasporto conta i byte contro Content-Length: un'incoerenza passerebbe inosservata fino alla fine della lettura.
        var wrong = Manual(206, 11, "Content-Type", "application/pdf", "Content-Range", "bytes 0-9/590");
        var right = Manual(206, 10, "Content-Type", "application/pdf", "Content-Range", "bytes 0-9/590");
        var unknownLength = Manual(206, null, "Content-Type", "application/pdf", "Content-Range", "bytes 0-9/590");

        Assert.Throws<UnexpectedResponseException>(() => Read(wrong));
        Assert.Equal(new ContentRange(0, 9, 590), Read(right).Range);
        Assert.Equal(new ContentRange(0, 9, 590), Read(unknownLength).Range);
        Assert.Null(Read(unknownLength).ContentLength);
    }

    [Fact]
    public void A_200_ignores_a_Content_Range_even_an_invalid_one()
    {
        var withRange = Manual(200, 590, "Content-Type", "application/pdf", "Content-Range", "bytes 0-9/590");
        var invalid = Manual(200, 590, "Content-Type", "application/pdf", "Content-Range", "garbage");

        Assert.Null(Read(withRange).Range);
        Assert.Null(Read(invalid).Range);
    }

    // ----- tipo, nome, data -----

    [Fact]
    public void The_content_type_keeps_its_parameters_and_a_missing_one_is_the_default_octet_stream()
    {
        Assert.Equal("text/plain; charset=utf-8", Read(Manual(200, 1, "Content-Type", "text/plain; charset=utf-8")).ContentType);
        Assert.Equal("application/octet-stream", Read(Manual(200, 1)).ContentType);
        Assert.Equal(DownloadHeaders.DefaultContentType, Read(Manual(200, 1)).ContentType);
    }

    [Fact]
    public void A_missing_or_unusable_Content_Disposition_gives_a_null_name()
    {
        Assert.Null(Read(Manual(200, 1, "Content-Type", "application/pdf")).FileName);
        Assert.Null(Read(Manual(200, 1, "Content-Type", "application/pdf", "Content-Disposition", "attachment")).FileName);
    }

    [Fact]
    public void A_missing_or_invalid_Last_Modified_is_null_and_never_an_error()
    {
        Assert.Null(Read(Manual(200, 1, "Content-Type", "application/pdf")).LastModified);
        var invalid = Manual(200, 1, "Content-Type", "application/pdf");
        invalid.ContentHeaders.TryAddWithoutValidation("Last-Modified", "ieri");

        Assert.Null(Read(invalid).LastModified);
    }

    [Fact]
    public void A_missing_content_length_is_null()
    {
        Assert.Null(Read(Manual(200, null, "Content-Type", "application/pdf")).ContentLength);
    }

    [Fact]
    public void Reading_does_not_depend_on_the_current_culture()
    {
        WireTest.WithCulture("ar-SA", () =>
        {
            var headers = Read(FromCapture("110-doc-content-range-suffix"));

            Assert.Equal(new ContentRange(580, 589, 590), headers.Range);
            Assert.Equal(WireTest.Utc(2026, 10, 1, 9, 59, 15), headers.LastModified);
        });
    }

    [Fact]
    public void A_null_response_is_an_argument_error()
    {
        Assert.Throws<ArgumentNullException>(() => DownloadHeaders.Read(null!));
    }

    // ----- verso DocumentContent -----

    private sealed class DisposeSpy : IDisposable
    {
        internal int Disposed { get; private set; }

        public void Dispose() => Disposed++;
    }

    [Fact]
    public void The_headers_become_a_DocumentContent_that_owns_the_stream_and_the_resource()
    {
        var spy = new DisposeSpy();
        var stream = new MemoryStream(new byte[] { 1, 2, 3 });

        using (var content = Read(FromCapture("109-doc-content-range-0-9")).ToContent(stream, spy))
        {
            Assert.Same(stream, content.Content);
            Assert.Equal("application/pdf", content.ContentType);
            Assert.Equal("fattura.pdf", content.FileName);
            Assert.Equal(10L, content.ContentLength);
            Assert.Equal(WireTest.Utc(2026, 10, 1, 9, 59, 15), content.LastModified);
            Assert.Equal(new ContentRange(0, 9, 590), content.Range);
            Assert.True(content.IsPartial);
            Assert.Equal(0, spy.Disposed);
        }

        Assert.Equal(1, spy.Disposed);
        Assert.False(stream.CanRead);
    }

    [Fact]
    public void A_full_download_becomes_a_DocumentContent_that_is_not_partial()
    {
        using var content = Read(FromCapture("108-doc-content")).ToContent(new MemoryStream(), owner: null);

        Assert.False(content.IsPartial);
        Assert.Null(content.Range);
        Assert.Equal(590L, content.ContentLength);
    }
}
