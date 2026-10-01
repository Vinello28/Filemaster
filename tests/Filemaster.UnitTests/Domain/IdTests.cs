using Filemaster.Domain;

namespace Filemaster.UnitTests.Domain;

/// <summary>
/// Regole degli id tipizzati (DocumentId, ContactId, TenantId): prefisso + ULID canonico di 26 caratteri, esattamente
/// cio' che il server accetta (Ids.IsValid di Sharp-a-File: Ulid.TryParse + confronto con la forma canonica).
/// I casi di validazione girano su tutti e tre i tipi tramite gli adattatori di <see cref="Kinds"/>.
/// </summary>
public sealed class IdTests
{
    // Esempio della specifica ULID e id reale catturato dal server dev (fixture, non segreto).
    private const string SpecUlid = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private const string FixtureUlid = "01M3VEESG5KBYR5PYAJ0TDT4B2";

    // Alfabeto Crockford maiuscolo: niente I, L, O, U. Il primo carattere del ULID arriva solo a '7' (130 bit di
    // base32 per 128 di valore: '8' o oltre e' overflow, e il server lo scarta perche' non e' la forma canonica).
    private static readonly HashSet<char> Alphabet = new("0123456789ABCDEFGHJKMNPQRSTVWXYZ");
    private static readonly HashSet<char> FirstCharAlphabet = new("01234567");

    private delegate bool TryParseFunc<TId>(string? value, out TId id);

    private sealed record IdKind(
        string Name,
        string Prefix,
        Func<string?, bool> IsValid,
        Func<string?, (bool Ok, object Id)> TryParse,
        Func<string, object> Parse,
        Func<string, object> Construct,
        object Default);

    private static IdKind Kind<TId>(
        string prefix,
        Func<string?, bool> isValid,
        TryParseFunc<TId> tryParse,
        Func<string, TId> parse,
        Func<string, TId> construct)
        where TId : struct =>
        new(
            typeof(TId).Name,
            prefix,
            isValid,
            value =>
            {
                var ok = tryParse(value, out var id);
                return (ok, id);
            },
            value => parse(value),
            value => construct(value),
            default(TId));

    private static readonly IdKind[] Kinds =
    {
        Kind<DocumentId>("doc_", DocumentId.IsValid, DocumentId.TryParse, DocumentId.Parse, value => new DocumentId(value)),
        Kind<ContactId>("con_", ContactId.IsValid, ContactId.TryParse, ContactId.Parse, value => new ContactId(value)),
        Kind<TenantId>("ten_", TenantId.IsValid, TenantId.TryParse, TenantId.Parse, value => new TenantId(value)),
    };

    private static void AssertAccepted(IdKind kind, string value)
    {
        Assert.True(kind.IsValid(value), $"{kind.Name}.IsValid(\"{value}\") doveva essere true");

        var (ok, parsed) = kind.TryParse(value);
        Assert.True(ok, $"{kind.Name}.TryParse(\"{value}\") doveva riuscire");
        Assert.Equal(value, parsed.ToString());
        Assert.NotEqual(kind.Default, parsed);
        Assert.Equal(parsed, kind.Parse(value));
        Assert.Equal(parsed, kind.Construct(value));
    }

    private static void AssertRejected(IdKind kind, string? value)
    {
        Assert.False(kind.IsValid(value), $"{kind.Name}.IsValid(\"{value}\") doveva essere false");

        var (ok, id) = kind.TryParse(value);
        Assert.False(ok, $"{kind.Name}.TryParse(\"{value}\") doveva fallire");
        Assert.Equal(kind.Default, id);

        if (value is null)
        {
            Assert.Equal("value", Assert.Throws<ArgumentNullException>(() => kind.Parse(null!)).ParamName);
            Assert.Equal("value", Assert.Throws<ArgumentNullException>(() => kind.Construct(null!)).ParamName);
        }
        else
        {
            // Throws<T> vuole il tipo esatto: un id non valido e' ArgumentException, non una sua derivata.
            var fromParse = Assert.Throws<ArgumentException>(() => kind.Parse(value));
            Assert.Equal("value", fromParse.ParamName);
            Assert.False(string.IsNullOrWhiteSpace(fromParse.Message));
            Assert.Equal("value", Assert.Throws<ArgumentException>(() => kind.Construct(value)).ParamName);
        }
    }

