using System.Globalization;
using System.Reflection;
using Filemaster.Domain;

namespace Filemaster.UnitTests.Domain;

/// <summary>
/// Regole degli id tipizzati (DocumentId, ContactId, TenantId): interi positivi in decimale canonico, esattamente cio'
/// che il server accetta (Ids.TryParse di Sharp-a-File: NumberStyles.None, valore maggiore di zero, la stringa riletta
/// dal numero uguale all'originale; tutto il resto e' un 404). Documento: da 1 a long.MaxValue; contatto ed ente: da 1 a
/// int.MaxValue. I casi di validazione girano su tutti e tre i tipi tramite gli adattatori di <see cref="Kinds"/>.
/// </summary>
public sealed class IdTests
{
    private const string IntMax = "2147483647";
    private const string LongMax = "9223372036854775807";
    private const string IntMaxPrefix = "214748364"; // IntMax senza l'ultima cifra
    private const string LongMaxPrefix = "922337203685477580"; // LongMax senza l'ultima cifra

    private delegate bool TryParseFunc<TId>(string? value, out TId id);

    private sealed record IdKind(
        string Name,
        string Max,
        Func<string?, bool> IsValid,
        Func<string?, (bool Ok, object Id)> TryParse,
        Func<string, object> Parse,
        Func<string, object> Construct,
        Func<long, object> From,
        Func<object, long> Number,
        Func<object, string> Value,
        Func<object, bool> IsEmpty,
        object Default);

    private static IdKind Kind<TId>(
        string max,
        Func<string?, bool> isValid,
        TryParseFunc<TId> tryParse,
        Func<string, TId> parse,
        Func<string, TId> construct,
        Func<long, TId> from,
        Func<TId, long> number,
        Func<TId, string> value,
        Func<TId, bool> isEmpty)
        where TId : struct =>
        new(
            typeof(TId).Name,
            max,
            isValid,
            text =>
            {
                var ok = tryParse(text, out var id);
                return (ok, id);
            },
            text => parse(text),
            text => construct(text),
            n => from(n),
            id => number((TId)id),
            id => value((TId)id),
            id => isEmpty((TId)id),
            default(TId));

    // Il documento e' bigint, contatto ed ente sono int: DocumentKind accetta fino a long.MaxValue, gli altri fino a int.MaxValue.
    private static readonly IdKind DocumentKind = Kind<DocumentId>(
        LongMax, DocumentId.IsValid, DocumentId.TryParse, DocumentId.Parse, text => new DocumentId(text), DocumentId.From, id => id.Number, id => id.Value, id => id.IsEmpty);

    private static readonly IdKind ContactKind = Kind<ContactId>(
        IntMax, ContactId.IsValid, ContactId.TryParse, ContactId.Parse, text => new ContactId(text), n => ContactId.From(checked((int)n)), id => id.Number, id => id.Value, id => id.IsEmpty);

    private static readonly IdKind TenantKind = Kind<TenantId>(
        IntMax, TenantId.IsValid, TenantId.TryParse, TenantId.Parse, text => new TenantId(text), n => TenantId.From(checked((int)n)), id => id.Number, id => id.Value, id => id.IsEmpty);

    private static readonly IdKind[] Kinds = { DocumentKind, ContactKind, TenantKind };

    private static readonly IdKind[] IntKinds = { ContactKind, TenantKind };

    private static readonly long[] NonPositive = { 0L, -1L, -42L, int.MinValue };

    private static readonly Type[] IdTypes = { typeof(DocumentId), typeof(ContactId), typeof(TenantId) };

