using Filemaster.Domain;
using Filemaster.Infrastructure;

namespace Filemaster.UnitTests.Wire;

/// <summary>
/// Le letture tipizzate di <see cref="WireObject"/>: per ogni tipo, il valore giusto, la proprieta' assente, <c>null</c>, il tipo sbagliato e i
/// valori estremi. Assente e <c>null</c> sono la stessa cosa (il server omette i null); un campo obbligatorio assente o sbagliato e'
/// <see cref="UnexpectedResponseException"/>. I corpi sono JSON scritto a mano.
/// </summary>
public sealed class WireObjectTests
{
    private const string DocId = "doc_01M3VEESG5KBYR5PYAJ0TDT4B2";

    private static T Read<T>(string json, Func<WireObject, T> read) =>
        WireJson.ReadObject(WireTest.Utf8(json), WireTest.Context(), "oggetto", read);

    private static string One(string value) => "{\"v\":" + value + "}";

    // ----- testo -----

    [Theory]
    [InlineData("\"abc\"", "abc")]
    [InlineData("\"\"", "")]
    [InlineData("\"  spazi  \"", "  spazi  ")]
    [InlineData("\"a\\\"b\\\\c\"", "a\"b\\c")]
    [InlineData("\"\\u00e8\"", "\U000000E8")]
    public void A_required_string_is_read_as_it_is(string json, string expected)
    {
        Assert.Equal(expected, Read(One(json), o => o.RequiredString("v")));
    }