    [Theory]
    [InlineData(SpecUlid)]
    [InlineData(FixtureUlid)]
    [InlineData("00000000000000000000000000")] // minimo
    [InlineData("7ZZZZZZZZZZZZZZZZZZZZZZZZZ")] // massimo: il primo carattere arriva a '7'
    [InlineData("70000000000000000000000000")]
    [InlineData("70123456789ABCDEFGHJKMNPQR")] // tutto l'alfabeto, prima parte
    [InlineData("0STVWXYZ0123456789ABCDEFGH")] // e il resto
    public void Id_with_canonical_ulid_is_accepted(string ulid)
    {
        foreach (var kind in Kinds)
        {
            AssertAccepted(kind, kind.Prefix + ulid);
        }
    }

    [Theory]
    [InlineData("")] // prefisso senza ULID
    [InlineData("01ARZ3NDEKTSV4RRFFQ69G5FA")] // 25 caratteri
    [InlineData("01ARZ3NDEKTSV4RRFFQ69G5FAV0")] // 27 caratteri
    [InlineData("01ARZ3NDEKTSV4RRFFQ69G5F")]
    [InlineData("01arz3ndektsv4rrffq69g5fav")] // minuscolo: il server risponde 404
    [InlineData("01ARZ3NDEKTSV4RRFFQ69G5FAv")] // una sola cifra minuscola, in fondo
    [InlineData("01ARZ3NDEKTSV4RRFFQ69g5FAV")] // ... e in mezzo
    [InlineData("0IARZ3NDEKTSV4RRFFQ69G5FAV")] // I, L, O, U non sono nell'alfabeto
    [InlineData("0LARZ3NDEKTSV4RRFFQ69G5FAV")]
    [InlineData("0OARZ3NDEKTSV4RRFFQ69G5FAV")]
    [InlineData("0UARZ3NDEKTSV4RRFFQ69G5FAV")]
    [InlineData("01ARZ3NDEKTSV4RRFFQ69G5FAU")]
    [InlineData("01ARZ3NDEKTSV4RRFFQ69G5FA-")]
    [InlineData("01ARZ3NDEKTSV4RRFFQ_9G5FAV")]
    [InlineData("01ARZ3NDEKTSV4RRFFQ 9G5FAV")]
    [InlineData("01ARZ3NDEKTSV4RRFFQ69G5FAV\n")] // newline finale: con una regex "$" passerebbe
    [InlineData("01ARZ3NDEKTSV4RRFFQ69G5FA\n")] // newline al posto dell'ultimo carattere (26 in tutto)
    [InlineData("01ARZ3NDEKTSV4RRFFQ69G5FAV\r\n")]
    [InlineData("01ARZ3NDEKTSV4RRFFQ69G5FAV ")]
    [InlineData("01ARZ3NDEKTSV4RRFFQ69G5FAV\t")]
    [InlineData(" 01ARZ3NDEKTSV4RRFFQ69G5FAV")]
    [InlineData("\n01ARZ3NDEKTSV4RRFFQ69G5FAV")]
    [InlineData("01ARZ3NDEKTSV4RRFFQ69G5FA\0")]
    [InlineData("0\U00000661ARZ3NDEKTSV4RRFFQ69G5FAV")] // cifra arabo-indica 1 al posto di '1'
    [InlineData("\U00000660" + "1ARZ3NDEKTSV4RRFFQ69G5FAV")] // cifra arabo-indica 0 in testa
    [InlineData("0\U000006F1ARZ3NDEKTSV4RRFFQ69G5FAV")] // cifra persiana 1
    [InlineData("0\U0000FF11ARZ3NDEKTSV4RRFFQ69G5FAV")] // cifra a larghezza intera
    [InlineData("01\U0000FF21RZ3NDEKTSV4RRFFQ69G5FAV")] // lettera a larghezza intera
    [InlineData("01\U00000410RZ3NDEKTSV4RRFFQ69G5FAV")] // A cirillica
    [InlineData("01ARZ3ND\U000000C9KTSV4RRFFQ69G5FAV")] // E accentata maiuscola
    [InlineData("01ARZ3NDE\U00000301KTSV4RRFFQ69G5FAV")] // E + accento combinante (27 caratteri)
    [InlineData("01ARZ3ND\U000000E9KTSV4RRFFQ69G5FAV")] // e accentata minuscola
    [InlineData("01ARZ3NDE\U0000212ATSV4RRFFQ69G5FAV")] // segno Kelvin, che in minuscolo diventa 'k'
    [InlineData("01ARZ3NDEKTSV4RRFFQ69G5F\U0001F600")] // emoji (coppia surrogata, 2 unita')
    public void Id_with_malformed_ulid_is_rejected(string ulid)
    {
        foreach (var kind in Kinds)
        {
            AssertRejected(kind, kind.Prefix + ulid);
        }
    }