    private static void AssertAccepted(IdKind kind, string text)
    {
        Assert.True(kind.IsValid(text), $"{kind.Name}.IsValid(\"{text}\") doveva essere true");

        var (ok, parsed) = kind.TryParse(text);
        Assert.True(ok, $"{kind.Name}.TryParse(\"{text}\") doveva riuscire");
        Assert.Equal(text, parsed.ToString());
        Assert.Equal(text, kind.Value(parsed));
        Assert.Equal(long.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture), kind.Number(parsed));
        Assert.False(kind.IsEmpty(parsed));
        Assert.NotEqual(kind.Default, parsed);
        Assert.Equal(parsed, kind.Parse(text));
        Assert.Equal(parsed, kind.Construct(text));
    }

    private static void AssertRejected(IdKind kind, string? text)
    {
        Assert.False(kind.IsValid(text), $"{kind.Name}.IsValid(\"{text}\") doveva essere false");

        var (ok, id) = kind.TryParse(text);
        Assert.False(ok, $"{kind.Name}.TryParse(\"{text}\") doveva fallire");
        Assert.Equal(kind.Default, id);

        if (text is null)
        {
            Assert.Equal("value", Assert.Throws<ArgumentNullException>(() => kind.Parse(null!)).ParamName);
            Assert.Equal("value", Assert.Throws<ArgumentNullException>(() => kind.Construct(null!)).ParamName);
        }
        else
        {
            // Throws<T> vuole il tipo esatto: un id non valido e' ArgumentException, non una sua derivata.
            var fromParse = Assert.Throws<ArgumentException>(() => kind.Parse(text));
            Assert.Equal("value", fromParse.ParamName);
            Assert.False(string.IsNullOrWhiteSpace(fromParse.Message));
            Assert.Equal("value", Assert.Throws<ArgumentException>(() => kind.Construct(text)).ParamName);
        }
    }

    [Fact]
    public void The_limits_used_by_the_tests_are_the_real_ones()
    {
        // Le costanti sono testo (servono agli InlineData): qui si prova che sono davvero int.MaxValue e long.MaxValue.
        Assert.Equal(IntMax, int.MaxValue.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(LongMax, long.MaxValue.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(IntMax, IntMaxPrefix + "7");
        Assert.Equal(LongMax, LongMaxPrefix + "7");
    }

    [Theory]
    [InlineData("1")] // minimo
    [InlineData("2")]
    [InlineData("9")]
    [InlineData("10")] // il primo con due cifre: lo zero dentro il numero e' ammesso
    [InlineData("11")]
    [InlineData("42")]
    [InlineData("99")]
    [InlineData("100")]
    [InlineData("101")]
    [InlineData("123456789")]
    [InlineData("1000000000")]
    [InlineData("2147483646")] // int.MaxValue - 1
    [InlineData(IntMax)] // massimo di contatto ed ente
    public void Id_with_canonical_number_is_accepted_by_every_type(string text)
    {
        foreach (var kind in Kinds)
        {
            AssertAccepted(kind, text);
        }
    }

    [Theory]
    [InlineData("2147483648")] // int.MaxValue + 1
    [InlineData("2147483649")]
    [InlineData("2147483650")]
    [InlineData("2147483657")]
    [InlineData("2147483700")]
    [InlineData("2200000000")]
    [InlineData("3000000000")]
    [InlineData("4294967295")] // uint.MaxValue
    [InlineData("4294967296")] // 2^32: troncato a 32 bit darebbe 0
    [InlineData("4294967297")] // 2^32 + 1: troncato a 32 bit darebbe 1
    [InlineData("5000000042")]
    [InlineData("9999999999")]
    [InlineData("10000000000")]
    [InlineData("9223372036854775806")] // long.MaxValue - 1
    [InlineData(LongMax)]
    public void Id_above_int_max_is_accepted_only_by_DocumentId(string text)
    {
        AssertAccepted(DocumentKind, text);

        foreach (var kind in IntKinds)
        {
            AssertRejected(kind, text);
        }
    }

    [Theory]
    [InlineData("9223372036854775808")] // long.MaxValue + 1
    [InlineData("9223372036854775809")]
    [InlineData("9223372036854775810")] // cambia la penultima cifra, non l'ultima
    [InlineData("9223372036854775817")]
    [InlineData("9223372036854775900")]
    [InlineData("9223372036854776000")]
    [InlineData("9223372036854780000")]
    [InlineData("9300000000000000000")]
    [InlineData("9999999999999999999")] // 19 cifre
    [InlineData("10000000000000000000")] // 20 cifre
    [InlineData("18446744073709551615")] // ulong.MaxValue
    [InlineData("18446744073709551616")] // 2^64: troncato a 64 bit darebbe 0
    [InlineData("18446744073709551617")] // 2^64 + 1: troncato a 64 bit darebbe 1
    [InlineData("92233720368547758070")] // long.MaxValue seguito da uno 0
    [InlineData("99999999999999999999")]
    [InlineData("1000000000000000000000000")] // 25 cifre
    [InlineData("9999999999999999999999999")]
    [InlineData("1111111111111111111111111111111111111111")] // 40 cifre
    public void Id_above_long_max_is_rejected_by_every_type(string text)
    {
        foreach (var kind in Kinds)
        {
            AssertRejected(kind, text);
        }
    }

    [Fact]
    public void Id_limit_depends_on_the_last_digit_only_when_the_prefix_is_exactly_the_limit_prefix()
    {
        // Il confronto di overflow guarda anche l'ultima cifra: ...7 sta nel tipo, ...8 e ...9 no, ...0 e ...6 si'.
        for (var digit = 0; digit <= 9; digit++)
        {
            var suffix = digit.ToString(CultureInfo.InvariantCulture);
            var intText = IntMaxPrefix + suffix;
            var longText = LongMaxPrefix + suffix;

            foreach (var kind in IntKinds)
            {
                if (digit <= 7)
                {
                    AssertAccepted(kind, intText);
                }
                else
                {
                    AssertRejected(kind, intText);
                }

                AssertRejected(kind, longText); // sempre oltre int.MaxValue
            }

            AssertAccepted(DocumentKind, intText); // oltre int.MaxValue o no, sempre dentro long
            if (digit <= 7)
            {
                AssertAccepted(DocumentKind, longText);
            }
            else
            {
                AssertRejected(DocumentKind, longText);
            }
        }
    }

    [Theory]
    [InlineData("")] // vuota
    [InlineData(" ")]
    [InlineData("0")] // il minimo e' 1
    [InlineData("00")]
    [InlineData("000")]
    [InlineData("01")] // zero iniziale: il server rilegge "1" e non coincide
    [InlineData("007")]
    [InlineData("042")]
    [InlineData("0001")]
    [InlineData("00000000000000000001")]
    [InlineData("02147483647")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData(" 1 ")]
    [InlineData("\t1")]
    [InlineData("1\t")]
    [InlineData("\n1")]
    [InlineData("1\n")] // newline finale: con una regex "$" passerebbe
    [InlineData("1\r\n")]
    [InlineData("42\n")]
    [InlineData("1 0")]
    [InlineData("1\0")]
    [InlineData("\0")]
    [InlineData("+1")]
    [InlineData("+42")]
    [InlineData("+0")]
    [InlineData("-1")]
    [InlineData("-42")]
    [InlineData("-0")]
    [InlineData("--1")]
    [InlineData("1-")]
    [InlineData("(1)")]
    [InlineData("1.0")]
    [InlineData("1.")]
    [InlineData(".5")]
    [InlineData("0.5")]
    [InlineData("1e3")]
    [InlineData("1E3")]
    [InlineData("1,0")]
    [InlineData("1,000")]
    [InlineData("1_000")]
    [InlineData("0x1")]
    [InlineData("0x10")]
    [InlineData("1/2")]
    [InlineData("abc")]
    [InlineData("1a")]
    [InlineData("a1")]
    [InlineData("NaN")]
    [InlineData("\U00000661")] // cifra arabo-indica 1 (char.IsDigit la accetterebbe)
    [InlineData("\U00000661\U00000662\U00000663")]
    [InlineData("1\U00000661")]
    [InlineData("\U00000661" + "1")]
    [InlineData("\U00000660")] // cifra arabo-indica 0
    [InlineData("\U000006F1")] // cifra persiana 1
    [InlineData("\U0000FF11")] // cifra a larghezza intera 1
    [InlineData("\U0000FF11\U0000FF12")]
    [InlineData("4\U0000FF12")]
    [InlineData("\U00000967")] // cifra devanagari 7
    [InlineData("42\U0001F600")] // emoji (coppia surrogata)
    [InlineData("doc_01ARZ3NDEKTSV4RRFFQ69G5FAV")] // vecchi id con prefisso e ULID
    [InlineData("con_01ARZ3NDEKTSV4RRFFQ69G5FAV")]
    [InlineData("ten_01ARZ3NDEKTSV4RRFFQ69G5FAV")]
    [InlineData("01ARZ3NDEKTSV4RRFFQ69G5FAV")]
    [InlineData("doc_42")]
    [InlineData("doc_1")]
    public void Id_not_in_canonical_decimal_form_is_rejected_by_every_type(string text)
    {
        foreach (var kind in Kinds)
        {
            AssertRejected(kind, text);
        }
    }

    [Fact]
    public void Id_null_is_rejected_without_throwing_from_TryParse_or_IsValid()
    {
        foreach (var kind in Kinds)
        {
            AssertRejected(kind, null);
        }
    }

    [Fact]
    public void Id_accepts_exactly_the_ascii_digits_0_to_9_in_every_position()
    {
        // Ogni carattere dell'intero BMP in seconda, prima e ultima posizione: accettato se e solo se e' una cifra ASCII
        // (in prima posizione solo 1..9, perche' uno zero iniziale non e' canonico).
        foreach (var kind in Kinds)
        {
            for (var code = 0; code <= char.MaxValue; code++)
            {
                var c = (char)code;
                var isDigit = c >= '0' && c <= '9';

                Assert.True(isDigit == kind.IsValid("1" + c), $"{kind.Name}: carattere U+{code:X4} in seconda posizione");
                Assert.True(isDigit == kind.IsValid("1" + c + "1"), $"{kind.Name}: carattere U+{code:X4} in mezzo");
                Assert.True((isDigit && c != '0') == kind.IsValid(c + "1"), $"{kind.Name}: carattere U+{code:X4} in prima posizione");
                Assert.True((isDigit && c != '0') == kind.IsValid(c.ToString()), $"{kind.Name}: carattere U+{code:X4} da solo");
            }
        }
    }

    [Fact]
    public void Id_validity_matches_an_independent_decimal_oracle_on_random_digit_strings()
    {
        // Stringhe di cifre di 1..27 lunghe, con molte cifre alte e blocchi di 9: valide se la prima cifra non e' 0 e il
        // valore (decimal, 28 cifre) non supera il massimo del tipo. Seme fisso: lo stesso insieme a ogni corsa.
        var random = new Random(20261009);
        var buffer = new char[27];
        foreach (var kind in Kinds)
        {
            var max = decimal.Parse(kind.Max, NumberStyles.None, CultureInfo.InvariantCulture);
            for (var round = 0; round < 20000; round++)
            {
                var length = random.Next(1, buffer.Length + 1);
                for (var i = 0; i < length; i++)
                {
                    buffer[i] = (char)('0' + (random.Next(3) == 0 ? 9 : random.Next(10)));
                }

                var text = new string(buffer, 0, length);
                var expected = text[0] != '0'
                    && decimal.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture) <= max;

                Assert.True(expected == kind.IsValid(text), $"{kind.Name}: \"{text}\" atteso {expected}");
                Assert.Equal(expected, kind.TryParse(text).Ok);
            }
        }
    }

    [Fact]
    public void Id_text_round_trips_through_Parse_Value_and_ToString()
    {
        foreach (var kind in Kinds)
        {
            foreach (var text in new[] { "1", "9", "10", "42", "1000", IntMax })
            {
                var id = kind.Parse(text);
                Assert.Equal(text, kind.Value(id));
                Assert.Equal(text, id.ToString());
                Assert.Equal(id, kind.Parse(kind.Value(id)));
                Assert.Equal(long.Parse(text, CultureInfo.InvariantCulture), kind.Number(id));
            }
        }

        Assert.Equal(LongMax, DocumentId.Parse(LongMax).Value);
        Assert.Equal(long.MaxValue, DocumentId.Parse(LongMax).Number);
    }

    [Fact]
    public void Id_Value_is_written_with_the_invariant_culture()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            // Una cultura con cifre o separatori diversi non deve cambiare il testo canonico.
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            Assert.Equal("1234567", ContactId.From(1234567).Value);
            Assert.Equal("1234567", TenantId.From(1234567).ToString());
            Assert.Equal("5000000042", DocumentId.From(5000000042L).Value);
            Assert.True(DocumentId.IsValid("1234567"));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void Id_From_creates_the_same_id_as_Parse()
    {
        foreach (var kind in Kinds)
        {
            foreach (var number in new long[] { 1, 2, 9, 10, 42, 1000, int.MaxValue })
            {
                var created = kind.From(number);
                Assert.Equal(kind.Parse(number.ToString(CultureInfo.InvariantCulture)), created);
                Assert.Equal(number, kind.Number(created));
                Assert.Equal(number.ToString(CultureInfo.InvariantCulture), kind.Value(created));
                Assert.False(kind.IsEmpty(created));
            }
        }

        var big = DocumentId.From(int.MaxValue + 1L);
        Assert.Equal("2147483648", big.Value);
        Assert.Equal(2147483648L, big.Number);
        Assert.Equal(DocumentId.Parse(LongMax), DocumentId.From(long.MaxValue));
    }

    [Fact]
    public void Id_From_rejects_zero_and_negative_numbers()
    {
        foreach (var kind in Kinds)
        {
            foreach (var number in NonPositive)
            {
                var error = Assert.Throws<ArgumentOutOfRangeException>(() => kind.From(number));
                Assert.Equal("number", error.ParamName);
                Assert.False(string.IsNullOrWhiteSpace(error.Message));
            }
        }

        Assert.Equal("number", Assert.Throws<ArgumentOutOfRangeException>(() => DocumentId.From(long.MinValue)).ParamName);
        Assert.Equal("number", Assert.Throws<ArgumentOutOfRangeException>(() => ContactId.From(int.MinValue)).ParamName);
        Assert.Equal("number", Assert.Throws<ArgumentOutOfRangeException>(() => TenantId.From(int.MinValue)).ParamName);
    }

    [Fact]
    public void Id_default_is_empty_invalid_and_safe_to_use()
    {
        foreach (var kind in Kinds)
        {
            var empty = kind.Default;

            Assert.True(kind.IsEmpty(empty));
            Assert.Equal(0L, kind.Number(empty));
            Assert.Equal(string.Empty, kind.Value(empty));
            Assert.Equal(string.Empty, empty.ToString());
            Assert.Equal(empty, kind.Default);
            Assert.Equal(empty.GetHashCode(), kind.Default.GetHashCode());
            Assert.NotEqual(kind.From(1), empty);

            // L'id vuoto non e' un id valido: ne' la stringa vuota ne' "0" lo producono.
            AssertRejected(kind, string.Empty);
            AssertRejected(kind, "0");
        }
    }

    [Fact]
    public void Id_equality_and_hash_are_by_number()
    {
        foreach (var kind in Kinds)
        {
            var first = kind.Parse("42");
            var same = kind.Construct("42");
            var other = kind.Parse("43");

            Assert.Equal(first, same);
            Assert.True(first.Equals(same));
            Assert.Equal(first.GetHashCode(), same.GetHashCode());
            Assert.NotEqual(first, other);
            Assert.False(first.Equals(other));
            Assert.False(first.Equals(null));
            Assert.False(first.Equals(kind.Default));
        }

        // Stesso numero, tipo diverso: due id di tipo diverso non sono mai uguali.
        Assert.False(((object)DocumentId.From(42)).Equals(ContactId.From(42)));
        Assert.False(((object)ContactId.From(42)).Equals(TenantId.From(42)));
        Assert.Contains(DocumentId.From(42), new HashSet<DocumentId> { DocumentId.Parse("42") });
        Assert.DoesNotContain(DocumentId.From(43), new HashSet<DocumentId> { DocumentId.Parse("42") });
        Assert.Contains(ContactId.From(42), new HashSet<ContactId> { ContactId.Parse("42") });
        Assert.Contains(TenantId.From(42), new HashSet<TenantId> { TenantId.Parse("42") });
    }

    [Fact]
    public void Id_Value_and_Number_are_get_only_so_a_validated_id_cannot_be_overwritten()
    {
        // Un record posizionale darebbe a Value un setter init: "id with { Value = ... }" aggirerebbe la validazione.
        foreach (var type in IdTypes)
        {
            Assert.Null(type.GetProperty("Value")!.SetMethod);
            Assert.Null(type.GetProperty("Number")!.SetMethod);
            Assert.Null(type.GetProperty("IsEmpty")!.SetMethod);
        }
    }

    [Fact]
    public void Id_has_one_string_constructor_and_a_numeric_Number_of_the_right_width()
    {
        foreach (var type in IdTypes)
        {
            // Un solo costruttore pubblico (quello con la stringa): il numero entra da From, non da un secondo costruttore.
            var constructor = Assert.Single(type.GetConstructors());
            var parameter = Assert.Single(constructor.GetParameters());
            Assert.Equal(typeof(string), parameter.ParameterType);
            Assert.Equal("value", parameter.Name);
            Assert.Equal(typeof(string), type.GetProperty("Value")!.PropertyType);
        }

        Assert.Equal(typeof(long), typeof(DocumentId).GetProperty("Number")!.PropertyType);
        Assert.Equal(typeof(int), typeof(ContactId).GetProperty("Number")!.PropertyType);
        Assert.Equal(typeof(int), typeof(TenantId).GetProperty("Number")!.PropertyType);

        Assert.Equal(typeof(long), Assert.Single(typeof(DocumentId).GetMethods(BindingFlags.Public | BindingFlags.Static), m => m.Name == "From").GetParameters()[0].ParameterType);
        Assert.Equal(typeof(int), Assert.Single(typeof(ContactId).GetMethods(BindingFlags.Public | BindingFlags.Static), m => m.Name == "From").GetParameters()[0].ParameterType);
        Assert.Equal(typeof(int), Assert.Single(typeof(TenantId).GetMethods(BindingFlags.Public | BindingFlags.Static), m => m.Name == "From").GetParameters()[0].ParameterType);
    }

    [Fact]
    public void Id_has_no_implicit_or_explicit_conversions()
    {
        // Nessuna conversione implicita da/verso string o numeri: l'id si costruisce solo con ctor/Parse/TryParse/From.
        foreach (var type in IdTypes)
        {
            Assert.DoesNotContain(type.GetMethods(), m => m.Name is "op_Implicit" or "op_Explicit");
        }
    }
}