    [Fact]
    public void A_string_of_4096_bytes_and_an_emoji_are_read_intact()
    {
        var big = new string('x', 4096);
        var emoji = "ciao \U0001F600";

        Assert.Equal(big, Read(One("\"" + big + "\""), o => o.RequiredString("v")));
        Assert.Equal(emoji, Read(One("\"" + emoji + "\""), o => o.RequiredString("v")));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"v\":null}")]
    public void A_missing_or_null_required_string_is_not_interpretable(string json)
    {
        var exception = WireTest.Unexpected(() => Read(json, o => o.RequiredString("v")));

        Assert.Contains("manca", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("5")]
    [InlineData("true")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void A_string_property_of_another_type_is_not_interpretable(string value)
    {
        WireTest.Unexpected(() => Read(One(value), o => o.RequiredString("v")));
        WireTest.Unexpected(() => Read(One(value), o => o.OptionalString("v")));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"v\":null}")]
    public void A_missing_or_null_optional_string_is_null(string json)
    {
        Assert.Null(Read(json, o => o.OptionalString("v")));
    }

    [Fact]
    public void An_empty_optional_string_is_empty_not_null()
    {
        Assert.Equal(string.Empty, Read(One("\"\""), o => o.OptionalString("v")));
    }

    // ----- interi -----

    [Theory]
    [InlineData("0", 0)]
    [InlineData("590", 590)]
    [InlineData("-5", -5)]
    [InlineData("2147483647", int.MaxValue)]
    [InlineData("-2147483648", int.MinValue)]
    public void An_integer_is_read_within_the_range_of_int(string json, int expected)
    {
        Assert.Equal(expected, Read(One(json), o => o.RequiredInt32("v")));
        Assert.Equal(expected, Read(One(json), o => o.OptionalInt32("v")));
    }

    [Theory]
    [InlineData("2147483648")]
    [InlineData("-2147483649")]
    [InlineData("99999999999999999999")]
    [InlineData("12.5")]
    [InlineData("590.0")]
    [InlineData("1e3")]
    [InlineData("\"5\"")]
    [InlineData("true")]
    [InlineData("[5]")]
    public void A_value_that_is_not_an_integer_of_32_bits_is_not_interpretable(string value)
    {
        WireTest.Unexpected(() => Read(One(value), o => o.RequiredInt32("v")));
        WireTest.Unexpected(() => Read(One(value), o => o.OptionalInt32("v")));
    }

    [Fact]
    public void A_missing_integer_is_an_error_when_required_and_null_when_optional()
    {
        WireTest.Unexpected(() => Read("{}", o => o.RequiredInt32("v")));
        WireTest.Unexpected(() => Read("{\"v\":null}", o => o.RequiredInt32("v")));
        Assert.Null(Read("{}", o => o.OptionalInt32("v")));
        Assert.Null(Read("{\"v\":null}", o => o.OptionalInt32("v")));
    }

    [Fact]
    public void A_count_is_not_negative()
    {
        Assert.Equal(0, Read(One("0"), o => o.RequiredCount("v")));
        Assert.Equal(7, Read(One("7"), o => o.OptionalCount("v")));
        Assert.Null(Read("{}", o => o.OptionalCount("v")));
        WireTest.Unexpected(() => Read(One("-1"), o => o.RequiredCount("v")));
        WireTest.Unexpected(() => Read(One("-1"), o => o.OptionalCount("v")));
        WireTest.Unexpected(() => Read("{}", o => o.RequiredCount("v")));
    }

    [Theory]
    [InlineData("0", 0L)]
    [InlineData("590", 590L)]
    [InlineData("5242880", 5242880L)]
    [InlineData("3000000000", 3000000000L)]
    [InlineData("9223372036854775807", long.MaxValue)]
    public void A_size_is_read_as_a_64_bit_integer_that_can_exceed_int(string json, long expected)
    {
        Assert.Equal(expected, Read(One(json), o => o.RequiredSize("v")));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("9223372036854775808")]
    [InlineData("590.5")]
    [InlineData("1e3")]
    [InlineData("\"590\"")]
    [InlineData("null")]
    public void A_size_that_is_negative_not_an_integer_or_missing_is_not_interpretable(string value)
    {
        WireTest.Unexpected(() => Read(One(value), o => o.RequiredSize("v")));
    }

    [Fact]
    public void A_missing_size_is_not_interpretable()
    {
        WireTest.Unexpected(() => Read("{}", o => o.RequiredSize("v")));
    }

    // ----- booleani -----

    [Fact]
    public void A_boolean_is_true_or_false_and_nothing_else()
    {
        Assert.True(Read(One("true"), o => o.RequiredBool("v")));
        Assert.False(Read(One("false"), o => o.RequiredBool("v")));
        foreach (var wrong in new[] { "\"true\"", "1", "0", "null", "[]" })
        {
            WireTest.Unexpected(() => Read(One(wrong), o => o.RequiredBool("v")));
        }

        WireTest.Unexpected(() => Read("{}", o => o.RequiredBool("v")));
    }

    // ----- date -----

    [Fact]
    public void A_date_is_read_with_its_offset_and_never_as_local_time()
    {
        var z = Read(One("\"2026-10-01T09:59:12.9531Z\""), o => o.RequiredDate("v"));
        var plus = Read(One("\"2026-10-01T09:59:12+02:00\""), o => o.RequiredDate("v"));

        Assert.Equal(WireTest.Utc(2026, 10, 1, 9, 59, 12, 9531000), z);
        Assert.Equal(TimeSpan.Zero, z.Offset);
        Assert.Equal(WireTest.Utc(2026, 10, 1, 7, 59, 12), plus.ToUniversalTime());
        Assert.Equal(TimeSpan.FromHours(2), plus.Offset);
    }

    [Theory]
    [InlineData("\"2026-10-01T09:59:12\"")]
    [InlineData("\"2026-10-01\"")]
    [InlineData("\"ieri\"")]
    [InlineData("\"\"")]
    [InlineData("1790848752")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("{}")]
    public void A_date_without_an_offset_or_of_another_shape_is_not_interpretable(string value)
    {
        WireTest.Unexpected(() => Read(One(value), o => o.RequiredDate("v")));
    }

    [Fact]
    public void A_missing_date_is_not_interpretable()
    {
        WireTest.Unexpected(() => Read("{}", o => o.RequiredDate("v")));
    }

    // ----- id forti -----

    [Fact]
    public void A_valid_id_is_read_through_the_TryParse_of_its_type()
    {
        var id = Read(One("\"" + DocId + "\""), o => o.RequiredId<DocumentId>("v", DocumentId.TryParse, "un id di documento"));
        var optional = Read(One("\"" + DocId + "\""), o => o.OptionalId<DocumentId>("v", DocumentId.TryParse, "un id di documento"));

        Assert.Equal(new DocumentId(DocId), id);
        Assert.Equal(new DocumentId(DocId), optional);
    }

    [Theory]
    [InlineData("\"doc_abc\"")]
    [InlineData("\"\"")]
    [InlineData("\"doc_01m3veesg5kbyr5pyaj0tdt4b2\"")]
    [InlineData("\"fld_01M3VEESG5KBYR5PYAJ0TDT4B2\"")]
    [InlineData("\"doc_01M3VEESG5KBYR5PYAJ0TDT4B2\\n\"")]
    [InlineData("\"doc_81M3VEESG5KBYR5PYAJ0TDT4B2\"")]
    [InlineData("5")]
    public void An_invalid_id_is_not_interpretable_and_the_empty_id_is_never_produced(string value)
    {
        WireTest.Unexpected(() => Read(One(value), o => o.RequiredId<DocumentId>("v", DocumentId.TryParse, "un id di documento")));
        WireTest.Unexpected(() => Read(One(value), o => o.OptionalId<DocumentId>("v", DocumentId.TryParse, "un id di documento")));
    }

    [Fact]
    public void A_missing_id_is_an_error_when_required_and_null_when_optional()
    {
        WireTest.Unexpected(() => Read("{}", o => o.RequiredId<DocumentId>("v", DocumentId.TryParse, "un id di documento")));
        WireTest.Unexpected(() => Read("{\"v\":null}", o => o.RequiredId<DocumentId>("v", DocumentId.TryParse, "un id di documento")));
        Assert.Null(Read("{}", o => o.OptionalId<DocumentId>("v", DocumentId.TryParse, "un id di documento")));
        Assert.Null(Read("{\"v\":null}", o => o.OptionalId<DocumentId>("v", DocumentId.TryParse, "un id di documento")));
    }

    // ----- SHA-256 -----

    private static readonly string Sha = "cc1ba284a9fe9cefa40d4bd9dfb8d9e7fb395431aaf79478efca4e04da6c9d7e";

    [Fact]
    public void A_sha256_of_64_lowercase_hex_digits_is_read_as_it_is()
    {
        Assert.Equal(Sha, Read(One("\"" + Sha + "\""), o => o.RequiredSha256("v")));
        Assert.Equal(Sha, Read(One("\"" + Sha + "\""), o => o.OptionalSha256("v")));
        Assert.Equal(new string('0', 64), Read(One("\"" + new string('0', 64) + "\""), o => o.RequiredSha256("v")));
    }

    public static TheoryData<string> BadHashes() => new()
    {
        new string('a', 63),
        new string('a', 65),
        string.Empty,
        Sha.ToUpperInvariant(),
        "g" + new string('a', 63),
        " " + new string('a', 63),
        new string('a', 63) + "\\n",
        "sha256:" + new string('a', 57),
        new string('\U00000663', 64),
        new string('\U0000FF11', 64),
    };

    [Theory]
    [MemberData(nameof(BadHashes))]
    public void A_sha256_that_is_not_64_lowercase_ASCII_hex_digits_is_not_interpretable(string hash)
    {
        // Uppercase, lunghezze diverse e cifre Unicode (arabo-indiche, a larghezza piena) non sono un SHA-256: chi lo verifica lancerebbe ArgumentException.
        WireTest.Unexpected(() => Read(One("\"" + hash + "\""), o => o.RequiredSha256("v")));
        WireTest.Unexpected(() => Read(One("\"" + hash + "\""), o => o.OptionalSha256("v")));
    }

    [Fact]
    public void A_missing_sha256_is_an_error_when_required_and_null_when_optional()
    {
        WireTest.Unexpected(() => Read("{}", o => o.RequiredSha256("v")));
        Assert.Null(Read("{}", o => o.OptionalSha256("v")));
        Assert.Null(Read("{\"v\":null}", o => o.OptionalSha256("v")));
        WireTest.Unexpected(() => Read(One("5"), o => o.OptionalSha256("v")));
    }

    // ----- liste -----

    [Fact]
    public void A_list_of_objects_is_read_in_order()
    {
        var names = Read("{\"items\":[{\"n\":\"a\"},{\"n\":\"b\"},{\"n\":\"c\"}]}", o => o.RequiredList("items", item => item.RequiredString("n")));

        Assert.Equal(new[] { "a", "b", "c" }, names);
    }

    [Fact]
    public void An_empty_list_is_valid_and_a_missing_list_is_an_error_when_required_and_null_when_optional()
    {
        Assert.Empty(Read("{\"items\":[]}", o => o.RequiredList("items", item => item.RequiredString("n"))));
        WireTest.Unexpected(() => Read("{}", o => o.RequiredList("items", item => item.RequiredString("n"))));
        WireTest.Unexpected(() => Read("{\"items\":null}", o => o.RequiredList("items", item => item.RequiredString("n"))));
        Assert.Null(Read("{}", o => o.OptionalList("items", item => item.RequiredString("n"))));
        Assert.Null(Read("{\"items\":null}", o => o.OptionalList("items", item => item.RequiredString("n"))));
    }

    [Theory]
    [InlineData("{\"items\":{}}")]
    [InlineData("{\"items\":\"x\"}")]
    [InlineData("{\"items\":5}")]
    public void A_list_property_that_is_not_an_array_is_not_interpretable(string json)
    {
        WireTest.Unexpected(() => Read(json, o => o.RequiredList("items", item => item.RequiredString("n"))));
        WireTest.Unexpected(() => Read(json, o => o.OptionalList("items", item => item.RequiredString("n"))));
    }

    [Theory]
    [InlineData("{\"items\":[{\"n\":\"a\"},5]}", 1)]
    [InlineData("{\"items\":[null]}", 0)]
    [InlineData("{\"items\":[{\"n\":\"a\"},{\"n\":\"b\"},[]]}", 2)]
    [InlineData("{\"items\":[\"x\"]}", 0)]
    public void A_list_element_that_is_not_an_object_is_not_interpretable_and_the_message_gives_its_index(string json, int index)
    {
        var exception = WireTest.Unexpected(() => Read(json, o => o.RequiredList("items", item => item.RequiredString("n"))));

        Assert.Contains("items[" + index + "]", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_error_inside_a_list_element_gives_the_path_with_the_index()
    {
        var exception = WireTest.Unexpected(() => Read("{\"items\":[{\"n\":\"a\"},{\"n\":\"b\"},{}]}", o => o.RequiredList("items", item => item.RequiredString("n"))));

        Assert.Contains("oggetto.items[2].n", exception.Message, StringComparison.Ordinal);
    }

    // ----- oggetto copiato -----

    [Fact]
    public void An_object_is_copied_and_a_missing_or_null_one_is_null()
    {
        Assert.Equal("{\"a\":1}", Read("{\"m\":{\"a\":1}}", o => o.OptionalObjectCopy("m")!.Value.GetRawText()));
        Assert.Equal("{}", Read("{\"m\":{}}", o => o.OptionalObjectCopy("m")!.Value.GetRawText()));
        Assert.Null(Read("{}", o => o.OptionalObjectCopy("m")));
        Assert.Null(Read("{\"m\":null}", o => o.OptionalObjectCopy("m")));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[{}]")]
    [InlineData("\"x\"")]
    [InlineData("5")]
    [InlineData("true")]
    public void A_property_that_is_not_an_object_cannot_be_copied_as_one(string value)
    {
        WireTest.Unexpected(() => Read(One(value), o => o.OptionalObjectCopy("v")));
    }
}