    [Theory]
    [InlineData('8')] // 8 e oltre: overflow dei 128 bit, ma Ulid.TryParse non lo rifiuta da solo
    [InlineData('9')]
    [InlineData('A')]
    [InlineData('H')]
    [InlineData('Z')]
    public void Id_with_first_ulid_character_above_7_is_rejected(char first)
    {
        foreach (var kind in Kinds)
        {
            AssertRejected(kind, kind.Prefix + first + SpecUlid.Substring(1));
            AssertRejected(kind, kind.Prefix + first + new string('0', 25));
        }
    }

    [Fact]
    public void Id_accepts_exactly_the_crockford_upper_alphabet_after_the_first_character()
    {
        // Ogni carattere dell'intero BMP in seconda posizione: accettato se e solo se e' nell'alfabeto.
        foreach (var kind in Kinds)
        {
            for (var code = 0; code <= char.MaxValue; code++)
            {
                var c = (char)code;
                var value = kind.Prefix + "0" + c + new string('0', 24);

                Assert.True(
                    Alphabet.Contains(c) == kind.IsValid(value),
                    $"{kind.Name}: carattere U+{code:X4} in seconda posizione");
            }
        }
    }

    [Fact]
    public void Id_accepts_exactly_the_digits_0_to_7_as_first_ulid_character()
    {
        foreach (var kind in Kinds)
        {
            for (var code = 0; code <= char.MaxValue; code++)
            {
                var c = (char)code;
                var value = kind.Prefix + c + new string('0', 25);

                Assert.True(
                    FirstCharAlphabet.Contains(c) == kind.IsValid(value),
                    $"{kind.Name}: carattere U+{code:X4} in prima posizione");
            }
        }
    }

    [Fact]
    public void Id_without_prefix_is_rejected()
    {
        foreach (var kind in Kinds)
        {
            AssertRejected(kind, SpecUlid);
            AssertRejected(kind, kind.Prefix.TrimEnd('_') + SpecUlid); // manca il carattere '_'
        }
    }

    [Fact]
    public void Id_with_the_prefix_of_another_resource_is_rejected()
    {
        // Gli id sono tipizzati: un id di contatto non e' un id di documento. fld_/key_/acc_/wh_/whd_ sono prefissi
        // reali del server (cartelle di master, chiavi, account, webhook).
        foreach (var kind in Kinds)
        {
            foreach (var other in new[] { "doc_", "con_", "ten_", "fld_", "key_", "acc_", "wh_", "whd_" })
            {
                if (other != kind.Prefix)
                {
                    AssertRejected(kind, other + SpecUlid);
                }
            }
        }
    }

