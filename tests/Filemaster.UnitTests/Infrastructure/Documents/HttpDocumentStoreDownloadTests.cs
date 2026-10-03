using Filemaster.Application;
using Filemaster.Domain;
using Filemaster.UnitTests.Wire;

namespace Filemaster.UnitTests.Infrastructure.Documents;

/// <summary>
/// Contenuto e anteprima in streaming: percorso, intestazione <c>Range</c> nelle tre forme (le stesse delle catture 109-111), intestazioni
/// di risposta catturate lette nel <see cref="DocumentContent"/>, status attesi (senza intervallo solo 200; con intervallo 200 o 206),
/// 416 oltre la fine, smaltimento della risposta quando le intestazioni non si leggono, troncamento rilevato end-to-end, ritentativo solo
/// prima di consegnare lo stream.
/// </summary>
public sealed class HttpDocumentStoreDownloadTests
{
    [Fact]
    public async Task The_whole_content_is_a_GET_without_Range_and_the_captured_headers_end_up_in_the_DocumentContent()
    {
        using var rig = new StoreRig();
        var body = StreamReading.Pattern(590);
        rig.Then(() => FixtureReply.Download("108-doc-content", body));

        using var content = await rig.Store.OpenContentAsync(StoreRig.Id);

        var sent = rig.Single;
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal(WireFixtures.RequestPath("108-doc-content"), sent.PathAndQuery);
        Assert.Null(sent.Header("Range"));
        Assert.Null(sent.Body);
        Assert.Equal("application/pdf", content.ContentType);
        Assert.Equal("fattura.pdf", content.FileName);
        Assert.Equal(590, content.ContentLength);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 9, 59, 15, TimeSpan.Zero), content.LastModified);
        Assert.False(content.IsPartial);
        Assert.Null(content.Range);
        Assert.Equal(body, await StreamReading.ReadToEndAsync(content.Content, ReadApi.Async, 100));
    }

    [Theory]
    [InlineData("109-doc-content-range-0-9", 0L, 9L)]
    [InlineData("110-doc-content-range-suffix", 580L, 589L)]
    [InlineData("111-doc-content-range-open", 5L, 589L)]
    public async Task A_range_sends_the_Range_header_of_the_capture_and_a_206_is_a_partial_content(string fixture, long first, long last)
    {
        using var rig = new StoreRig();
        rig.Then(() => FixtureReply.Download(fixture));
        var range = fixture.Contains("suffix") ? ByteRange.Suffix(10) : fixture.Contains("open") ? ByteRange.From(5) : ByteRange.Between(0, 9);

        using var content = await rig.Store.OpenContentAsync(StoreRig.Id, range);

        var expectedRange = WireFixtures.CapturedText(fixture, "request").Split('\n').Single(l => l.StartsWith("# curl: Range: ", StringComparison.Ordinal)).Substring("# curl: Range: ".Length);
        Assert.Equal(expectedRange, rig.Single.Header("Range"));
        Assert.True(content.IsPartial);
        Assert.Equal(new ContentRange(first, last, 590), content.Range);
        Assert.Equal(last - first + 1, content.ContentLength);
    }

    [Fact]
    public async Task A_range_answered_200_with_the_whole_content_is_not_partial()
    {
        using var rig = new StoreRig();
        rig.Then(() => FixtureReply.Download("113-doc-content-range-multi"));

        using var content = await rig.Store.OpenContentAsync(StoreRig.Id, ByteRange.Between(0, 1));

        Assert.False(content.IsPartial);
        Assert.Equal(590, content.ContentLength);
    }

    [Fact]
    public async Task A_206_that_was_not_asked_for_is_UnexpectedResponseException_and_the_response_is_disposed()
    {
        using var rig = new StoreRig();
        var response = FixtureReply.Download("109-doc-content-range-0-9");
        rig.Then(() => response);

        var exception = await Assert.ThrowsAsync<UnexpectedResponseException>(() => rig.Store.OpenContentAsync(StoreRig.Id));

        Assert.Equal(206, exception.StatusCode);
        Assert.Equal(1, response.ContentOf().DisposeCount);
        Assert.Single(rig.Sent);
    }

    [Fact]
    public async Task A_range_past_the_end_is_416_UnexpectedResponseException_not_retried_and_the_response_is_disposed()
    {
        using var rig = new StoreRig();
        var response = FixtureReply.Download("112-doc-content-range-unsatisfiable", Array.Empty<byte>());
        rig.Then(() => response);

        var exception = await Assert.ThrowsAsync<UnexpectedResponseException>(() => rig.Store.OpenContentAsync(StoreRig.Id, ByteRange.From(999999)));

        Assert.Equal(416, exception.StatusCode);
        Assert.Equal("bytes=999999-", rig.Single.Header("Range"));
        Assert.Equal(1, response.ContentOf().DisposeCount);
    }

    [Fact]
    public async Task A_206_with_an_unreadable_Content_Range_is_UnexpectedResponseException_and_the_response_is_disposed_once()
    {
        using var rig = new StoreRig();
        var response = FixtureReply.Download("109-doc-content-range-0-9");
        response.Content.Headers.Remove("Content-Range");
        response.Content.Headers.TryAddWithoutValidation("Content-Range", "bytes */590");
        rig.Then(() => response);

        var exception = await Assert.ThrowsAsync<UnexpectedResponseException>(() => rig.Store.OpenContentAsync(StoreRig.Id, ByteRange.Between(0, 9)));

        Assert.Equal(206, exception.StatusCode);
        Assert.Equal(1, response.ContentOf().DisposeCount);
    }

    [Fact]
    public async Task A_206_whose_length_does_not_match_the_range_is_UnexpectedResponseException_and_the_response_is_disposed()
    {
        using var rig = new StoreRig();
        var response = FixtureReply.Download("109-doc-content-range-0-9");
        response.Content.Headers.Remove("Content-Range");
        response.Content.Headers.TryAddWithoutValidation("Content-Range", "bytes 0-19/590");
        rig.Then(() => response);

        await Assert.ThrowsAsync<UnexpectedResponseException>(() => rig.Store.OpenContentAsync(StoreRig.Id, ByteRange.Between(0, 19)));

        Assert.Equal(1, response.ContentOf().DisposeCount);
    }

    [Fact]
    public async Task Disposing_the_DocumentContent_disposes_the_HTTP_response_and_not_before()
    {
        using var rig = new StoreRig();
        var response = FixtureReply.Download("108-doc-content");
        rig.Then(() => response);

        var content = await rig.Store.OpenContentAsync(StoreRig.Id);
        Assert.Equal(0, response.ContentOf().DisposeCount);
        content.Dispose();

        Assert.Equal(1, response.ContentOf().DisposeCount);
    }

    [Fact]
    public async Task A_download_cut_short_of_Content_Length_is_ContentIntegrityException_truncated_when_read_end_to_end()
    {
        using var rig = new StoreRig();
        rig.Then(() => FixtureReply.Download("108-doc-content", StreamReading.Pattern(100)));

        using var content = await rig.Store.OpenContentAsync(StoreRig.Id);

        Assert.Equal(590, content.ContentLength);
        var exception = await Assert.ThrowsAsync<ContentIntegrityException>(() => StreamReading.ReadToEndAsync(content.Content, ReadApi.Async, 64));
        Assert.True(exception.IsTruncated);
        Assert.Equal(200, exception.StatusCode);
    }

    [Fact]
    public async Task A_transient_503_before_the_headers_is_retried_and_the_Range_is_sent_again()
    {
        using var rig = new StoreRig();
        rig.Then(() => Reply.Text(503));
        rig.Then(() => FixtureReply.Download("109-doc-content-range-0-9"));

        using var content = await rig.Store.OpenContentAsync(StoreRig.Id, ByteRange.Between(0, 9));

        Assert.Equal(2, rig.Sent.Count);
        Assert.All(rig.Sent, s => Assert.Equal("bytes=0-9", s.Header("Range")));
        Assert.True(content.IsPartial);
    }

    [Theory]
    [InlineData(404, "not-found", typeof(NotFoundException))]
    [InlineData(409, "content-unavailable", typeof(ContentUnavailableException))]
    [InlineData(401, "unauthorized", typeof(UnauthorizedException))]
    [InlineData(500, "internal-error", typeof(ServerErrorException))]
    public async Task The_errors_of_the_content_are_mapped(int status, string slug, Type expected)
    {
        using var rig = new StoreRig();
        rig.Then(() => Reply.Problem(status, slug));

        var exception = await Assert.ThrowsAnyAsync<FilemasterException>(() => rig.Store.OpenContentAsync(StoreRig.Id));

        Assert.IsType(expected, exception);
    }

    // ----- anteprima -----

    [Fact]
    public async Task The_preview_is_a_GET_of_the_preview_path_without_Range_and_reads_capture_121()
    {
        using var rig = new StoreRig();
        rig.Then(() => FixtureReply.Download("121-doc-preview-pdf"));

        using var content = await rig.Store.OpenPreviewAsync(StoreRig.Id);

        var sent = rig.Single;
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal(WireFixtures.RequestPath("121-doc-preview-pdf"), sent.PathAndQuery);
        Assert.Null(sent.Header("Range"));
        Assert.Equal("application/pdf", content.ContentType);
        Assert.Equal("fattura.pdf", content.FileName);
        Assert.False(content.IsPartial);
    }

    [Fact]
    public async Task A_206_on_the_preview_is_UnexpectedResponseException_because_no_range_was_asked()
    {
        using var rig = new StoreRig();
        var response = FixtureReply.Download("122-doc-preview-range");
        rig.Then(() => response);

        var exception = await Assert.ThrowsAsync<UnexpectedResponseException>(() => rig.Store.OpenPreviewAsync(StoreRig.Id));

        Assert.Equal(206, exception.StatusCode);
        Assert.Equal(1, response.ContentOf().DisposeCount);
    }

    [Theory]
    [InlineData(415, "unsupported-media-type", typeof(UnsupportedMediaTypeException))]
    [InlineData(409, "content-unavailable", typeof(ContentUnavailableException))]
    [InlineData(404, "not-found", typeof(NotFoundException))]
    public async Task The_errors_of_the_preview_are_mapped(int status, string slug, Type expected)
    {
        using var rig = new StoreRig();
        rig.Then(() => Reply.Problem(status, slug));

        var exception = await Assert.ThrowsAnyAsync<FilemasterException>(() => rig.Store.OpenPreviewAsync(StoreRig.Id));

        Assert.IsType(expected, exception);
    }

    [Theory]
    [InlineData(204, true)]
    [InlineData(204, false)]
    [InlineData(201, false)]
    [InlineData(203, true)]
    public async Task Other_2xx_statuses_of_a_download_are_UnexpectedResponseException(int status, bool withRange)
    {
        using var rig = new StoreRig();
        var response = Reply.Empty(status);
        rig.Then(() => response);

        var exception = await Assert.ThrowsAsync<UnexpectedResponseException>(
            () => rig.Store.OpenContentAsync(StoreRig.Id, withRange ? ByteRange.From(0) : null));

        Assert.Equal(status, exception.StatusCode);
        Assert.Equal(1, response.ContentOf().DisposeCount);
    }
}
