using Filemaster.Domain;

namespace Filemaster.UnitTests.Domain;

/// <summary>Gerarchia e proprieta' delle eccezioni del Domain. La mappatura wire -> eccezione e' di Infrastructure.</summary>
public sealed class ExceptionTests
{
    private const string CustomMessage = "messaggio di prova";
    private const int CustomStatus = 499;

    /// <summary>
    /// Eccezioni costruite da una risposta HTTP con il costruttore uniforme
    /// (message, statusCode, problemType, requestId, detail, innerException): stato canonico di default.
    /// </summary>
    private sealed record HttpCase(
        Type Type,
        int DefaultStatus,
        Func<FilemasterException> CreateDefault,
        Func<Exception, FilemasterException> CreateFull);

    private static readonly HttpCase[] HttpCases =
    {
        new(typeof(NotFoundException), 404, () => new NotFoundException(),
            inner => new NotFoundException(CustomMessage, CustomStatus, "slug-di-prova", "req-1", "dettaglio", inner)),
        new(typeof(ConflictException), 409, () => new ConflictException(),
            inner => new ConflictException(CustomMessage, CustomStatus, "slug-di-prova", "req-1", "dettaglio", inner)),
        new(typeof(ContentUnavailableException), 409, () => new ContentUnavailableException(),
            inner => new ContentUnavailableException(CustomMessage, CustomStatus, "slug-di-prova", "req-1", "dettaglio", inner)),
        new(typeof(InvalidRequestException), 400, () => new InvalidRequestException(),
            inner => new InvalidRequestException(CustomMessage, CustomStatus, "slug-di-prova", "req-1", "dettaglio", inner)),
        new(typeof(UnauthorizedException), 401, () => new UnauthorizedException(),
            inner => new UnauthorizedException(CustomMessage, CustomStatus, "slug-di-prova", "req-1", "dettaglio", inner)),
        new(typeof(ForbiddenException), 403, () => new ForbiddenException(),
            inner => new ForbiddenException(CustomMessage, CustomStatus, "slug-di-prova", "req-1", "dettaglio", inner)),
        new(typeof(RequestTooLargeException), 413, () => new RequestTooLargeException(),
            inner => new RequestTooLargeException(CustomMessage, CustomStatus, "slug-di-prova", "req-1", "dettaglio", inner)),
        new(typeof(UnsupportedMediaTypeException), 415, () => new UnsupportedMediaTypeException(),
            inner => new UnsupportedMediaTypeException(CustomMessage, CustomStatus, "slug-di-prova", "req-1", "dettaglio", inner)),
        new(typeof(StorageNotConfiguredException), 503, () => new StorageNotConfiguredException(),
            inner => new StorageNotConfiguredException(CustomMessage, CustomStatus, "slug-di-prova", "req-1", "dettaglio", inner)),
        new(typeof(ServerErrorException), 500, () => new ServerErrorException(),
            inner => new ServerErrorException(CustomMessage, CustomStatus, "slug-di-prova", "req-1", "dettaglio", inner)),
    };

    [Fact]
    public void FilemasterException_is_a_public_abstract_root_deriving_from_Exception()
    {
        var root = typeof(FilemasterException);

        Assert.True(root.IsPublic);
        Assert.True(root.IsAbstract);
        Assert.False(root.IsSealed);
        Assert.Equal(typeof(Exception), root.BaseType);
    }

