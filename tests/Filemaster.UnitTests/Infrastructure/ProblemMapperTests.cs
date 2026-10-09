using System.Text;
using Filemaster.Domain;
using Filemaster.Infrastructure;

namespace Filemaster.UnitTests.Infrastructure;

/// <summary>
/// La mappatura problem+json -> eccezioni, con la regola del doc di <see cref="FilemasterException"/>: prima lo slug, poi lo status;
/// per il 415 vale sempre lo status. Una tabella per ogni slug che il server <c>dev</c> emette davvero (<c>validation-error</c>,
/// <c>unauthorized</c>, <c>forbidden</c>, <c>not-found</c>, <c>conflict</c>, <c>content-unavailable</c>, <c>request-too-large</c>,
/// <c>unsupported-media-type</c>, <c>storage-not-configured</c>, <c>internal-error</c>, <c>method-not-allowed</c>, <c>error</c>), per
/// ogni status senza slug, per i corpi illeggibili e per i tre campi (<c>request_id</c>, <c>detail</c>, <c>type</c>). I corpi sono JSON
/// scritto a mano con valori inventati.
/// </summary>
public sealed class ProblemMapperTests
{
    private const string Backslash = "\\";

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    // Un problem+json come lo scrive il server: type, title, status, detail (se c'e'), request_id (se c'e').
    private static byte[] Problem(string? slug, int status = 400, string? detail = null, string? requestId = "req-0001")
    {
        var builder = new StringBuilder("{");
        if (slug is not null)
        {
            builder.Append("\"type\":\"/problems/").Append(slug).Append("\",");
        }

        builder.Append("\"title\":\"titolo\",\"status\":").Append(status);
        if (detail is not null)
        {
            builder.Append(",\"detail\":\"").Append(detail).Append('"');
        }

        if (requestId is not null)
        {
            builder.Append(",\"request_id\":\"").Append(requestId).Append('"');
        }

        return Utf8(builder.Append('}').ToString());
    }

    private static T Mapped<T>(int status, byte[]? body, string? header = null)
        where T : FilemasterException
    {
        var exception = ProblemMapper.Map(status, body, header);
        Assert.Equal(status, exception.StatusCode);
        return Assert.IsType<T>(exception);
    }

    // ----- slug noti con il loro status -----

    public static TheoryData<int, string, Type> KnownSlugs() => new()
    {
        { 400, "validation-error", typeof(InvalidRequestException) },
        { 401, "unauthorized", typeof(UnauthorizedException) },
        { 403, "forbidden", typeof(ForbiddenException) },
        { 404, "not-found", typeof(NotFoundException) },
        { 409, "conflict", typeof(ConflictException) },
        { 409, "content-unavailable", typeof(ContentUnavailableException) },
        { 413, "request-too-large", typeof(RequestTooLargeException) },
        { 415, "unsupported-media-type", typeof(UnsupportedMediaTypeException) },
        { 503, "storage-not-configured", typeof(StorageNotConfiguredException) },
        { 500, "internal-error", typeof(ServerErrorException) },
        { 405, "method-not-allowed", typeof(UnexpectedResponseException) },
    };

    [Theory]
    [MemberData(nameof(KnownSlugs))]
    public void A_known_slug_with_its_usual_status_maps_to_its_exception(int status, string slug, Type expected)
    {
        var exception = ProblemMapper.Map(status, Problem(slug, status, detail: "motivo", requestId: "req-42"), headerRequestId: null);

        Assert.IsType(expected, exception);
        Assert.Equal(status, exception.StatusCode);
        Assert.Equal(slug, exception.ProblemType);
        Assert.Equal("motivo", exception.Detail);
        Assert.Equal("req-42", exception.RequestId);
    }

