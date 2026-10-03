using Filemaster.Infrastructure;

namespace Filemaster.UnitTests.Wire;

/// <summary>
/// La codifica percent dei valori della query string, con vettori scritti A MANO (non ricalcolati con <c>Uri.EscapeDataString</c>): spazio come
/// <c>%20</c> e mai <c>+</c>, <c>&amp; = + % /</c> codificati, non ASCII e emoji come byte UTF-8 (coppie surrogate comprese), esadecimale
/// maiuscolo. Un surrogato isolato e' rifiutato (.NET 8+ lo sostituirebbe in silenzio con U+FFFD, .NET Framework lancerebbe UriFormatException) e una stringa molto lunga si codifica a
/// pezzi senza spezzare una coppia surrogata.
/// </summary>
public sealed class PercentEncodingTests
{
    private static string Encode(string value) => PercentEncoding.Encode(value, "valore");

    public static TheoryData<string, string> Vectors() => new()
    {
        { string.Empty, string.Empty },
        { "abc", "abc" },
        { "ABCxyz0189", "ABCxyz0189" },
        { "-._~", "-._~" },
        { "Acme Srl", "Acme%20Srl" },
        { "a b", "a%20b" },
        { " ", "%20" },
        { "a&b=c", "a%26b%3Dc" },
        { "a+b", "a%2Bb" },
        { "100%", "100%25" },
        { "%20", "%2520" },
        { "a/b", "a%2Fb" },
        { "a?b#c", "a%3Fb%23c" },
        { "a:b;c@d,e", "a%3Ab%3Bc%40d%2Ce" },
        { "'*()!", "%27%2A%28%29%21" },
        { "\"\\", "%22%5C" },
        { "<>[]{}|^`", "%3C%3E%5B%5D%7B%7D%7C%5E%60" },
        { "\r\n\t\0", "%0D%0A%09%00" },
        { "\U00000080\U000007FF", "%C2%80%DF%BF" },
        { "perch\U000000E9 \U000000E8", "perch%C3%A9%20%C3%A8" },
        { "\U000020AC", "%E2%82%AC" },
        { "\U0001F600", "%F0%9F%98%80" },
        { "a\U0001F600b\U0001F4C1", "a%F0%9F%98%80b%F0%9F%93%81" },
        { "\U0000FFFF", "%EF%BF%BF" },
        { "{\"arxivar\":{\"docnumber\":12345}}", "%7B%22arxivar%22%3A%7B%22docnumber%22%3A12345%7D%7D" },
        { "!!!", "%21%21%21" },
        { " zz/", "%20zz%2F" },
        { "Acme Srl & Co. = 50% + 1/2", "Acme%20Srl%20%26%20Co.%20%3D%2050%25%20%2B%201%2F2" },
    };

    [Theory]
    [MemberData(nameof(Vectors))]
    public void A_value_is_encoded_as_percent_escaped_UTF8_with_uppercase_hex(string value, string expected)
    {
        Assert.Equal(expected, Encode(value));
    }

