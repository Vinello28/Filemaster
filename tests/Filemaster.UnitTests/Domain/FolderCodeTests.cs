using Filemaster.Domain;

namespace Filemaster.UnitTests.Domain;

/// <summary>
/// Regole di FolderCode: lunghezza 1..50, primo carattere alfanumerico ASCII, poi alfanumerici ASCII, '_', '.' e '-'
/// (FolderCodes.IsValid di Sharp-a-File). Il client e' piu' stretto del server in un punto solo, volutamente: il
/// server accetta un newline finale (il "$" della sua regex) e trimma gli input; qui nessun trim e nessun newline.
/// </summary>
public sealed class FolderCodeTests
{
    private const string AsciiAlphanumerics = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    private static readonly HashSet<char> FirstCharacters = new(AsciiAlphanumerics);
    private static readonly HashSet<char> InnerCharacters = new(AsciiAlphanumerics + "_.-");

    private static void AssertAccepted(string value)
    {
        Assert.True(FolderCode.IsValid(value), $"IsValid(\"{value}\") doveva essere true");

        Assert.True(FolderCode.TryParse(value, out var parsed), $"TryParse(\"{value}\") doveva riuscire");
        Assert.Equal(value, parsed.Value);
        Assert.Equal(value, parsed.ToString());
        Assert.False(parsed.IsEmpty);
        Assert.Equal(parsed, FolderCode.Parse(value));
        Assert.Equal(parsed, new FolderCode(value));
    }

    private static void AssertRejected(string? value)
    {
        Assert.False(FolderCode.IsValid(value), $"IsValid(\"{value}\") doveva essere false");

        Assert.False(FolderCode.TryParse(value, out var parsed), $"TryParse(\"{value}\") doveva fallire");
        Assert.Equal(default(FolderCode), parsed);

        if (value is null)
        {
            Assert.Equal("value", Assert.Throws<ArgumentNullException>(() => FolderCode.Parse(null!)).ParamName);
            Assert.Equal("value", Assert.Throws<ArgumentNullException>(() => new FolderCode(null!)).ParamName);
        }
        else
        {
            // Throws<T> vuole il tipo esatto: un codice non valido e' ArgumentException, non una sua derivata.
            var fromParse = Assert.Throws<ArgumentException>(() => FolderCode.Parse(value));
            Assert.Equal("value", fromParse.ParamName);
            Assert.False(string.IsNullOrWhiteSpace(fromParse.Message));
            Assert.Equal("value", Assert.Throws<ArgumentException>(() => new FolderCode(value)).ParamName);
        }
    }

    [Fact]
    public void FolderCode_MaxLength_is_50()
    {
        Assert.Equal(50, FolderCode.MaxLength);
    }

    [Theory]
    [InlineData("A")]
    [InlineData("a")]
    [InlineData("0")]
    [InlineData("9")]
    [InlineData("FATTURE")]
    [InlineData("fatture")]
    [InlineData("Fatture-2026")]
    [InlineData("a.b")]
    [InlineData("a_b")]
    [InlineData("a-b")]
    [InlineData("A0_.-")]
    [InlineData("a-")] // i caratteri speciali sono ammessi anche in fondo
    [InlineData("a.")]
    [InlineData("a_")]
    [InlineData("a--")]
    [InlineData("a..b")]
    [InlineData("0abc")]
    [InlineData("2026.09-fatture_v2")]
    [InlineData("fld_01M3VEESG5KBYR5PYAJ0TDT4B2")] // id di cartella di master (30 caratteri): e' un codice valido
    [InlineData("fld_01m3veesg5kbyr5pyaj0tdt4b2")] // il codice non e' un id: il minuscolo qui e' lecito
    public void FolderCode_with_allowed_characters_is_accepted(string value)
    {
        AssertAccepted(value);
    }

    [Fact]
    public void FolderCode_of_1_and_50_characters_is_accepted()
    {
        AssertAccepted("a");
        AssertAccepted(new string('a', 50));
        AssertAccepted("a" + new string('-', 49));
        AssertAccepted(new string('Z', 49) + ".");
    }

    [Fact]
    public void FolderCode_of_0_and_51_characters_is_rejected()
    {
        AssertRejected(string.Empty);
        AssertRejected(new string('a', 51));
        AssertRejected(new string('a', 100));
    }