    public static TheoryData<int, string, Type> KnownSlugsWithAnotherStatus() => new()
    {
        // Con uno slug noto vince lo slug: l'eccezione porta lo status vero.
        { 500, "not-found", typeof(NotFoundException) },
        { 404, "conflict", typeof(ConflictException) },
        { 404, "content-unavailable", typeof(ContentUnavailableException) },
        { 200 + 200, "internal-error", typeof(ServerErrorException) },
        { 408, "validation-error", typeof(InvalidRequestException) },
        { 431, "validation-error", typeof(InvalidRequestException) },
        { 400, "unauthorized", typeof(UnauthorizedException) },
        { 500, "forbidden", typeof(ForbiddenException) },
        { 400, "request-too-large", typeof(RequestTooLargeException) },
        { 500, "storage-not-configured", typeof(StorageNotConfiguredException) },
        { 404, "unsupported-media-type", typeof(UnsupportedMediaTypeException) },
        { 404, "method-not-allowed", typeof(UnexpectedResponseException) },
        { 599, "unauthorized", typeof(UnauthorizedException) },
    };

    [Theory]
    [MemberData(nameof(KnownSlugsWithAnotherStatus))]
    public void A_known_slug_with_an_unexpected_status_still_maps_by_slug_and_keeps_the_real_status(int status, string slug, Type expected)
    {
        var exception = ProblemMapper.Map(status, Problem(slug, status), headerRequestId: null);

        Assert.IsType(expected, exception);
        Assert.Equal(status, exception.StatusCode);
        Assert.Equal(slug, exception.ProblemType);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(200)]
    [InlineData(204)]
    [InlineData(301)]
    [InlineData(304)]
    [InlineData(399)]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_status_below_400_is_always_unexpected_even_with_a_known_slug(int status)
    {
        foreach (var slug in new[] { "not-found", "internal-error", "validation-error", null })
        {
            var exception = Mapped<UnexpectedResponseException>(status, Problem(slug, status));

            Assert.Equal(slug, exception.ProblemType);
        }

        Mapped<UnexpectedResponseException>(status, body: null);
    }

    // ----- il 415 vale sempre lo status -----

    [Theory]
    [InlineData(null)]
    [InlineData("unsupported-media-type")]
    [InlineData("error")]
    [InlineData("validation-error")]
    [InlineData("not-found")]
    [InlineData("internal-error")]
    [InlineData("method-not-allowed")]
    [InlineData("a-slug-nobody-knows")]
    public void Status_415_is_always_UnsupportedMediaType_whatever_the_slug(string? slug)
    {
        var exception = Mapped<UnsupportedMediaTypeException>(415, Problem(slug, 415));

        Assert.Equal(slug, exception.ProblemType);
    }

    [Fact]
    public void Status_415_without_a_body_or_with_garbage_is_UnsupportedMediaType()
    {
        Mapped<UnsupportedMediaTypeException>(415, body: null);
        Mapped<UnsupportedMediaTypeException>(415, Utf8("not json"));
    }

    // ----- lo slug generico "error" -----