    [Fact]
    public void Domain_exception_types_are_the_documented_sealed_leaves_of_FilemasterException()
    {
        var root = typeof(FilemasterException);
        var leaves = root.Assembly.GetExportedTypes()
            .Where(t => t != root && typeof(Exception).IsAssignableFrom(t))
            .ToArray();

        Assert.All(leaves, t =>
        {
            Assert.True(root.IsAssignableFrom(t), $"{t.Name} deve derivare da FilemasterException");
            Assert.True(t.IsSealed, $"{t.Name} deve essere sealed");
        });
        Assert.Equal(
            new[]
            {
                nameof(ConflictException),
                nameof(ConnectionException),
                nameof(ContentIntegrityException),
                nameof(ContentUnavailableException),
                nameof(FilemasterTimeoutException),
                nameof(ForbiddenException),
                nameof(InvalidRequestException),
                nameof(NotFoundException),
                nameof(RequestTooLargeException),
                nameof(ServerErrorException),
                nameof(StorageNotConfiguredException),
                nameof(UnauthorizedException),
                nameof(UnexpectedResponseException),
                nameof(UnsupportedMediaTypeException),
            },
            leaves.Select(t => t.Name).OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void Http_exception_created_without_arguments_has_canonical_status_and_a_default_message()
    {
        foreach (var testCase in HttpCases)
        {
            var exception = testCase.CreateDefault();
            var name = testCase.Type.Name;

            Assert.Equal(testCase.Type, exception.GetType());
            Assert.IsAssignableFrom<FilemasterException>(exception);
            Assert.Equal(testCase.DefaultStatus, exception.StatusCode);
            Assert.False(string.IsNullOrWhiteSpace(exception.Message), $"{name}: messaggio di default vuoto");
            Assert.DoesNotContain("Exception of type", exception.Message, StringComparison.Ordinal); // quello del framework
            Assert.Null(exception.ProblemType);
            Assert.Null(exception.RequestId);
            Assert.Null(exception.Detail);
            Assert.Null(exception.InnerException);
        }

        // Ogni tipo ha il suo messaggio: due errori diversi non si somigliano nei log.
        Assert.Equal(
            HttpCases.Length,
            HttpCases.Select(testCase => testCase.CreateDefault().Message).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Http_exception_preserves_message_status_problem_type_request_id_detail_and_inner_exception()
    {
        foreach (var testCase in HttpCases)
        {
            var inner = new InvalidOperationException("causa");

            var exception = testCase.CreateFull(inner);

            Assert.Equal(testCase.Type, exception.GetType());
            Assert.Equal(CustomMessage, exception.Message);
            Assert.Equal(CustomStatus, exception.StatusCode);
            Assert.Equal("slug-di-prova", exception.ProblemType);
            Assert.Equal("req-1", exception.RequestId);
            Assert.Equal("dettaglio", exception.Detail);
            Assert.Same(inner, exception.InnerException);
        }
    }

    [Fact]
    public void Http_exception_with_a_null_message_falls_back_to_the_default_message()
    {
        Assert.Equal(new NotFoundException().Message, new NotFoundException(null, 404, "not-found").Message);
        Assert.Equal(new ServerErrorException().Message, new ServerErrorException(null, 502).Message);
        Assert.Equal(502, new ServerErrorException(null, 502).StatusCode);
    }

    [Fact]
    public void ConnectionException_has_no_http_status_and_keeps_inner_exception_and_request_id()
    {
        var inner = new InvalidOperationException("rete");

        var exception = new ConnectionException("connessione persa", inner, "req-2");

        Assert.Equal(0, exception.StatusCode);
        Assert.Equal("connessione persa", exception.Message);
        Assert.Same(inner, exception.InnerException);
        Assert.Equal("req-2", exception.RequestId);
        Assert.Null(exception.ProblemType);
        Assert.Null(exception.Detail);
        Assert.IsAssignableFrom<FilemasterException>(exception);
    }

    [Fact]
    public void ConnectionException_created_without_arguments_has_a_default_message()
    {
        var exception = new ConnectionException();

        Assert.Equal(0, exception.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(exception.Message));
        Assert.Null(exception.InnerException);
        Assert.Null(exception.RequestId);
    }

    [Fact]
    public void FilemasterTimeoutException_has_no_http_status_and_keeps_inner_exception_and_request_id()
    {
        var inner = new TaskCanceledException("scaduto");

        var exception = new FilemasterTimeoutException("troppo lento", inner, "req-3");

        Assert.Equal(0, exception.StatusCode);
        Assert.Equal("troppo lento", exception.Message);
        Assert.Same(inner, exception.InnerException);
        Assert.Equal("req-3", exception.RequestId);
        Assert.IsAssignableFrom<FilemasterException>(exception);
        Assert.False(string.IsNullOrWhiteSpace(new FilemasterTimeoutException().Message));
        Assert.NotEqual(new ConnectionException().Message, new FilemasterTimeoutException().Message);
    }

    [Fact]
    public void UnexpectedResponseException_requires_the_status_and_keeps_every_property()
    {
        var inner = new FormatException("json");

        var full = new UnexpectedResponseException(CustomMessage, 200, "slug-di-prova", "req-4", "dettaglio", inner);
        var minimal = new UnexpectedResponseException(null, 416);

        Assert.Equal(CustomMessage, full.Message);
        Assert.Equal(200, full.StatusCode);
        Assert.Equal("slug-di-prova", full.ProblemType);
        Assert.Equal("req-4", full.RequestId);
        Assert.Equal("dettaglio", full.Detail);
        Assert.Same(inner, full.InnerException);
        Assert.IsAssignableFrom<FilemasterException>(full);

        Assert.Equal(416, minimal.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(minimal.Message));
        Assert.Null(minimal.ProblemType);
        Assert.Null(minimal.RequestId);
        Assert.Null(minimal.Detail);
        Assert.Null(minimal.InnerException);
    }

    [Fact]
    public void ContentIntegrityException_for_a_truncated_download_exposes_lengths_and_the_response_status()
    {
        var exception = new ContentIntegrityException(true, 200, expectedLength: 590, actualLength: 100, requestId: "req-5");

        Assert.True(exception.IsTruncated);
        Assert.Equal(590L, exception.ExpectedLength);
        Assert.Equal(100L, exception.ActualLength);
        Assert.Equal(200, exception.StatusCode);
        Assert.Equal("req-5", exception.RequestId);
        Assert.Null(exception.ProblemType);
        Assert.Null(exception.Detail);
        Assert.IsAssignableFrom<FilemasterException>(exception);
        Assert.Contains("590", exception.Message, StringComparison.Ordinal);
        Assert.Contains("100", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ContentIntegrityException_for_a_hash_mismatch_is_not_truncated_and_may_omit_the_lengths()
    {
        var exception = new ContentIntegrityException(false, 206);

        Assert.False(exception.IsTruncated);
        Assert.Null(exception.ExpectedLength);
        Assert.Null(exception.ActualLength);
        Assert.Equal(206, exception.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(exception.Message));
        Assert.NotEqual(
            new ContentIntegrityException(true, 206).Message,
            exception.Message);
    }

    [Fact]
    public void ContentIntegrityException_preserves_a_custom_message_and_inner_exception()
    {
        var inner = new IOException("socket chiuso");

        var exception = new ContentIntegrityException(true, 200, CustomMessage, 10, 5, "req-6", inner);

        Assert.Equal(CustomMessage, exception.Message);
        Assert.Same(inner, exception.InnerException);
        Assert.Equal(10L, exception.ExpectedLength);
        Assert.Equal(5L, exception.ActualLength);
    }
}