    [Fact]
    public void Id_with_malformed_prefix_is_rejected()
    {
        foreach (var kind in Kinds)
        {
            var p = kind.Prefix;
            var variants = new[]
            {
                p.ToUpperInvariant(), // DOC_
                char.ToUpperInvariant(p[0]) + p.Substring(1), // Doc_
                p.Replace('_', '-'),
                p.Substring(0, p.Length - 1), // doc
                " " + p,
                p + p,
                string.Empty,
            };

            foreach (var variant in variants)
            {
                AssertRejected(kind, variant + SpecUlid);
            }

            AssertRejected(kind, p); // solo il prefisso
        }
    }

    [Theory]
    [InlineData(" ", "")]
    [InlineData("", " ")]
    [InlineData("\t", "")]
    [InlineData("", "\t")]
    [InlineData("\n", "")]
    [InlineData("", "\n")]
    [InlineData("", "\r\n")]
    [InlineData("", "\n\n")]
    public void Id_surrounded_by_whitespace_is_rejected_without_trimming(string before, string after)
    {
        foreach (var kind in Kinds)
        {
            AssertRejected(kind, before + kind.Prefix + SpecUlid + after);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\n")]
    public void Id_null_or_blank_is_rejected_without_throwing_from_TryParse(string? value)
    {
        foreach (var kind in Kinds)
        {
            AssertRejected(kind, value);
        }
    }

    [Fact]
    public void DocumentId_exposes_value_and_exact_equality()
    {
        AssertValueSemantics("doc_" + SpecUlid, "doc_" + FixtureUlid, value => new DocumentId(value), id => id.Value, id => id.IsEmpty);
    }

    [Fact]
    public void ContactId_exposes_value_and_exact_equality()
    {
        AssertValueSemantics("con_" + SpecUlid, "con_" + FixtureUlid, value => new ContactId(value), id => id.Value, id => id.IsEmpty);
    }

    [Fact]
    public void TenantId_exposes_value_and_exact_equality()
    {
        AssertValueSemantics("ten_" + SpecUlid, "ten_" + FixtureUlid, value => new TenantId(value), id => id.Value, id => id.IsEmpty);
    }

    [Fact]
    public void Id_Value_is_get_only_so_a_validated_id_cannot_be_overwritten()
    {
        // Un record posizionale darebbe a Value un setter init: "id with { Value = ... }" aggirerebbe la validazione.
        foreach (var type in new[] { typeof(DocumentId), typeof(ContactId), typeof(TenantId) })
        {
            Assert.Null(type.GetProperty("Value")!.SetMethod);
        }
    }

    [Fact]
    public void Id_has_no_implicit_or_explicit_conversions()
    {
        // Nessuna conversione implicita da/verso string: l'id si costruisce solo con ctor/Parse/TryParse.
        foreach (var type in new[] { typeof(DocumentId), typeof(ContactId), typeof(TenantId) })
        {
            Assert.DoesNotContain(type.GetMethods(), m => m.Name is "op_Implicit" or "op_Explicit");
        }
    }

    private static void AssertValueSemantics<TId>(
        string text,
        string otherText,
        Func<string, TId> create,
        Func<TId, string> value,
        Func<TId, bool> isEmpty)
        where TId : struct
    {
        var first = create(text);
        var same = create(text);
        var other = create(otherText);
        var empty = default(TId);

        Assert.Equal(text, value(first));
        Assert.Equal(text, first.ToString());
        Assert.False(isEmpty(first));

        Assert.Equal(first, same);
        Assert.True(first.Equals((object)same));
        Assert.Equal(first.GetHashCode(), same.GetHashCode());
        Assert.NotEqual(first, other);
        Assert.False(first.Equals(null));
        Assert.Contains(same, new HashSet<TId> { first });

        // default: vuoto, sicuro da usare, diverso da ogni id valido, uguale a un altro default.
        Assert.True(isEmpty(empty));
        Assert.Equal(string.Empty, value(empty));
        Assert.Equal(string.Empty, empty.ToString());
        Assert.Equal(default(TId), empty);
        Assert.Equal(default(TId).GetHashCode(), empty.GetHashCode());
        Assert.NotEqual(first, empty);
        Assert.False(first.Equals((object)empty));
    }
}