    [Theory]
    [InlineData("-a")] // il primo carattere deve essere alfanumerico
    [InlineData(".a")]
    [InlineData("_a")]
    [InlineData("-")]
    [InlineData(".")]
    [InlineData("_")]
    [InlineData("..")]
    [InlineData(" ")]
    [InlineData(" a")] // nessun trim: il server trimma gli input, il client no
    [InlineData("a ")]
    [InlineData("a b")]
    [InlineData("\ta")]
    [InlineData("a\tb")]
    [InlineData("a\0b")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a%20b")]
    [InlineData("a%2Fb")]
    [InlineData("a:b")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a#b")]
    [InlineData("a+b")]
    [InlineData("a@b")]
    [InlineData("a,b")]
    [InlineData("a;b")]
    [InlineData("a=b")]
    [InlineData("a~b")]
    [InlineData("a'b")]
    [InlineData("a\"b")]
    [InlineData("caff\U000000E8")] // e accentata
    [InlineData("\U000000E8a")]
    [InlineData("a\U000000E9b")]
    [InlineData("e\U00000301a")] // e + accento combinante
    [InlineData("a\U00000301")]
    [InlineData("\U00000661")] // cifra arabo-indica 1
    [InlineData("a\U00000661")]
    [InlineData("a\U000006F1")] // cifra persiana 1
    [InlineData("\U0000FF21")] // A a larghezza intera
    [InlineData("a\U0000FF41")]
    [InlineData("\U0000FF11a")] // cifra a larghezza intera
    [InlineData("\U0000212A")] // segno Kelvin: in minuscolo diventa 'k'
    [InlineData("a\U0000212A")]
    [InlineData("\U00000410")] // A cirillica
    [InlineData("a\U00000430")] // a cirillica
    [InlineData("a\U00002013b")] // trattino en
    [InlineData("a\U000000A0b")] // spazio non separabile
    [InlineData("a\U0000200B")] // spazio a larghezza zero
    [InlineData("a\U0001F600")] // emoji
    public void FolderCode_with_disallowed_characters_is_rejected(string value)
    {
        AssertRejected(value);
    }

    [Theory]
    [InlineData("a\n")] // il server lo accetta (il "$" della regex combacia prima di un newline finale): qui no
    [InlineData("a\r\n")]
    [InlineData("a\r")]
    [InlineData("a\n\n")]
    [InlineData("\na")]
    [InlineData("a\nb")]
    [InlineData("\n")]
    public void FolderCode_with_newline_is_rejected(string value)
    {
        AssertRejected(value);
    }

    [Fact]
    public void FolderCode_of_49_characters_plus_trailing_newline_is_rejected()
    {
        // 50 caratteri in tutto, l'ultimo e' '\n': l'unico input su cui client e server divergono (il server lo accetta).
        AssertRejected(new string('a', 49) + "\n");
    }

    [Fact]
    public void FolderCode_accepts_exactly_the_ascii_alphanumerics_as_first_character()
    {
        for (var code = 0; code <= char.MaxValue; code++)
        {
            var c = (char)code;

            Assert.True(
                FirstCharacters.Contains(c) == FolderCode.IsValid(c + "bc"),
                $"carattere U+{code:X4} in prima posizione");
        }
    }

    [Fact]
    public void FolderCode_accepts_exactly_alphanumerics_underscore_dot_and_hyphen_after_the_first_character()
    {
        for (var code = 0; code <= char.MaxValue; code++)
        {
            var c = (char)code;
            var expected = InnerCharacters.Contains(c);

            Assert.True(expected == FolderCode.IsValid("a" + c + "b"), $"carattere U+{code:X4} in posizione centrale");
            Assert.True(expected == FolderCode.IsValid("ab" + c), $"carattere U+{code:X4} in ultima posizione");
        }
    }

    [Fact]
    public void FolderCode_comparison_is_exact_and_case_sensitive()
    {
        var upper = new FolderCode("FATTURE");
        var lower = new FolderCode("fatture");

        Assert.NotEqual(upper, lower);
        Assert.False(upper.Equals((object)lower));
        Assert.Equal(upper, new FolderCode("FATTURE"));
        Assert.Equal(upper.GetHashCode(), new FolderCode("FATTURE").GetHashCode());
        Assert.Equal(2, new HashSet<FolderCode> { upper, lower }.Count);
    }

    [Fact]
    public void FolderCode_exposes_value_and_ToString()
    {
        var code = new FolderCode("Fatture-2026");

        Assert.Equal("Fatture-2026", code.Value);
        Assert.Equal("Fatture-2026", code.ToString());
        Assert.False(code.IsEmpty);
        Assert.False(code.Equals(null));
    }

    [Fact]
    public void FolderCode_default_is_the_empty_code_and_is_safe_to_use()
    {
        // default(struct) e' il codice "vuoto": Value stringa vuota (mai null), IsEmpty true, nessuna eccezione.
        // Non si puo' costruire con la API pubblica: "" non e' un codice valido.
        var empty = default(FolderCode);

        Assert.True(empty.IsEmpty);
        Assert.Equal(string.Empty, empty.Value);
        Assert.Equal(string.Empty, empty.ToString());
        Assert.Equal(default(FolderCode), empty);
        Assert.Equal(default(FolderCode).GetHashCode(), empty.GetHashCode());
        Assert.NotEqual(new FolderCode("a"), empty);
        Assert.False(empty.Equals((object)new FolderCode("a")));
    }

    [Fact]
    public void FolderCode_null_is_rejected_without_throwing_from_TryParse()
    {
        AssertRejected(null);
    }

    [Fact]
    public void FolderCode_Value_is_get_only_so_a_validated_code_cannot_be_overwritten()
    {
        // Un record posizionale darebbe a Value un setter init: "code with { Value = ... }" aggirerebbe la validazione.
        Assert.Null(typeof(FolderCode).GetProperty("Value")!.SetMethod);
    }

    [Fact]
    public void FolderCode_has_no_implicit_or_explicit_conversions()
    {
        Assert.DoesNotContain(typeof(FolderCode).GetMethods(), m => m.Name is "op_Implicit" or "op_Explicit");
    }
}
