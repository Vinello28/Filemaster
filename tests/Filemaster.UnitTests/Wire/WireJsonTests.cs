using System.Text;
using System.Text.Json;
using Filemaster.Domain;
using Filemaster.Infrastructure;

namespace Filemaster.UnitTests.Wire;

/// <summary>
/// L'ingresso della lettura JSON (<see cref="WireJson"/>): un corpo vuoto, non JSON, non un oggetto, con BOM, troppo profondo; lo status e
/// l'id di correlazione veri nell'errore; nessun valore della risposta nei messaggi. I corpi sono scritti a mano.
/// </summary>
public sealed class WireJsonTests
{
    private const string Backslash = "\\";

    private static T Read<T>(byte[]? body, Func<WireObject, T> read, int status = 200) =>
        WireJson.ReadObject(body, WireTest.Context(status), "oggetto", read);

    private static string Name(WireObject o) => o.RequiredString("name");

    [Fact]
    public void A_null_or_empty_body_is_not_interpretable()
    {
        var empty = WireTest.Unexpected(() => Read(Array.Empty<byte>(), Name));
        var nothing = WireTest.Unexpected(() => Read(null, Name));

        Assert.Contains("vuoto", empty.Message, StringComparison.Ordinal);
        Assert.Contains("vuoto", nothing.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string> NotJson() => new()
    {
        "{",
        "{\"name\":",
        "nope",
        "<html><body>502</body></html>",
        "{'name':'x'}",
        "{\"name\":\"x\",}",
        "{\"name\":\"x\"} trailing",
        "{\"name\":\"x\"}{\"name\":\"y\"}",
        "{\"name\":\"x\"",
        "   ",
    };

    [Theory]
    [MemberData(nameof(NotJson))]
    public void A_body_that_is_not_valid_JSON_is_not_interpretable_and_keeps_the_parser_error_as_the_cause(string body)
    {
        var exception = WireTest.Unexpected(() => Read(WireTest.Utf8(body), Name));

        Assert.IsAssignableFrom<JsonException>(exception.InnerException);
    }

    public static TheoryData<string> NotAnObject() => new()
    {
        "[]",
        "[{\"name\":\"x\"}]",
        "1",
        "\"x\"",
        "null",
        "true",
        "false",
    };

    [Theory]
    [MemberData(nameof(NotAnObject))]
    public void A_root_that_is_not_an_object_is_not_interpretable(string body)
    {
        var exception = WireTest.Unexpected(() => Read(WireTest.Utf8(body), Name));

        Assert.Contains("oggetto", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_UTF8_byte_order_mark_is_tolerated_where_System_Text_Json_rejects_it()
    {
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var body = bom.Concat(WireTest.Utf8("{\"name\":\"con bom\"}")).ToArray();
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(body)); // il controllo non passa a vuoto: STJ lo rifiuta davvero

        Assert.Equal("con bom", Read(body, Name));
    }

    [Fact]
    public void A_body_that_is_only_a_byte_order_mark_is_not_interpretable()
    {
        WireTest.Unexpected(() => Read(new byte[] { 0xEF, 0xBB, 0xBF }, Name));
    }

    [Fact]
    public void The_error_carries_the_real_status_and_the_request_id_of_the_response()
    {
        var created = Assert.Throws<UnexpectedResponseException>(() => WireJson.ReadObject(WireTest.Utf8("{}"), new WireContext(201, "abc-123"), "oggetto", Name));
        var partial = Assert.Throws<UnexpectedResponseException>(() => WireJson.ReadObject(WireTest.Utf8("{}"), new WireContext(206, null), "oggetto", Name));

        Assert.Equal(201, created.StatusCode);
        Assert.Equal("abc-123", created.RequestId);
        Assert.Equal(206, partial.StatusCode);
        Assert.Null(partial.RequestId);
        Assert.Null(created.ProblemType);
        Assert.Null(created.Detail);
    }

    [Fact]
    public void The_context_of_a_transport_response_has_its_status_and_request_id()
    {
        using var message = new HttpResponseMessage();
        using var content = new ByteArrayContent(Array.Empty<byte>());
        var response = new TransportResponse(201, "req-xyz", WireTest.Utf8("{}"), message.Headers, content.Headers);

        var context = WireContext.Of(response);

        Assert.Equal(201, context.StatusCode);
        Assert.Equal("req-xyz", context.RequestId);
    }

    [Fact]
    public void The_message_never_contains_a_value_of_the_response()
    {
        // Un valore che potrebbe essere un segreto o un dato personale: finisce nei log, non deve stare nel messaggio.
        const string Sentinel = "SENTINELLA-valore-riservato-123";

        var wrongType = WireTest.Unexpected(() => Read(WireTest.Utf8("{\"count\":\"" + Sentinel + "\"}"), o => o.RequiredCount("count")));
        var badId = WireTest.Unexpected(() => Read(WireTest.Utf8("{\"id\":\"" + Sentinel + "\"}"), o => o.RequiredId<DocumentId>("id", DocumentId.TryParse, "un id di documento")));
        var badDate = WireTest.Unexpected(() => Read(WireTest.Utf8("{\"at\":\"" + Sentinel + "\"}"), o => o.RequiredDate("at")));

        Assert.DoesNotContain(Sentinel, wrongType.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Sentinel, badId.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Sentinel, badDate.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_message_names_the_field_that_is_missing_or_wrong()
    {
        var missing = WireTest.Unexpected(() => Read(WireTest.Utf8("{}"), o => o.RequiredString("name")));
        var wrong = WireTest.Unexpected(() => Read(WireTest.Utf8("{\"name\":5}"), o => o.RequiredString("name")));

        Assert.Contains("oggetto.name", missing.Message, StringComparison.Ordinal);
        Assert.Contains("oggetto.name", wrong.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Nesting_up_to_the_depth_the_server_can_store_is_accepted()
    {
        // Il server legge e scrive i metadati con la profondita' di default (64): radice + items + documento + 64 livelli = 67.
        var body = WireTest.Utf8("{\"deep\":" + Nested(100) + "}");

        var kind = Read(body, o => o.OptionalObjectCopy("deep")!.Value.ValueKind);

        Assert.Equal(JsonValueKind.Object, kind);
    }

    [Fact]
    public void Nesting_deeper_than_the_limit_is_not_interpretable_and_does_not_escape_as_a_parser_exception()
    {
        var body = WireTest.Utf8("{\"deep\":" + Nested(400) + "}");

        var exception = WireTest.Unexpected(() => Read(body, o => o.OptionalObjectCopy("deep")));

        Assert.IsAssignableFrom<JsonException>(exception.InnerException);
    }

    [Fact]
    public void A_string_with_an_isolated_surrogate_escape_is_not_interpretable_and_does_not_leak_an_InvalidOperationException()
    {
        // Il parser accetta "\ud800" (surrogato isolato); e' GetString() a lanciare.
        var body = WireTest.Utf8("{\"name\":\"a" + Backslash + "ud800b\"}");

        var exception = WireTest.Unexpected(() => Read(body, Name));

        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    [Fact]
    public void A_string_with_invalid_UTF8_bytes_is_not_interpretable_and_does_not_leak_an_InvalidOperationException()
    {
        var body = Encoding.ASCII.GetBytes("{\"name\":\"a").Concat(new byte[] { 0xFF, 0xFE }).Concat(Encoding.ASCII.GetBytes("b\"}")).ToArray();

        var exception = WireTest.Unexpected(() => Read(body, Name));

        Assert.IsAssignableFrom<ArgumentException>(exception.InnerException); // DecoderFallbackException: rifiutato all'ingresso
    }

    [Fact]
    public void A_property_name_with_an_isolated_surrogate_escape_is_either_ignored_or_not_interpretable_and_never_another_exception()
    {
        // I difetti di UTF-16/UTF-8 possono stare anche nei NOMI delle proprieta': la lettura riesce (chiave ignorata) oppure e' non interpretabile.
        var unknownFirst = WireTest.Utf8("{\"" + Backslash + "ud800\":1,\"name\":\"x\"}");
        var unknownLast = WireTest.Utf8("{\"name\":\"x\",\"a" + Backslash + "udc00b\":{\"y\":2}}");
        var missing = WireTest.Utf8("{\"" + Backslash + "ud800\":1}");

        Assert.Equal("x", Read(unknownFirst, Name));
        Assert.Equal("x", Read(unknownLast, Name));
        WireTest.Unexpected(() => Read(missing, Name));
    }

    [Fact]
    public void Invalid_UTF8_bytes_anywhere_in_the_body_are_not_interpretable_whether_in_a_name_a_value_or_unread_data()
    {
        // Rifiutati all'ingresso: nei nomi, nei valori e anche nei dati che nessun lettore guarderebbe (i metadati), dove poi GetRawText() lancerebbe.
        var inName = Encoding.ASCII.GetBytes("{\"a").Concat(new byte[] { 0xFF, 0xFE }).Concat(Encoding.ASCII.GetBytes("b\":1,\"name\":\"x\"}")).ToArray();
        var inUnreadValue = Encoding.ASCII.GetBytes("{\"name\":\"x\",\"altro\":\"a").Concat(new byte[] { 0xC3 }).Concat(Encoding.ASCII.GetBytes("\"}")).ToArray();
        var overlong = Encoding.ASCII.GetBytes("{\"name\":\"").Concat(new byte[] { 0xC0, 0x80 }).Concat(Encoding.ASCII.GetBytes("\"}")).ToArray();

        foreach (var body in new[] { inName, inUnreadValue, overlong })
        {
            var exception = WireTest.Unexpected(() => Read(body, Name));

            Assert.IsAssignableFrom<ArgumentException>(exception.InnerException);
            Assert.Contains("UTF-8", exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Valid_multibyte_UTF8_and_a_byte_order_mark_pass_the_strict_check()
    {
        var body = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("{\"name\":\"perch\U000000E9 \U0001F600 \U000020AC\"}")).ToArray();

        Assert.Equal("perch\U000000E9 \U0001F600 \U000020AC", Read(body, Name));
    }

    [Fact]
    public void An_unknown_property_is_ignored()
    {
        Assert.Equal("x", Read(WireTest.Utf8("{\"name\":\"x\",\"novita\":{\"a\":[1,2,3]},\"altro\":null}"), Name));
    }

    [Fact]
    public void With_a_repeated_key_the_last_value_wins_like_the_parser()
    {
        Assert.Equal("secondo", Read(WireTest.Utf8("{\"name\":\"primo\",\"name\":\"secondo\"}"), Name));
    }

    [Fact]
    public void A_copied_object_survives_the_disposal_of_the_parsed_document()
    {
        // ReadObject smaltisce il documento: un elemento non copiato sarebbe invalido (ObjectDisposedException a ogni lettura).
        var copy = Read(WireTest.Utf8("{\"m\":{\"a\":[1,2,{\"b\":null}]}}"), o => o.OptionalObjectCopy("m")!.Value);

        Assert.Equal("{\"a\":[1,2,{\"b\":null}]}", copy.GetRawText());
    }

    private static string Nested(int levels) => string.Concat(Enumerable.Repeat("{\"a\":", levels)) + "1" + new string('}', levels);
}
