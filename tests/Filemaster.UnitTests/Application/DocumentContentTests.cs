using Filemaster.Application;

namespace Filemaster.UnitTests.Application;

/// <summary>
/// <see cref="DocumentContent"/>: lo stream del contenuto piu' i metadati letti dagli header, e la regola di
/// <c>Dispose</c>: rilascia lo stream e poi cio' che lo possiede (di solito la risposta HTTP), una volta sola.
/// </summary>
public sealed class DocumentContentTests
{
    private static readonly DateTimeOffset Modified = new(2026, 10, 1, 9, 59, 15, TimeSpan.Zero);

    [Fact]
    public void Constructor_with_null_content_throws_ArgumentNullException_for_content()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => new DocumentContent(null!, "application/pdf"));

        Assert.Equal("content", exception.ParamName);
    }

    [Fact]
    public void Constructor_with_null_contentType_throws_ArgumentNullException_for_contentType()
    {
        using var stream = new MemoryStream();

        var exception = Assert.Throws<ArgumentNullException>(() => new DocumentContent(stream, null!));

        Assert.Equal("contentType", exception.ParamName);
    }

    [Fact]
    public void Constructor_with_only_the_required_arguments_describes_a_whole_content_without_other_headers()
    {
        using var stream = new MemoryStream();

        using var content = new DocumentContent(stream, "application/pdf");

        Assert.Same(stream, content.Content);
        Assert.Equal("application/pdf", content.ContentType);
        Assert.Null(content.FileName);
        Assert.Null(content.ContentLength);
        Assert.Null(content.LastModified);
        Assert.Null(content.Range);
        Assert.False(content.IsPartial);
    }

    [Fact]
    public void Constructor_keeps_every_header_value()
    {
        using var stream = new MemoryStream();
        var range = new ContentRange(0, 9, 590);

        using var content = new DocumentContent(stream, "application/pdf", "fattura.pdf", 10, Modified, range);

        Assert.Equal("fattura.pdf", content.FileName);
        Assert.Equal(10L, content.ContentLength);
        Assert.Equal(Modified, content.LastModified);
        Assert.Equal(range, content.Range);
    }

    [Fact]
    public void IsPartial_is_true_exactly_when_a_range_is_present()
    {
        using var stream = new MemoryStream();

        using var whole = new DocumentContent(stream, "application/pdf");
        using var partial = new DocumentContent(stream, "application/pdf", range: new ContentRange(580, 589, 590));

        Assert.False(whole.IsPartial);
        Assert.True(partial.IsPartial);
    }

    [Fact]
    public void Dispose_disposes_the_stream_and_then_the_owner()
    {
        var order = new List<string>();
        var stream = new TrackedStream(order);
        var owner = new TrackedDisposable(order);
        var content = new DocumentContent(stream, "application/pdf", owner: owner);

        content.Dispose();

        Assert.Equal(new[] { "stream", "owner" }, order);
    }

    [Fact]
    public void Dispose_twice_releases_the_stream_and_the_owner_only_once_and_does_not_throw()
    {
        var order = new List<string>();
        var content = new DocumentContent(new TrackedStream(order), "application/pdf", owner: new TrackedDisposable(order));

        content.Dispose();
        content.Dispose();
        content.Dispose();

        Assert.Equal(new[] { "stream", "owner" }, order);
    }

    [Fact]
    public void Dispose_without_an_owner_disposes_just_the_stream()
    {
        var order = new List<string>();
        var content = new DocumentContent(new TrackedStream(order), "application/pdf");

        content.Dispose();

        Assert.Equal(new[] { "stream" }, order);
    }

    [Fact]
    public void Dispose_disposes_the_owner_even_if_disposing_the_stream_throws()
    {
        // La risposta HTTP che possiede lo stream va rilasciata comunque: altrimenti la connessione resta occupata.
        var order = new List<string>();
        var owner = new TrackedDisposable(order);
        var content = new DocumentContent(new TrackedStream(order, throwOnDispose: true), "application/pdf", owner: owner);

        Assert.Throws<InvalidOperationException>(content.Dispose);

        Assert.Equal(new[] { "stream", "owner" }, order);
        content.Dispose(); // il secondo Dispose non riprova e non rilancia
        Assert.Equal(new[] { "stream", "owner" }, order);
    }

    [Fact]
    public void Content_stays_readable_as_a_property_after_Dispose_but_the_stream_is_closed()
    {
        var stream = new MemoryStream(new byte[] { 1, 2, 3 });
        var content = new DocumentContent(stream, "application/pdf");

        content.Dispose();

        Assert.Same(stream, content.Content);
        Assert.False(stream.CanRead);
    }

    private sealed class TrackedStream : MemoryStream
    {
        private readonly List<string> _log;
        private readonly bool _throwOnDispose;

        public TrackedStream(List<string> log, bool throwOnDispose = false)
        {
            _log = log;
            _throwOnDispose = throwOnDispose;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _log.Add("stream");
                if (_throwOnDispose)
                {
                    throw new InvalidOperationException("Dispose dello stream fallito");
                }
            }

            base.Dispose(disposing);
        }
    }

    private sealed class TrackedDisposable : IDisposable
    {
        private readonly List<string> _log;

        public TrackedDisposable(List<string> log) => _log = log;

        public void Dispose() => _log.Add("owner");
    }
}