    [Theory]
    [InlineData(405)]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(413)]
    [InlineData(416)]
    [InlineData(429)]
    public void The_generic_slug_error_with_a_status_other_than_415_or_5xx_is_unexpected(int status)
    {
        var exception = Mapped<UnexpectedResponseException>(status, Problem("error", status));

        Assert.Equal("error", exception.ProblemType);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    [InlineData(599)]
    public void The_generic_slug_error_with_a_5xx_status_is_a_server_error(int status)
    {
        // Il doc di FilemasterException dice "ogni altro 5xx" e "slug error con altri status": per error + 5xx vale il primo.
        var exception = Mapped<ServerErrorException>(status, Problem("error", status));

        Assert.Equal("error", exception.ProblemType);
    }

    // ----- slug sconosciuto o assente: decide lo status -----

    public static TheoryData<int, Type> StatusFallbackTable() => new()
    {
        { 400, typeof(InvalidRequestException) },
        { 401, typeof(UnauthorizedException) },
        { 403, typeof(ForbiddenException) },
        { 404, typeof(NotFoundException) },
        { 409, typeof(ConflictException) },
        { 413, typeof(RequestTooLargeException) },
        { 415, typeof(UnsupportedMediaTypeException) },
        { 500, typeof(ServerErrorException) },
        { 501, typeof(ServerErrorException) },
        { 502, typeof(ServerErrorException) },
        { 503, typeof(ServerErrorException) },
        { 504, typeof(ServerErrorException) },
        { 599, typeof(ServerErrorException) },
        { 405, typeof(UnexpectedResponseException) },
        { 416, typeof(UnexpectedResponseException) },
        { 402, typeof(UnexpectedResponseException) },
        { 408, typeof(UnexpectedResponseException) },
        { 410, typeof(UnexpectedResponseException) },
        { 422, typeof(UnexpectedResponseException) },
        { 429, typeof(UnexpectedResponseException) },
        { 499, typeof(UnexpectedResponseException) },
        { 600, typeof(UnexpectedResponseException) },
    };

    [Theory]
    [MemberData(nameof(StatusFallbackTable))]
    public void Without_a_body_the_status_decides(int status, Type expected)
    {
        var exception = ProblemMapper.Map(status, body: null, headerRequestId: null);

        Assert.IsType(expected, exception);
        Assert.Equal(status, exception.StatusCode);
        Assert.Null(exception.ProblemType);
        Assert.Null(exception.Detail);
        Assert.Null(exception.RequestId);
    }

    [Theory]
    [MemberData(nameof(StatusFallbackTable))]
    public void An_unknown_slug_is_kept_as_ProblemType_and_the_status_decides(int status, Type expected)
    {
        var exception = ProblemMapper.Map(status, Problem("a-slug-from-the-future", status), headerRequestId: null);

        Assert.IsType(expected, exception);
        Assert.Equal(status, exception.StatusCode);
        Assert.Equal("a-slug-from-the-future", exception.ProblemType);
    }

    [Fact]
    public void A_503_that_is_not_storage_not_configured_is_a_server_error()
    {
        Mapped<ServerErrorException>(503, body: null);
        Mapped<ServerErrorException>(503, Problem("internal-error", 503));
        Mapped<ServerErrorException>(503, Problem("overloaded", 503));
        Mapped<ServerErrorException>(503, Utf8("<html><body>Service Unavailable</body></html>"));
        Mapped<StorageNotConfiguredException>(503, Problem("storage-not-configured", 503));
    }

    [Fact]
    public void The_readyz_503_body_is_not_a_problem_and_maps_by_status_without_borrowing_its_error_text()
    {
        // {"status":"unavailable","error":"..."}: non ha "type": l'unico campo che il mapper guarderebbe e' "detail", che qui non c'e'.
        var exception = Mapped<ServerErrorException>(503, Utf8("{\"status\":\"unavailable\",\"error\":\"database non raggiungibile\"}"));

        Assert.Null(exception.ProblemType);
        Assert.Null(exception.Detail);
    }

    [Fact]
    public void A_416_without_a_body_is_an_unexpected_response_with_status_416()
    {
        var exception = Mapped<UnexpectedResponseException>(416, body: null);

        Assert.Equal(416, exception.StatusCode);
        Assert.Contains("416", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_405_with_the_server_body_is_unexpected_and_a_404_for_an_unknown_route_is_not_found()
    {
        var unexpected = Mapped<UnexpectedResponseException>(405, Problem("method-not-allowed", 405));
        var notFound = Mapped<NotFoundException>(404, Problem("not-found", 404, detail: "risorsa inesistente"));

        Assert.Equal("method-not-allowed", unexpected.ProblemType);
        Assert.Equal("risorsa inesistente", notFound.Detail);
    }

    [Fact]
    public void A_409_without_a_slug_is_a_conflict_because_content_unavailable_is_only_told_by_its_slug()
    {
        Mapped<ConflictException>(409, body: null);
        Mapped<ConflictException>(409, Problem("a-new-conflict-kind", 409));
        Mapped<ContentUnavailableException>(409, Problem("content-unavailable", 409));
    }

    // ----- corpi illeggibili: mai un'eccezione di System.Text.Json, si ripiega sullo status -----

    public static TheoryData<string, byte[]> UnreadableBodies()
    {
        var hugeValid = Utf8("{\"type\":\"/problems/not-found\",\"detail\":\"" + new string('x', ProblemBody.MaxBytes) + "\"}");
        var deepObject = Utf8(string.Concat(Enumerable.Repeat("{\"a\":", 40)) + "1" + new string('}', 40));
        var deepArray = Utf8(new string('[', 300) + new string(']', 300));
        return new TheoryData<string, byte[]>
        {
            { "empty", Array.Empty<byte>() },
            { "spaces", Utf8("   \r\n  ") },
            { "plain text", Utf8("Bad Gateway") },
            { "html page", Utf8("<html><head><title>502</title></head><body><h1>Bad Gateway</h1></body></html>") },
            { "truncated object", Utf8("{\"type\":\"/problems/not-found\",\"detail\":\"docu") },
            { "truncated before the brace", Utf8("{\"type\":\"/problems/not-found\"") },
            { "json array", Utf8("[\"/problems/not-found\"]") },
            { "json array of objects", Utf8("[{\"type\":\"/problems/not-found\"}]") },
            { "json string", Utf8("\"/problems/not-found\"") },
            { "json number", Utf8("404") },
            { "json true", Utf8("true") },
            { "json null", Utf8("null") },
            { "huge valid body", hugeValid },
            { "deeply nested object", deepObject },
            { "deeply nested array", deepArray },
            { "invalid utf-8 in the middle", new byte[] { 0x7B, 0x22, 0x74, 0x79, 0x70, 0x65, 0x22, 0x3A, 0x22, 0xFF, 0xFE, 0x22, 0x7D } },
            { "only a bom", new byte[] { 0xEF, 0xBB, 0xBF } },
            { "nul bytes", new byte[] { 0, 0, 0, 0 } },
        };
    }

    [Theory]
    [MemberData(nameof(UnreadableBodies))]
    public void An_unreadable_body_falls_back_to_the_status_and_throws_nothing(string name, byte[] body)
    {
        Assert.NotNull(name);

        var notFound = Mapped<NotFoundException>(404, body);
        var serverError = Mapped<ServerErrorException>(502, body);
        var unexpected = Mapped<UnexpectedResponseException>(405, body);

        Assert.Null(notFound.ProblemType);
        Assert.Null(notFound.Detail);
        Assert.Null(serverError.ProblemType);
        Assert.Null(unexpected.ProblemType);
    }

    [Fact]
    public void A_body_just_over_the_limit_is_ignored_and_one_at_the_limit_is_read()
    {
        // Un problem+json di esattamente "length" byte, con lo slug conflict: letto, vince lo slug (Conflict) anche con status 500;
        // ignorato, decide lo status (ServerError).
        static byte[] ConflictBodyOfLength(int length)
        {
            var frame = Utf8("{\"type\":\"/problems/conflict\",\"detail\":\"\"}").Length;
            return Utf8("{\"type\":\"/problems/conflict\",\"detail\":\"" + new string('d', length - frame) + "\"}");
        }

        var atLimit = ConflictBodyOfLength(ProblemBody.MaxBytes);
        var overLimit = ConflictBodyOfLength(ProblemBody.MaxBytes + 1);
        Assert.Equal(ProblemBody.MaxBytes, atLimit.Length);
        Assert.Equal(ProblemBody.MaxBytes + 1, overLimit.Length);

        Assert.IsType<ConflictException>(ProblemMapper.Map(500, atLimit, null));
        Assert.IsType<ServerErrorException>(ProblemMapper.Map(500, overLimit, null));
        Assert.Null(ProblemMapper.Map(500, overLimit, null).ProblemType);
    }

    [Fact]
    public void A_body_with_a_byte_order_mark_is_read()
    {
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var json = Problem("not-found", 404, detail: "manca", requestId: "req-bom");
        var body = bom.Concat(json).ToArray();

        var exception = Mapped<NotFoundException>(404, body);

        Assert.Equal("not-found", exception.ProblemType);
        Assert.Equal("manca", exception.Detail);
        Assert.Equal("req-bom", exception.RequestId);
    }

    [Fact]
    public void A_lone_surrogate_in_detail_is_treated_as_absent_and_the_rest_of_the_body_still_counts()
    {
        // Il testo JSON contiene la sequenza backslash + "ud800" (un surrogato isolato): GetString() lancia, il mapper no.
        var body = Utf8("{\"type\":\"/problems/not-found\",\"detail\":\"x" + Backslash + "ud800y\",\"request_id\":\"req-9\"}");

        var exception = Mapped<NotFoundException>(404, body);

        Assert.Null(exception.Detail);
        Assert.Equal("not-found", exception.ProblemType);
        Assert.Equal("req-9", exception.RequestId);
    }

    [Fact]
    public void A_lone_surrogate_in_type_leaves_no_slug_so_the_status_decides()
    {
        var body = Utf8("{\"type\":\"/problems/not-fou" + Backslash + "udc00nd\",\"detail\":\"ok\"}");

        var exception = Mapped<ConflictException>(409, body);

        Assert.Null(exception.ProblemType);
        Assert.Equal("ok", exception.Detail);
    }

    [Fact]
    public void Random_bytes_never_make_the_mapper_throw()
    {
        var random = new Random(20261002);
        for (var i = 0; i < 3000; i++)
        {
            var body = new byte[random.Next(0, 64)];
            random.NextBytes(body);
            var status = new[] { 200, 400, 401, 404, 409, 415, 500, 503 }[random.Next(0, 8)];

            var exception = ProblemMapper.Map(status, body, "x");

            Assert.NotNull(exception);
        }
    }

    [Fact]
    public void Mutated_valid_bodies_never_make_the_mapper_throw()
    {
        // Ogni prefisso e ogni byte alterato di un problem+json vero: nessuna eccezione, sempre un'eccezione del Domain.
        var valid = Problem("not-found", 404, detail: "documento abc non trovato", requestId: "abc-123");
        for (var length = 0; length <= valid.Length; length++)
        {
            Assert.NotNull(ProblemMapper.Map(404, valid.Take(length).ToArray(), null));
        }

        for (var index = 0; index < valid.Length; index++)
        {
            var mutated = (byte[])valid.Clone();
            mutated[index] = 0xFF;
            Assert.NotNull(ProblemMapper.Map(404, mutated, null));
        }
    }

    // ----- type -> slug -----

    [Theory]
    [InlineData("/problems/not-found", "not-found")]
    [InlineData("/problems/a", "a")]
    [InlineData("/problems/Some_Slug.v2-x", "Some_Slug.v2-x")]
    [InlineData("https://gateway.example.test/problems/not-found", "not-found")]
    [InlineData("https://gateway.example.test/api/problems/conflict", "conflict")]
    [InlineData("/problems/outer/problems/not-found", "not-found")]
    public void The_slug_is_what_follows_the_last_problems_marker(string type, string slug)
    {
        var body = Utf8("{\"type\":\"" + type + "\"}");

        var exception = ProblemMapper.Map(500, body, null);

        Assert.Equal(slug, exception.ProblemType);
    }

    [Theory]
    [InlineData("about:blank")]
    [InlineData("not-found")]
    [InlineData("/problem/not-found")]
    [InlineData("/problems/")]
    [InlineData("/problems/not found")]
    [InlineData("/problems/not-found/")]
    [InlineData("/problems/not-found?x=1")]
    [InlineData("/problems/caf\\u00E9")]
    [InlineData("")]
    public void A_type_that_is_not_in_the_server_form_gives_no_slug(string type)
    {
        var body = Utf8("{\"type\":\"" + type + "\"}");

        var exception = ProblemMapper.Map(500, body, null);

        Assert.Null(exception.ProblemType);
        Assert.IsType<ServerErrorException>(exception);
    }

    [Fact]
    public void A_slug_longer_than_64_characters_is_not_a_slug_but_64_is()
    {
        var at = Utf8("{\"type\":\"/problems/" + new string('a', 64) + "\"}");
        var over = Utf8("{\"type\":\"/problems/" + new string('a', 65) + "\"}");

        Assert.Equal(new string('a', 64), ProblemMapper.Map(500, at, null).ProblemType);
        Assert.Null(ProblemMapper.Map(500, over, null).ProblemType);
    }

    [Fact]
    public void A_type_that_is_not_a_string_gives_no_slug()
    {
        foreach (var literal in new[] { "404", "null", "true", "[\"/problems/not-found\"]", "{\"a\":1}" })
        {
            var exception = ProblemMapper.Map(500, Utf8("{\"type\":" + literal + "}"), null);

            Assert.Null(exception.ProblemType);
        }
    }

    // ----- request_id -----

    [Fact]
    public void RequestId_comes_from_the_body()
    {
        var exception = ProblemMapper.Map(404, Problem("not-found", 404, requestId: "body-id-1"), headerRequestId: null);

        Assert.Equal("body-id-1", exception.RequestId);
    }

    [Fact]
    public void RequestId_falls_back_to_the_header_when_the_body_has_none_or_no_body()
    {
        Assert.Equal("header-id-1", ProblemMapper.Map(404, Problem("not-found", 404, requestId: null), "header-id-1").RequestId);
        Assert.Equal("header-id-1", ProblemMapper.Map(502, body: null, "header-id-1").RequestId);
        Assert.Equal("header-id-1", ProblemMapper.Map(502, Utf8("<html/>"), "header-id-1").RequestId);
    }

    [Fact]
    public void The_body_RequestId_wins_over_the_header()
    {
        var exception = ProblemMapper.Map(404, Problem("not-found", 404, requestId: "body-id-1"), "header-id-1");

        Assert.Equal("body-id-1", exception.RequestId);
    }

    [Fact]
    public void RequestId_is_null_when_neither_the_body_nor_the_header_has_one()
    {
        Assert.Null(ProblemMapper.Map(404, Problem("not-found", 404, requestId: null), null).RequestId);
        Assert.Null(ProblemMapper.Map(404, body: null, null).RequestId);
        Assert.Null(ProblemMapper.Map(404, body: null, headerRequestId: string.Empty).RequestId);
    }

    [Fact]
    public void An_implausible_RequestId_is_ignored_in_favour_of_the_header()
    {
        var withNewline = Utf8("{\"type\":\"/problems/not-found\",\"request_id\":\"a" + Backslash + "nb\"}");
        var withEscapeChar = Utf8("{\"type\":\"/problems/not-found\",\"request_id\":\"a" + Backslash + "u001Bb\"}");
        var tooLong = Utf8("{\"type\":\"/problems/not-found\",\"request_id\":\"" + new string('r', 129) + "\"}");
        var number = Utf8("{\"type\":\"/problems/not-found\",\"request_id\":12345}");

        foreach (var body in new[] { withNewline, withEscapeChar, tooLong, number })
        {
            Assert.Equal("header-id-1", ProblemMapper.Map(404, body, "header-id-1").RequestId);
            Assert.Null(ProblemMapper.Map(404, body, null).RequestId);
        }

        var atLimit = Utf8("{\"type\":\"/problems/not-found\",\"request_id\":\"" + new string('r', 128) + "\"}");
        Assert.Equal(new string('r', 128), ProblemMapper.Map(404, atLimit, "header-id-1").RequestId);
    }

    [Fact]
    public void An_implausible_header_RequestId_is_ignored()
    {
        Assert.Null(ProblemMapper.Map(502, body: null, "bad" + (char)10 + "id").RequestId);
        Assert.Null(ProblemMapper.Map(502, body: null, "bad" + (char)127 + "id").RequestId);
        Assert.Null(ProblemMapper.Map(502, body: null, new string('h', 129)).RequestId);
    }

    // ----- detail e messaggio -----

    [Fact]
    public void Detail_comes_from_the_body_and_goes_into_the_message_with_the_status()
    {
        var exception = ProblemMapper.Map(400, Problem("validation-error", 400, detail: "limit non valido"), null);

        Assert.Equal("limit non valido", exception.Detail);
        Assert.Contains("400", exception.Message, StringComparison.Ordinal);
        Assert.Contains("limit non valido", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_detail_the_message_is_the_default_of_the_exception_type()
    {
        Assert.Equal(new NotFoundException().Message, ProblemMapper.Map(404, Problem("not-found", 404), null).Message);
        Assert.Equal(new ConflictException().Message, ProblemMapper.Map(409, Problem("conflict", 409), null).Message);
        Assert.Equal(new ServerErrorException().Message, ProblemMapper.Map(500, body: null, null).Message);
        Assert.Equal(new UnauthorizedException().Message, ProblemMapper.Map(401, Problem("unauthorized", 401), null).Message);
    }

    [Fact]
    public void An_empty_or_non_string_detail_is_absent()
    {
        Assert.Null(ProblemMapper.Map(404, Utf8("{\"type\":\"/problems/not-found\",\"detail\":\"\"}"), null).Detail);
        Assert.Null(ProblemMapper.Map(404, Utf8("{\"type\":\"/problems/not-found\",\"detail\":42}"), null).Detail);
        Assert.Null(ProblemMapper.Map(404, Utf8("{\"type\":\"/problems/not-found\",\"detail\":null}"), null).Detail);
        Assert.Null(ProblemMapper.Map(404, Utf8("{\"type\":\"/problems/not-found\",\"detail\":[\"a\"]}"), null).Detail);
    }

    [Fact]
    public void Detail_with_non_ASCII_text_and_escapes_is_read_as_text()
    {
        var detail = "l" + Backslash + "u00E0 " + Backslash + "u0027x" + Backslash + "u0027";
        var exception = ProblemMapper.Map(404, Problem("not-found", 404, detail: detail), null);

        Assert.Equal("l" + (char)0xE0 + " 'x'", exception.Detail);
    }

    [Fact]
    public void The_message_of_an_unexpected_response_always_has_the_status()
    {
        Assert.Contains("405", ProblemMapper.Map(405, Problem("method-not-allowed", 405), null).Message, StringComparison.Ordinal);
        Assert.Contains("429", ProblemMapper.Map(429, body: null, null).Message, StringComparison.Ordinal);
        var withDetail = ProblemMapper.Map(405, Problem("method-not-allowed", 405, detail: "niente HEAD"), null);
        Assert.Contains("405", withDetail.Message, StringComparison.Ordinal);
        Assert.Contains("niente HEAD", withDetail.Message, StringComparison.Ordinal);
    }

    // ----- fixture del server (forme reali, valori inventati) -----

    [Fact]
    public void A_401_for_a_wrong_key_has_no_detail_and_a_401_for_a_missing_key_has_one()
    {
        var wrong = Utf8("{\"type\":\"/problems/unauthorized\",\"title\":\"chiave API non valida\",\"status\":401,\"request_id\":\"1c3b351b99588f66\"}");
        var missing = Utf8("{\"type\":\"/problems/unauthorized\",\"title\":\"autenticazione richiesta\",\"status\":401,\"detail\":\"indica la chiave API nell" + Backslash + "u0027intestazione X-API-Key o Authorization: Bearer\",\"request_id\":\"d616320be29fc72f\"}");

        var first = Mapped<UnauthorizedException>(401, wrong);
        var second = Mapped<UnauthorizedException>(401, missing);

        Assert.Null(first.Detail);
        Assert.Equal("1c3b351b99588f66", first.RequestId);
        Assert.Equal("indica la chiave API nell'intestazione X-API-Key o Authorization: Bearer", second.Detail);
    }

    [Fact]
    public void The_415_with_the_generic_error_slug_and_no_detail_is_unsupported_media_type()
    {
        var body = Utf8("{\"type\":\"/problems/error\",\"title\":\"errore\",\"status\":415,\"request_id\":\"c5d1287e6fba41d8\"}");

        var exception = Mapped<UnsupportedMediaTypeException>(415, body);

        Assert.Equal("error", exception.ProblemType);
        Assert.Null(exception.Detail);
        Assert.Equal("c5d1287e6fba41d8", exception.RequestId);
    }
}