    [Fact]
    public void The_captured_requests_are_reproduced_by_the_encoder()
    {
        // Righe di richiesta vere accettate dal server (fixture 46, 77, 81, 94): i valori codificati dal server di prova coincidono.
        Assert.Equal("parent_id=%20zz%2F", "parent_id=" + Encode(" zz/"));
        Assert.EndsWith("metadata=" + Encode("{\"arxivar\":{\"docnumber\":12345}}"), WireFixtures.RequestPath("77-docs-list-filter-metadata"), StringComparison.Ordinal);
        Assert.EndsWith("sender=" + Encode("Acme Srl"), WireFixtures.RequestPath("81-docs-list-filter-sender"), StringComparison.Ordinal);
        Assert.EndsWith("cursor=" + Encode("!!!"), WireFixtures.RequestPath("94-err-400-cursor-bad"), StringComparison.Ordinal);
        Assert.EndsWith("parent_id=" + Encode(" zz/"), WireFixtures.RequestPath("46-err-404-folder-list-parent-malformed"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ar-SA")]
    [InlineData("it-IT")]
    [InlineData("tr-TR")]
    public void Encoding_does_not_depend_on_the_current_culture(string culture)
    {
        WireTest.WithCulture(culture, () =>
        {
            Assert.Equal("Acme%20Srl%20i%C4%B1%C4%B0", Encode("Acme Srl i\U00000131\U00000130"));
        });
    }

    // ----- surrogati isolati -----

    public static TheoryData<string> LoneSurrogates() => new()
    {
        "a" + new string((char)0xD800, 1) + "b",
        new string((char)0xD800, 1),
        new string((char)0xDC00, 1),
        "abc" + new string((char)0xD83D, 1),
        new string((char)0xDE00, 1) + "abc",
        new string((char)0xD83D, 1) + new string((char)0xD83D, 1) + new string((char)0xDE00, 1),
        new string((char)0xDE00, 1) + new string((char)0xD83D, 1),
        "\U0001F600" + new string((char)0xDC00, 1),
    };

    [Theory]
    [MemberData(nameof(LoneSurrogates))]
    public void A_lone_surrogate_is_refused_with_the_name_of_the_value_and_never_replaced_silently(string value)
    {
        var exception = Assert.Throws<ArgumentException>(() => PercentEncoding.Encode(value, "Owner"));

        Assert.Equal("Owner", exception.ParamName);
    }

    [Fact]
    public void The_framework_mishandles_a_lone_surrogate_which_is_why_it_is_refused_first()
    {
        var value = "a" + new string((char)0xD800, 1) + "b";

#if NETFRAMEWORK
        // Autoverifica (misurata sul job Windows net48 della CI): senza il controllo uscirebbe una UriFormatException senza il nome del parametro.
        Assert.Throws<UriFormatException>(() => Uri.EscapeDataString(value));
#else
        // Autoverifica (misurata su .NET 10): senza il controllo il valore partirebbe diverso da quello dell'utente.
        Assert.Equal("a%EF%BF%BDb", Uri.EscapeDataString(value));
#endif
        Assert.Throws<ArgumentException>(() => Encode(value));
    }

    [Fact]
    public void A_well_formed_pair_next_to_other_text_is_accepted()
    {
        Assert.Equal("%F0%9F%98%80%F0%9F%98%80", Encode("\U0001F600\U0001F600"));
    }

    [Fact]
    public void RequireWellFormed_accepts_text_and_refuses_lone_surrogates_for_every_outgoing_text()
    {
        PercentEncoding.RequireWellFormed("testo \U0001F600 \U000000E8", "x");
        PercentEncoding.RequireWellFormed(string.Empty, "x");

        var exception = Assert.Throws<ArgumentException>(() => PercentEncoding.RequireWellFormed("a" + new string((char)0xDC00, 1), "Tag"));
        Assert.Equal("Tag", exception.ParamName);
    }

    // ----- stringhe lunghe -----

    [Fact]
    public void A_long_value_is_encoded_in_chunks_with_the_same_result_as_a_short_one()
    {
        var plain = new string('x', 100_000);
        var accented = string.Concat(Enumerable.Repeat("\U000000E9", 40_000));
        var spaces = new string(' ', 50_000);

        Assert.Equal(plain, Encode(plain));
        Assert.Equal(string.Concat(Enumerable.Repeat("%C3%A9", 40_000)), Encode(accented));
        Assert.Equal(string.Concat(Enumerable.Repeat("%20", 50_000)), Encode(spaces));
    }

    [Fact]
    public void A_surrogate_pair_that_falls_on_a_chunk_boundary_is_never_split()
    {
        // 16.383 caratteri e poi una coppia: l'alto sta alla posizione 16.383 e il basso alla 16.384, esattamente sul confine.
        var value = new string('x', 16_383) + "\U0001F600" + "y";

        Assert.Equal(new string('x', 16_383) + "%F0%9F%98%80" + "y", Encode(value));
    }

    [Fact]
    public void Pairs_on_every_kind_of_boundary_survive_a_long_value()
    {
        var shifted = "x" + string.Concat(Enumerable.Repeat("\U0001F600", 30_000));
        var aligned = string.Concat(Enumerable.Repeat("\U0001F600", 30_000));

        Assert.Equal("x" + string.Concat(Enumerable.Repeat("%F0%9F%98%80", 30_000)), Encode(shifted));
        Assert.Equal(string.Concat(Enumerable.Repeat("%F0%9F%98%80", 30_000)), Encode(aligned));
    }

    [Fact]
    public void A_lone_surrogate_deep_inside_a_long_value_is_still_refused()
    {
        var value = new string('x', 70_000) + new string((char)0xD800, 1) + new string('y', 10);

        Assert.Throws<ArgumentException>(() => Encode(value));
    }

    [Fact]
    public void A_null_value_is_an_argument_error()
    {
        Assert.Throws<ArgumentNullException>(() => PercentEncoding.Encode(null!, "x"));
    }

    // ----- il valore sopravvive alla combinazione con l'indirizzo base -----

    [Fact]
    public void An_encoded_value_survives_being_combined_with_a_base_address_and_decodes_to_the_original()
    {
        const string Original = "a b&c=d+e%f/g?h#i perch\U000000E9 \U0001F600";
        var baseAddress = new Uri("https://files.example.test/api/");

        var combined = new Uri(baseAddress, "documents?owner=" + Encode(Original));

        Assert.Equal("?owner=a%20b%26c%3Dd%2Be%25f%2Fg%3Fh%23i%20perch%C3%A9%20%F0%9F%98%80", combined.Query);
        Assert.Equal(Original, Uri.UnescapeDataString(combined.Query.Remove(0, "?owner=".Length)));
        Assert.Equal("/api/documents", combined.AbsolutePath);
    }
}
