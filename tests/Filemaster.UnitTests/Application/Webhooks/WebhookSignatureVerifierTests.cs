using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Filemaster.Application;
using Microsoft.Extensions.Time.Testing;

// I test girano anche su net48 (solo Windows), dove non esistono le API generiche e statiche che questi analizzatori
// suggerirebbero (Enum.GetValues<T>, SHA256.HashData): qui si usano le forme che esistono su tutti i TFM di test.
#pragma warning disable CA1850, CA2263

namespace Filemaster.UnitTests.Application.Webhooks;

/// <summary>
/// Il verificatore della firma dei webhook contro vettori calcolati da <b>openssl</b>, non dal verificatore stesso (test non
/// circolari). Il formato e' quello che emette il server (Sharp-a-File, ramo dev, <c>WebhookSignature.Sign</c>): header
/// <c>t=&lt;secondi&gt;,v1=&lt;hex&gt;</c>, chiave HMAC-SHA256 = UTF-8 dell'intero segreto con <c>whsec_</c>, messaggio =
/// <c>"&lt;t&gt;."</c> + byte grezzi del corpo.
/// </summary>
/// <remarks>
/// <para>
/// <b>Come sono stati calcolati i vettori</b> (OpenSSL 3.6.5, il 2026-10-02). I corpi sono file (<c>bodyA</c>, <c>bodyB</c>,
/// <c>bodyC</c>, <c>bodyEmpty</c>, <c>bodyBom</c>) scritti da uno script Python dagli stessi byte di questo file; il messaggio e'
/// il prefisso <c>t.</c> seguito dal file, e la firma e':
/// <code>
/// { printf '%s.' 1700000000; cat bodyA; } &gt; msg
/// openssl dgst -sha256 -hmac 'whsec_NotARealSecretTestVectorOnly0123' -hex msg
/// </code>
/// Per il corpo A, che e' solo ASCII, e' equivalente (stesso esito, provato):
/// <c>printf '%s' "1700000000.$(cat bodyA)" | openssl dgst -sha256 -hmac 'whsec_NotARealSecretTestVectorOnly0123' -hex</c>.
/// Per gli altri corpi (non ASCII, byte non UTF-8, vuoto, BOM) si usa il file perche' la shell non e' trasparente ai byte.
/// Lo SHA-256 di ogni corpo (<c>shasum -a 256 body*</c>) e' una costante qui sotto e un test lo ricalcola sui byte costruiti da
/// questo file: se divergono dal file firmato da openssl, i vettori non sarebbero piu' quelli.
/// </para>
/// <para>
/// I segreti sono inventati e chiaramente non reali. I corpi non sono consegne vere ma la forma che il server emette
/// (derivata dal codice di <c>WebhookDispatcher.BuildBody</c>, non catturata): il server in realta' manda JSON ASCII puro,
/// con i non-ASCII e l'apostrofo come escape <c>\u00XX</c>, quindi i corpi B e C (non ASCII, CRLF, byte non UTF-8) sono prove
/// di robustezza byte per byte, non riproduzioni.
/// </para>
/// </remarks>
public sealed class WebhookSignatureVerifierTests
{
    internal const string Secret = "whsec_NotARealSecretTestVectorOnly0123";
    private const string OtherSecret = "whsec_AnotherFakeSecretTestVectorOnly45";
    internal const long SignedAt = 1700000000;
    private const string Backslash = "\U0000005C";

    // Corpo A, secret = Secret, t = 1700000000 (comandi nel commento della classe).
    internal const string SigA = "b8a86bf2187f78a172dbcee940c8f5d80fc2f7d39fec093693d44f3bb666deea";

    // Corpo A, ma con OtherSecret ("whsec_AnotherFakeSecretTestVectorOnly45"): una firma valida di un altro segreto.
    private const string SigAWithOtherSecret = "c97b853b23bbd113e1dfbaff07715d7d367dffd69fe7ff22c5155849c977763b";

    // Corpo A, chiave = il segreto SENZA il prefisso 'whsec_' ("NotARealSecretTestVectorOnly0123"), t = 1700000000.
    //   { printf '%s.' 1700000000; cat bodyA; } > msg; openssl dgst -sha256 -hmac 'NotARealSecretTestVectorOnly0123' -hex msg
    private const string SigAWithoutPrefixKey = "0d1d40b006513130d4cd67c360fa14873983824afc6fe4ee7c14790c695afb8f";

    // Corpo A firmato con t = 1700000300 (un altro istante, stesso corpo): serve ai casi con due 't'.
    private const string SigAAtOtherTime = "788df0070f7c8d5d9b7525fe6cdbdff74af368d38c77268b56bb6950fe771d67";

    // Corpo A con t = 0 e con t = 9223372036854775807 (long.MaxValue): firme valide di istanti fuori da ogni finestra.
    private const string SigAAtEpoch = "66b4df0b15ba38a35933537489ea5ba0236f68d9ee57dda565a7144c7501319e";
    private const string SigAAtLongMax = "d0a40746d1e7df687f6cd734034703d7651eb852182e776c8005a28a222e5782";

    // Corpo A con il testo di t = "01700000000" (zeri iniziali): { printf '%s.' 01700000000; cat bodyA; } > msg
    private const string SigAWithZeroPaddedT = "927d16dd26d5bf164aa48bda98702aaa45f7af6174b510a03a3f2217b9152042";

    // Corpo B (UTF-8 non ASCII, CRLF, a capo finale), C (byte non UTF-8), vuoto e con BOM: Secret, t = 1700000000.
    private const string SigB = "37e990b1aa5b3a4aa7275f00d11e7e0f53aed371f092fb5c6098045027fdbb3e";
    private const string SigC = "32f9da36f8013e632f6b3a4102c454955acd6ac22257cdb3cf302cc70ed0607d";
    private const string SigEmpty = "4700f54fee702fb6e458849ac5619789f6ca5e880b234c0daa4fd3a2e42a712a";
    private const string SigBom = "fa6ad9df2fbba3272064959ce8bbe0d381a0b8413b2e780d5dbfbddcce3bde8f";

    // shasum -a 256 bodyA bodyB bodyC bodyEmpty bodyBom
    private const string ShaA = "007b32c88f6ba84199c05d5ff156d4bcfc4893a4a5bd4fe0623b47e0e31d3723";
    private const string ShaB = "a5e2d64ba2950a3fa087b122b668446b6bba2d0430fb73c0332ff3bd85895cd0";
    private const string ShaC = "37d7e4993e429768e2d11252df42922d67810d7b98f6f577d6f7dc6a2b60277c";
    private const string ShaEmpty = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private const string ShaBom = "bc4dec93f457eeb8bdd10d90389cde1ef73e3ab46fda1002a07e52b2ad0638f2";

    // ------------------------------------------------------------------------------------------ corpi

    internal static byte[] BodyA() => Utf8(
        """
        {"event":"document.uploaded","delivery_id":"whd_01M3VEESG5KBYR5PYAJ0TDT4B2","occurred_at":"2023-11-14T22:13:20.123Z","payload":{"document_id":"doc_01M3VEESG5KBYR5PYAJ0TDT4B2","filename":"perch@BS@u0027 e@BS@u0027.pdf","sha256":"cc1ba284a9fe9cefa40d4bd9dfb8d9e7fb395431aaf79478efca4e04da6c9d7e","deduplicated":false}}
        """.Replace("@BS@", Backslash));

    // UTF-8 non ASCII (e con accento, tre ideogrammi, un carattere fuori dal piano base), CRLF e a capo finale.
    private static byte[] BodyB() => Utf8(
        "{\"event\":\"document.uploaded\",\r\n  \"delivery_id\":\"whd_01M3VEESG5KBYR5PYAJ0TDT4B2\",\r\n"
        + "  \"payload\":{\"filename\":\"perch\U000000E9 \U000065E5\U0000672C\U00008A9E \U0001F4C4.pdf\"}}\r\n");

    // Byte che non sono UTF-8 valido (0xE8 e 0xFF): una ricodifica via stringa li sostituisce e cambia i byte.
    private static byte[] BodyC()
    {
        var head = Utf8("{\"event\":\"x\",\"filename\":\"perch");
        var tail = Utf8(".pdf\"}");
        return head.Concat(new byte[] { 0xE8, 0x20, 0xFF }).Concat(tail).ToArray();
    }

    private static byte[] BodyBom() => new byte[] { 0xEF, 0xBB, 0xBF }.Concat(BodyA()).ToArray();

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    // ------------------------------------------------------------------------------------------ aiuti

    private static WebhookSignatureVerifier VerifierAt(long nowUnixSeconds, TimeSpan? tolerance = null, string secret = Secret) =>
        new(secret, tolerance, new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(nowUnixSeconds)));

    private static WebhookSignatureVerifier VerifierAtSignedTime(TimeSpan? tolerance = null) => VerifierAt(SignedAt, tolerance);

    private static string Header(string signature) => "t=" + SignedAt + ",v1=" + signature;

    private static string Hex(byte[] bytes)
    {
        var text = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
        {
            text.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    // Secondo calcolo, indipendente dal verificatore, con HMACSHA256 del framework: serve a controllare le costanti openssl.
    private static string IndependentMac(string secret, string timestampText, byte[] body)
    {
        var prefix = Utf8(timestampText + ".");
        var message = new byte[prefix.Length + body.Length];
        Buffer.BlockCopy(prefix, 0, message, 0, prefix.Length);
        Buffer.BlockCopy(body, 0, message, prefix.Length, body.Length);
        using var hmac = new HMACSHA256(Utf8(secret));
        return Hex(hmac.ComputeHash(message));
    }

    private static string Sha256Hex(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return Hex(sha.ComputeHash(bytes));
    }

    // Cambia il primo carattere esadecimale con un altro, per ottenere una firma della stessa lunghezza ma sbagliata.
    private static string CorruptHex(string hex) => With(hex, 0, hex[0] == '0' ? '1' : '0');

    // La stringa con il carattere in posizione index sostituito.
    private static string With(string text, int index, char replacement)
    {
        var chars = text.ToCharArray();
        chars[index] = replacement;
        return new string(chars);
    }

    private static void AssertValid(WebhookSignatureResult result)
    {
        Assert.True(result.IsValid, "attesa una firma valida, ottenuto " + result);
        Assert.Equal(WebhookSignatureFailure.None, result.Failure);
    }

    private static void AssertInvalid(WebhookSignatureFailure expected, WebhookSignatureResult result)
    {
        Assert.False(result.IsValid);
        Assert.Equal(expected, result.Failure);
    }

    // ------------------------------------------------------------------------------------------ i vettori sono quelli firmati

    [Fact]
    public void The_bodies_built_here_are_byte_for_byte_the_files_that_openssl_signed()
    {
        Assert.Equal(ShaA, Sha256Hex(BodyA()));
        Assert.Equal(ShaB, Sha256Hex(BodyB()));
        Assert.Equal(ShaC, Sha256Hex(BodyC()));
        Assert.Equal(ShaEmpty, Sha256Hex(Array.Empty<byte>()));
        Assert.Equal(ShaBom, Sha256Hex(BodyBom()));
    }

    [Fact]
    public void The_openssl_vectors_agree_with_an_independent_HMACSHA256_computation()
    {
        Assert.Equal(SigA, IndependentMac(Secret, "1700000000", BodyA()));
        Assert.Equal(SigAWithOtherSecret, IndependentMac(OtherSecret, "1700000000", BodyA()));
        Assert.Equal(SigAWithoutPrefixKey, IndependentMac(Secret.Substring("whsec_".Length), "1700000000", BodyA()));
        Assert.Equal(SigAAtOtherTime, IndependentMac(Secret, "1700000300", BodyA()));
        Assert.Equal(SigAAtEpoch, IndependentMac(Secret, "0", BodyA()));
        Assert.Equal(SigAAtLongMax, IndependentMac(Secret, "9223372036854775807", BodyA()));
        Assert.Equal(SigAWithZeroPaddedT, IndependentMac(Secret, "01700000000", BodyA()));
        Assert.Equal(SigB, IndependentMac(Secret, "1700000000", BodyB()));
        Assert.Equal(SigC, IndependentMac(Secret, "1700000000", BodyC()));
        Assert.Equal(SigEmpty, IndependentMac(Secret, "1700000000", Array.Empty<byte>()));
        Assert.Equal(SigBom, IndependentMac(Secret, "1700000000", BodyBom()));
    }

    // ------------------------------------------------------------------------------------------ firma valida

    [Fact]
    public void A_signature_in_the_server_format_is_accepted()
    {
        var result = VerifierAtSignedTime().Verify(Header(SigA), BodyA());

        AssertValid(result);
    }

    [Fact]
    public void The_header_names_are_the_ones_the_server_sends()
    {
        Assert.Equal("X-SharpAFile-Signature", WebhookHeaders.Signature);
        Assert.Equal("X-SharpAFile-Event", WebhookHeaders.Event);
        Assert.Equal("X-SharpAFile-Delivery", WebhookHeaders.Delivery);
    }

    [Fact]
    public void An_empty_body_is_a_valid_input_with_its_own_signature()
    {
        AssertValid(VerifierAtSignedTime().Verify(Header(SigEmpty), Array.Empty<byte>()));
        AssertInvalid(WebhookSignatureFailure.SignatureMismatch, VerifierAtSignedTime().Verify(Header(SigA), Array.Empty<byte>()));
    }

    [Fact]
    public void The_whsec_prefix_is_part_of_the_key_and_is_neither_stripped_nor_decoded()
    {
        // La firma calcolata senza il prefisso NON e' valida per il segreto intero...
        AssertInvalid(WebhookSignatureFailure.SignatureMismatch, VerifierAtSignedTime().Verify(Header(SigAWithoutPrefixKey), BodyA()));

        // ...e il segreto senza prefisso e' semplicemente un altro segreto: con lui, quella firma e' valida e quella del server no.
        var withoutPrefix = VerifierAt(SignedAt, secret: Secret.Substring("whsec_".Length));
        AssertValid(withoutPrefix.Verify(Header(SigAWithoutPrefixKey), BodyA()));
        AssertInvalid(WebhookSignatureFailure.SignatureMismatch, withoutPrefix.Verify(Header(SigA), BodyA()));
    }

    [Fact]
    public void A_different_secret_never_validates()
    {
        AssertInvalid(WebhookSignatureFailure.SignatureMismatch, VerifierAt(SignedAt, secret: OtherSecret).Verify(Header(SigA), BodyA()));
        AssertInvalid(WebhookSignatureFailure.SignatureMismatch, VerifierAtSignedTime().Verify(Header(SigAWithOtherSecret), BodyA()));
        AssertValid(VerifierAt(SignedAt, secret: OtherSecret).Verify(Header(SigAWithOtherSecret), BodyA()));
    }

    [Fact]
    public void The_secret_is_not_trimmed()
    {
        // Un segreto letto da un file con un a capo finale e' un altro segreto: le firme del server non tornano.
        AssertInvalid(WebhookSignatureFailure.SignatureMismatch, VerifierAt(SignedAt, secret: Secret + "\n").Verify(Header(SigA), BodyA()));
        AssertInvalid(WebhookSignatureFailure.SignatureMismatch, VerifierAt(SignedAt, secret: " " + Secret).Verify(Header(SigA), BodyA()));
    }

    // ------------------------------------------------------------------------------------------ piu' v1 (rotazione)

    [Fact]
    public void Several_v1_elements_are_accepted_when_any_one_matches_in_any_position()
    {
        var wrong = CorruptHex(SigA);
        var verifier = VerifierAtSignedTime();
        var prefix = "t=" + SignedAt;

        AssertValid(verifier.Verify(prefix + ",v1=" + wrong + ",v1=" + SigA, BodyA())); // la prima e' sbagliata, la seconda giusta
        AssertValid(verifier.Verify(prefix + ",v1=" + SigA + ",v1=" + wrong, BodyA())); // la prima e' giusta, la seconda sbagliata
        AssertValid(verifier.Verify(prefix + ",v1=" + wrong + ",v1=" + SigAWithOtherSecret + ",v1=" + SigA + ",v1=" + wrong, BodyA()));
        AssertValid(verifier.Verify("v1=" + wrong + "," + prefix + ",v1=" + SigA, BodyA())); // la t in mezzo
        AssertInvalid(WebhookSignatureFailure.SignatureMismatch, verifier.Verify(prefix + ",v1=" + wrong + ",v1=" + SigAWithOtherSecret, BodyA()));
    }

    [Fact]
    public void A_long_list_of_v1_elements_is_checked_to_the_end()
    {
        var wrong = CorruptHex(SigA);
        var header = "t=" + SignedAt + string.Concat(Enumerable.Repeat(",v1=" + wrong, 500)) + ",v1=" + SigA;

        AssertValid(VerifierAtSignedTime().Verify(header, BodyA()));
    }

    [Fact]
    public void A_v1_that_cannot_match_does_not_stop_the_others_from_matching()
    {
        var verifier = VerifierAtSignedTime();
        var prefix = "t=" + SignedAt;

        AssertValid(verifier.Verify(prefix + ",v1=abc,v1=" + SigA, BodyA())); // troppo corta, poi giusta
        AssertValid(verifier.Verify(prefix + ",v1=,v1=" + SigA, BodyA())); // vuota
        AssertValid(verifier.Verify(prefix + ",v1=" + new string('z', 64) + ",v1=" + SigA, BodyA())); // lunghezza giusta ma non esadecimale
        AssertValid(verifier.Verify(prefix + ",v1=" + SigA + "00,v1=" + SigA, BodyA())); // troppo lunga, poi giusta
    }

    // ------------------------------------------------------------------------------------------ v1 di forma sbagliata

    [Fact]
    public void A_v1_of_the_wrong_length_never_matches_even_when_it_is_a_prefix_or_has_a_valid_prefix()
    {
        var verifier = VerifierAtSignedTime();

        AssertInvalid(WebhookSignatureFailure.SignatureMismatch, verifier.Verify(Header(SigA.Substring(0, 62)), BodyA())); // 62 caratteri
        AssertInvalid(WebhookSignatureFailure.SignatureMismatch, verifier.Verify(Header(SigA.Substring(0, 1)), BodyA()));
        AssertInvalid(WebhookSignatureFailure.SignatureMismatch, verifier.Verify(Header(SigA + "00"), BodyA())); // la firma giusta piu' due cifre
        AssertInvalid(WebhookSignatureFailure.SignatureMismatch, verifier.Verify(Header(SigA + SigA), BodyA()));
        AssertInvalid(WebhookSignatureFailure.SignatureMismatch, verifier.Verify(Header(SigA + "zz"), BodyA()));
        AssertInvalid(WebhookSignatureFailure.SignatureMismatch, verifier.Verify(Header(string.Empty), BodyA()));
    }

    [Fact]
    public void A_v1_with_non_hexadecimal_or_non_ASCII_characters_never_matches()
    {
        var verifier = VerifierAtSignedTime();

        AssertInvalid(WebhookSignatureFailure.SignatureMismatch, verifier.Verify(Header(With(SigA, 63, 'g')), BodyA()));
        AssertInvalid(WebhookSignatureFailure.SignatureMismatch, verifier.Verify(Header(With(With(SigA, 0, '0'), 1, 'x')), BodyA()));
        AssertInvalid(WebhookSignatureFailure.SignatureMismatch, verifier.Verify(Header(With(SigA, 0, ' ')), BodyA())); // uno spazio dentro il valore (dopo il '='): resta un carattere non esadecimale
        AssertInvalid(WebhookSignatureFailure.SignatureMismatch, verifier.Verify(Header(new string('\U0000FF11', 64)), BodyA())); // cifre a larghezza piena
        AssertInvalid(WebhookSignatureFailure.SignatureMismatch, verifier.Verify(Header(With(SigA, 63, '\U00000665')), BodyA())); // cifra arabo-indiana
    }

    [Fact]
    public void An_uppercase_or_mixed_case_v1_is_decoded_and_compared_as_bytes()
    {
        var verifier = VerifierAtSignedTime();
        var mixed = string.Concat(SigA.Substring(0, 32).ToUpperInvariant(), SigA.Substring(32));

        AssertValid(verifier.Verify(Header(SigA.ToUpperInvariant()), BodyA()));
        AssertValid(verifier.Verify(Header(mixed), BodyA()));
    }

    // ------------------------------------------------------------------------------------------ corpo: byte per byte

    [Fact]
    public void A_body_changed_by_a_single_byte_is_rejected_wherever_the_byte_is()
    {
        var verifier = VerifierAtSignedTime();
        var original = BodyA();

        foreach (var index in new[] { 0, 1, original.Length / 2, original.Length - 2, original.Length - 1 })
        {
            var altered = (byte[])original.Clone();
            altered[index] ^= 0x01;
            AssertInvalid(WebhookSignatureFailure.SignatureMismatch, verifier.Verify(Header(SigA), altered));
        }

        // Un byte in meno e un byte in piu'.
        AssertInvalid(WebhookSignatureFailure.SignatureMismatch, verifier.Verify(Header(SigA), original.Take(original.Length - 1).ToArray()));
        AssertInvalid(WebhookSignatureFailure.SignatureMismatch, verifier.Verify(Header(SigA), original.Concat(new byte[] { 0x0A }).ToArray()));
        AssertInvalid(WebhookSignatureFailure.SignatureMismatch, verifier.Verify(Header(SigA), original.Skip(1).ToArray()));
    }

    [Fact]
    public void A_body_with_non_ASCII_text_CRLF_and_a_final_newline_is_used_byte_for_byte()
    {
        var verifier = VerifierAtSignedTime();

        AssertValid(verifier.Verify(Header(SigB), BodyB()));

        var text = Encoding.UTF8.GetString(BodyB());
        AssertInvalid(WebhookSignatureFailure.SignatureMismatch, verifier.Verify(Header(SigB), Utf8(text.Replace("\r\n", "\n")))); // fine riga diversi
        AssertInvalid(WebhookSignatureFailure.SignatureMismatch, verifier.Verify(Header(SigB), Utf8(text.TrimEnd('\r', '\n')))); // senza a capo finale
        AssertInvalid(WebhookSignatureFailure.SignatureMismatch, verifier.Verify(Header(SigB), Utf8(text + "\r\n"))); // un a capo in piu'
        AssertInvalid(WebhookSignatureFailure.SignatureMismatch, verifier.Verify(Header(SigB), Encoding.Unicode.GetBytes(text))); // altra codifica
        AssertInvalid(WebhookSignatureFailure.SignatureMismatch, verifier.Verify(Header(SigB), new byte[] { 0xEF, 0xBB, 0xBF }.Concat(BodyB()).ToArray())); // BOM aggiunto
    }

    [Fact]
    public void The_same_JSON_re_indented_is_a_different_body_and_is_rejected()
    {
        var verifier = VerifierAtSignedTime();
        var original = BodyA();
        var reindented = ReIndent(original);

        Assert.NotEqual(original, reindented); // il JSON e' lo stesso, i byte no
        AssertInvalid(WebhookSignatureFailure.SignatureMismatch, verifier.Verify(Header(SigA), reindented));
        AssertValid(verifier.Verify(Header(SigA), original));
    }

    [Fact]
    public void A_body_that_is_not_valid_UTF8_is_used_byte_for_byte_and_a_text_round_trip_breaks_it()
    {
        var verifier = VerifierAtSignedTime();
        var body = BodyC();

        AssertValid(verifier.Verify(Header(SigC), body));

        // Perche' non c'e' un overload che prenda una stringa: leggere il corpo come testo e ricodificarlo sostituisce i byte
        // non validi (U+FFFD) e la firma non torna piu'.
        var roundTripped = Utf8(Encoding.UTF8.GetString(body));
        Assert.NotEqual(body, roundTripped);
        AssertInvalid(WebhookSignatureFailure.SignatureMismatch, verifier.Verify(Header(SigC), roundTripped));
    }

    [Fact]
    public void A_leading_BOM_is_part_of_the_signed_bytes()
    {
        var verifier = VerifierAtSignedTime();

        AssertValid(verifier.Verify(Header(SigBom), BodyBom()));
        AssertInvalid(WebhookSignatureFailure.SignatureMismatch, verifier.Verify(Header(SigBom), BodyA())); // BOM tolto
        AssertInvalid(WebhookSignatureFailure.SignatureMismatch, verifier.Verify(Header(SigA), BodyBom())); // BOM aggiunto
    }

    // ------------------------------------------------------------------------------------------ finestra di tempo

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(-1, true)]
    [InlineData(299, true)]
    [InlineData(-299, true)]
    [InlineData(300, true)] // esattamente alla tolleranza: accettato
    [InlineData(-300, true)] // esattamente alla tolleranza nel futuro: accettato
    [InlineData(301, false)] // un secondo oltre: rifiutato
    [InlineData(-301, false)] // t nel futuro oltre la tolleranza: rifiutato
    [InlineData(100000, false)]
    [InlineData(-100000, false)]
    public void The_default_window_is_five_minutes_on_both_sides(long clockOffsetFromSignedTime, bool accepted)
    {
        // delta > 0: l'orologio del ricevitore e' in avanti rispetto a t (consegna vecchia); delta < 0: t e' nel futuro.
        var verifier = VerifierAt(SignedAt + clockOffsetFromSignedTime);

        var result = verifier.Verify(Header(SigA), BodyA());

        if (accepted)
        {
            AssertValid(result);
        }
        else
        {
            AssertInvalid(WebhookSignatureFailure.TimestampOutOfTolerance, result);
        }
    }

    [Theory]
    [InlineData(10, 10, true)]
    [InlineData(10, -10, true)]
    [InlineData(10, 11, false)]
    [InlineData(10, -11, false)]
    [InlineData(1, 1, true)]
    [InlineData(1, -1, true)]
    [InlineData(1, 2, false)]
    [InlineData(1, -2, false)]
    [InlineData(86400, 86400, true)]
    [InlineData(86400, -86401, false)]
    public void A_custom_tolerance_moves_both_edges(int toleranceSeconds, long clockOffsetFromSignedTime, bool accepted)
    {
        var verifier = VerifierAt(SignedAt + clockOffsetFromSignedTime, TimeSpan.FromSeconds(toleranceSeconds));

        var result = verifier.Verify(Header(SigA), BodyA());

        Assert.Equal(accepted, result.IsValid);
        Assert.Equal(accepted ? WebhookSignatureFailure.None : WebhookSignatureFailure.TimestampOutOfTolerance, result.Failure);
    }

    [Fact]
    public void The_window_is_measured_in_whole_seconds_and_a_sub_second_tolerance_means_the_same_second_only()
    {
        // 500 ms e' positiva (si accetta) ma vale 0 secondi interi: solo la stessa secondo.
        AssertValid(VerifierAt(SignedAt, TimeSpan.FromMilliseconds(500)).Verify(Header(SigA), BodyA()));
        AssertInvalid(WebhookSignatureFailure.TimestampOutOfTolerance, VerifierAt(SignedAt + 1, TimeSpan.FromMilliseconds(500)).Verify(Header(SigA), BodyA()));
        AssertInvalid(WebhookSignatureFailure.TimestampOutOfTolerance, VerifierAt(SignedAt - 1, TimeSpan.FromMilliseconds(500)).Verify(Header(SigA), BodyA()));

        // 1,9 s vale 1 secondo intero.
        AssertValid(VerifierAt(SignedAt + 1, TimeSpan.FromMilliseconds(1900)).Verify(Header(SigA), BodyA()));
        AssertInvalid(WebhookSignatureFailure.TimestampOutOfTolerance, VerifierAt(SignedAt + 2, TimeSpan.FromMilliseconds(1900)).Verify(Header(SigA), BodyA()));
    }

    [Fact]
    public void The_clock_is_read_in_whole_seconds()
    {
        // 300 s e 900 ms dopo t: l'ora in secondi interi e' t + 300, dentro la finestra.
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(SignedAt + 300).AddMilliseconds(900));
        var verifier = new WebhookSignatureVerifier(Secret, timeProvider: clock);

        AssertValid(verifier.Verify(Header(SigA), BodyA()));

        clock.Advance(TimeSpan.FromMilliseconds(100)); // t + 301 s esatti
        AssertInvalid(WebhookSignatureFailure.TimestampOutOfTolerance, verifier.Verify(Header(SigA), BodyA()));
    }

    [Fact]
    public void The_verifier_follows_the_clock_it_was_given()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(SignedAt));
        var verifier = new WebhookSignatureVerifier(Secret, timeProvider: clock);

        AssertValid(verifier.Verify(Header(SigA), BodyA()));

        clock.Advance(TimeSpan.FromMinutes(5));
        AssertValid(verifier.Verify(Header(SigA), BodyA()));

        clock.Advance(TimeSpan.FromSeconds(1));
        AssertInvalid(WebhookSignatureFailure.TimestampOutOfTolerance, verifier.Verify(Header(SigA), BodyA()));
    }

    [Fact]
    public void A_signed_timestamp_at_the_extremes_is_out_of_tolerance_and_never_overflows()
    {
        var verifier = VerifierAtSignedTime();

        AssertInvalid(WebhookSignatureFailure.TimestampOutOfTolerance, verifier.Verify("t=0,v1=" + SigAAtEpoch, BodyA()));
        AssertInvalid(WebhookSignatureFailure.TimestampOutOfTolerance, verifier.Verify("t=9223372036854775807,v1=" + SigAAtLongMax, BodyA()));

        // Con un orologio impossibile (prima del 1970) e un t enorme la sottrazione t - ora andrebbe in overflow: non deve.
        var beforeEpoch = VerifierAt(-86400 * 365);
        AssertInvalid(WebhookSignatureFailure.TimestampOutOfTolerance, beforeEpoch.Verify("t=9223372036854775807,v1=" + SigAAtLongMax, BodyA()));
    }

    [Fact]
    public void A_huge_tolerance_does_not_overflow()
    {
        var verifier = VerifierAtSignedTime(TimeSpan.MaxValue);

        AssertValid(verifier.Verify(Header(SigA), BodyA()));
        AssertValid(verifier.Verify("t=0,v1=" + SigAAtEpoch, BodyA()));
        AssertInvalid(WebhookSignatureFailure.TimestampOutOfTolerance, verifier.Verify("t=9223372036854775807,v1=" + SigAAtLongMax, BodyA())); // anche MaxValue secondi e' oltre
    }

    [Fact]
    public void The_signature_is_checked_before_the_window_so_a_wrong_signature_never_reveals_the_window()
    {
        var farFuture = VerifierAt(SignedAt + 100000); // t ben oltre la tolleranza

        // Firma sbagliata e fuori finestra: il motivo e' la firma.
        AssertInvalid(WebhookSignatureFailure.SignatureMismatch, farFuture.Verify(Header(CorruptHex(SigA)), BodyA()));
        AssertInvalid(WebhookSignatureFailure.SignatureMismatch, VerifierAt(SignedAt - 100000).Verify(Header(CorruptHex(SigA)), BodyA()));

        // Firma giusta e fuori finestra: il motivo e' il tempo.
        AssertInvalid(WebhookSignatureFailure.TimestampOutOfTolerance, farFuture.Verify(Header(SigA), BodyA()));
    }

    [Fact]
    public void The_real_system_clock_is_used_when_no_time_provider_is_given()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var body = BodyA();
        var verifier = new WebhookSignatureVerifier(Secret);

        AssertValid(verifier.Verify("t=" + now + ",v1=" + IndependentMac(Secret, now, body), body));
        AssertInvalid(WebhookSignatureFailure.TimestampOutOfTolerance, verifier.Verify(Header(SigA), body)); // t = 1700000000 e' passato
    }

    // ------------------------------------------------------------------------------------------ header assente o malformato

    [Fact]
    public void A_null_header_is_missing()
    {
        AssertInvalid(WebhookSignatureFailure.MissingHeader, VerifierAtSignedTime().Verify(null, BodyA()));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData(" \t ")]
    [InlineData("\r\n")]
    public void An_empty_or_blank_header_is_missing_not_malformed(string header)
    {
        AssertInvalid(WebhookSignatureFailure.MissingHeader, VerifierAtSignedTime().Verify(header, BodyA()));
    }

    public static TheoryData<string> MalformedHeaders()
    {
        const string Ts = "1700000000";
        var sig = SigA;
        return new TheoryData<string>
        {
            "garbage", // niente '='
            ",", // elementi vuoti
            "=", // chiave e valore vuoti
            "v1=" + sig, // manca t
            "t=" + Ts, // manca v1
            "t=" + Ts + ",", // elemento vuoto finale
            "t=" + Ts + ",,v1=" + sig, // elemento vuoto in mezzo
            ",t=" + Ts + ",v1=" + sig, // elemento vuoto iniziale
            "t=" + Ts + ",v1=" + sig + ",", // virgola finale
            "t=" + Ts + ",v1", // elemento senza '='
            "t=" + Ts + ",v1=" + sig + ",extra", // chiave sconosciuta ma senza '='
            "t=" + Ts + " v1=" + sig, // separati da spazio e non da virgola: un solo elemento con t non numerica
            "t=" + Ts + ";v1=" + sig, // punto e virgola
            "t=" + Ts + ",t=" + Ts + ",v1=" + sig, // due t uguali
            "t=" + Ts + ",t=1700000300,v1=" + sig, // due t: la prima e' quella firmata
            "t=1700000300,t=" + Ts + ",v1=" + sig, // due t: la seconda e' quella firmata
            "v1=" + sig + ",t=" + Ts + ",t=1700000300", // due t dopo la v1
            "t=,v1=" + sig, // t vuota
            "t=abc,v1=" + sig,
            "t=-1700000000,v1=" + sig, // negativa
            "t=+1700000000,v1=" + sig, // con segno
            "t=-0,v1=" + sig,
            "t=1700000000.5,v1=" + sig, // decimale
            "t=1.7e9,v1=" + sig, // notazione scientifica
            "t=0x6553F100,v1=" + sig, // esadecimale
            "t=1700 000000,v1=" + sig, // spazio dentro il numero
            "t=1700000000x,v1=" + sig,
            "t=9223372036854775808,v1=" + sig, // long.MaxValue + 1: non sta in un long
            "t=99999999999999999999,v1=" + sig, // enorme
            "t=" + new string('9', 400) + ",v1=" + sig, // enorme, centinaia di cifre
            "t=\U0000FF11\U0000FF17\U0000FF10\U0000FF10\U0000FF10\U0000FF10\U0000FF10\U0000FF10\U0000FF10\U0000FF10,v1=" + sig, // cifre a larghezza piena
            "t=\U00000661\U00000662\U00000663,v1=" + sig, // cifre arabo-indiane
            "T=" + Ts + ",v1=" + sig, // le chiavi sono sensibili alle maiuscole: manca 't'
            "t=" + Ts + ",V1=" + sig, // manca 'v1'
            "t = " + Ts + ",v1=" + sig, // spazi attorno al '=': la chiave diventa "t " (sconosciuta)
            "\U000000A0t=" + Ts + ",v1=" + sig, // spazio unicode (non-breaking) ai bordi: non e' uno spazio tollerato
            "t=" + Ts + ",v2=" + sig, // v2 non e' v1
        };
    }

    [Theory]
    [MemberData(nameof(MalformedHeaders))]
    public void A_header_that_is_not_t_and_v1_elements_is_malformed(string header)
    {
        // Con l'orologio sull'istante firmato e la firma giusta, un header ben formato sarebbe valido o al piu' un mismatch:
        // qui deve fermarsi prima, come malformato.
        AssertInvalid(WebhookSignatureFailure.MalformedHeader, VerifierAtSignedTime().Verify(header, BodyA()));
    }

    [Fact]
    public void The_largest_representable_timestamp_is_well_formed()
    {
        // long.MaxValue sta in un long: l'header e' ben formato (la firma e' quella vera di openssl) e il rifiuto e' per il tempo.
        AssertInvalid(WebhookSignatureFailure.TimestampOutOfTolerance, VerifierAtSignedTime().Verify("t=9223372036854775807,v1=" + SigAAtLongMax, BodyA()));
        AssertInvalid(WebhookSignatureFailure.SignatureMismatch, VerifierAtSignedTime().Verify("t=9223372036854775807,v1=" + SigA, BodyA()));
    }

    [Fact]
    public void Leading_zeros_in_t_are_part_of_the_signed_text_and_are_not_normalized()
    {
        var verifier = VerifierAtSignedTime();

        // Il messaggio usa il testo di t cosi' com'e' scritto nell'header (il server scrive sempre il numero canonico).
        AssertValid(verifier.Verify("t=01700000000,v1=" + SigAWithZeroPaddedT, BodyA()));
        AssertInvalid(WebhookSignatureFailure.SignatureMismatch, verifier.Verify("t=01700000000,v1=" + SigA, BodyA()));
        AssertInvalid(WebhookSignatureFailure.SignatureMismatch, verifier.Verify("t=1700000000,v1=" + SigAWithZeroPaddedT, BodyA()));
    }

    [Fact]
    public void The_message_signs_t_dot_body_and_neither_the_body_alone_nor_another_t()
    {
        var verifier = VerifierAtSignedTime();

        AssertInvalid(WebhookSignatureFailure.SignatureMismatch, verifier.Verify("t=1700000300,v1=" + SigA, BodyA())); // t diverso da quello firmato
        AssertInvalid(WebhookSignatureFailure.SignatureMismatch, verifier.Verify("t=1700000000,v1=" + SigAAtOtherTime, BodyA()));
        AssertValid(VerifierAt(1700000300).Verify("t=1700000300,v1=" + SigAAtOtherTime, BodyA())); // il t firmato con il suo t
    }

    // ------------------------------------------------------------------------------------------ elementi sconosciuti e spazi

    [Fact]
    public void Unknown_keys_are_ignored_wherever_they_are()
    {
        var verifier = VerifierAtSignedTime();

        AssertValid(verifier.Verify("t=1700000000,v2=" + new string('a', 64) + ",v1=" + SigA, BodyA()));
        AssertValid(verifier.Verify("v0=zzz,t=1700000000,v1=" + SigA + ",scheme=hmac-sha256", BodyA()));
        AssertValid(verifier.Verify("t=1700000000,v1=" + SigA + ",v2=", BodyA()));
        AssertValid(verifier.Verify("t=1700000000,v1=" + SigA + ",key=a=b=c", BodyA())); // il valore puo' contenere '='
        AssertValid(verifier.Verify("t=1700000000,v1=" + SigA + ",=x", BodyA())); // chiave vuota: sconosciuta, ignorata
    }

    [Fact]
    public void An_unknown_key_alone_does_not_replace_a_missing_v1()
    {
        AssertInvalid(WebhookSignatureFailure.MalformedHeader, VerifierAtSignedTime().Verify("t=1700000000,v2=" + SigA, BodyA()));
    }

    [Fact]
    public void Spaces_and_tabs_around_elements_are_tolerated()
    {
        var verifier = VerifierAtSignedTime();

        AssertValid(verifier.Verify("  t=1700000000  ,  v1=" + SigA + "  ", BodyA()));
        AssertValid(verifier.Verify("t=1700000000,\tv1=" + SigA + "\t", BodyA()));
        AssertValid(verifier.Verify(" \t t=1700000000 \t,\t \tv1=" + SigA + " ", BodyA()));
        AssertValid(verifier.Verify("t=1700000000, v1=" + SigA, BodyA())); // la forma con virgola e spazio
    }

    // ------------------------------------------------------------------------------------------ costruttore e argomenti

    [Fact]
    public void A_null_secret_throws_ArgumentNullException()
    {
        var error = Assert.Throws<ArgumentNullException>(() => new WebhookSignatureVerifier(null!));

        Assert.Equal("secret", error.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\r\n")]
    public void An_empty_or_blank_secret_throws_ArgumentException(string secret)
    {
        var error = Assert.Throws<ArgumentException>(() => new WebhookSignatureVerifier(secret));

        Assert.Equal("secret", error.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-300)]
    public void A_zero_or_negative_tolerance_throws_ArgumentOutOfRangeException(int seconds)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new WebhookSignatureVerifier(Secret, TimeSpan.FromSeconds(seconds)));

        Assert.Equal("tolerance", error.ParamName);
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebhookSignatureVerifier(Secret, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebhookSignatureVerifier(Secret, TimeSpan.MinValue));
    }

    [Fact]
    public void The_default_tolerance_is_five_minutes_and_a_tick_is_still_a_valid_positive_tolerance()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), new WebhookSignatureVerifier(Secret).Tolerance);
        Assert.Equal(TimeSpan.FromMinutes(5), WebhookSignatureVerifier.DefaultTolerance);
        Assert.Equal(TimeSpan.FromSeconds(30), new WebhookSignatureVerifier(Secret, TimeSpan.FromSeconds(30)).Tolerance);
        Assert.Equal(TimeSpan.FromTicks(1), new WebhookSignatureVerifier(Secret, TimeSpan.FromTicks(1)).Tolerance);
    }

    [Fact]
    public void A_null_body_throws_ArgumentNullException_for_any_header()
    {
        var verifier = VerifierAtSignedTime();

        Assert.Equal("body", Assert.Throws<ArgumentNullException>(() => verifier.Verify(Header(SigA), null!)).ParamName);
        Assert.Equal("body", Assert.Throws<ArgumentNullException>(() => verifier.Verify(null, null!)).ParamName); // il corpo null e' un errore di programmazione anche senza header
        Assert.Equal("body", Assert.Throws<ArgumentNullException>(() => verifier.Verify("garbage", null!)).ParamName);
    }

    [Fact]
    public void A_wrong_header_never_throws()
    {
        var verifier = VerifierAtSignedTime();
        var weird = new[]
        {
            new string('=', 10000),
            new string(',', 10000),
            new string('t', 10000),
            "t=" + new string('1', 100000) + ",v1=" + SigA,
            ((char)0xD800).ToString(), // surrogato isolato
            "t=1700000000,v1=" + SigA + "\0",
            "\0",
            "t=1700000000,v1=" + SigA + "\r\nX-Injected: 1",
        };

        foreach (var header in weird)
        {
            Assert.False(verifier.Verify(header, BodyA()).IsValid);
        }
    }

    [Fact]
    public void One_verifier_can_be_used_from_several_threads_at_once()
    {
        var verifier = VerifierAtSignedTime();
        var body = BodyA();
        var header = Header(SigA);
        var failures = new System.Collections.Concurrent.ConcurrentBag<WebhookSignatureResult>();

        Parallel.For(0, 400, _ =>
        {
            var result = verifier.Verify(header, body);
            if (!result.IsValid)
            {
                failures.Add(result);
            }
        });

        Assert.True(failures.IsEmpty);
    }

    // ------------------------------------------------------------------------------------------ il segreto non esce

    [Fact]
    public void The_secret_never_appears_in_ToString_in_exceptions_or_in_any_public_value()
    {
        var verifier = new WebhookSignatureVerifier(Secret);
        var body = BodyA();
        var outputs = new List<string>
        {
            verifier.ToString(),
            Assert.Throws<ArgumentOutOfRangeException>(() => new WebhookSignatureVerifier(Secret, TimeSpan.Zero)).ToString(),
            Assert.Throws<ArgumentOutOfRangeException>(() => new WebhookSignatureVerifier(Secret, TimeSpan.FromSeconds(-5))).ToString(),
            Assert.Throws<ArgumentNullException>(() => verifier.Verify(Header(SigA), null!)).ToString(),
            Assert.Throws<ArgumentException>(() => new WebhookSignatureVerifier("   ")).ToString(),
        };

        // Ogni risultato possibile, valido e non valido.
        outputs.Add(VerifierAtSignedTime().Verify(Header(SigA), body).ToString());
        outputs.Add(VerifierAtSignedTime().Verify(Header(CorruptHex(SigA)), body).ToString());
        outputs.Add(VerifierAtSignedTime().Verify(null, body).ToString());
        outputs.Add(VerifierAtSignedTime().Verify("garbage", body).ToString());
        outputs.Add(VerifierAt(SignedAt + 1000000).Verify(Header(SigA), body).ToString());
        foreach (WebhookSignatureFailure failure in Enum.GetValues(typeof(WebhookSignatureFailure)))
        {
            Assert.DoesNotContain(Secret, failure.ToString(), StringComparison.Ordinal);
        }

        // Nessuna proprieta' ne' campo pubblico del verificatore (o dei risultati) contiene il segreto.
        foreach (var instance in new object[] { verifier, VerifierAtSignedTime().Verify(Header(SigA), body) })
        {
            var type = instance.GetType();
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                outputs.Add(Convert.ToString(property.GetValue(instance, null), CultureInfo.InvariantCulture) ?? string.Empty);
            }

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                outputs.Add(Convert.ToString(field.GetValue(instance), CultureInfo.InvariantCulture) ?? string.Empty);
            }
        }

        foreach (var output in outputs)
        {
            Assert.DoesNotContain(Secret, output, StringComparison.Ordinal);
            Assert.DoesNotContain(Secret.Substring("whsec_".Length), output, StringComparison.Ordinal);
        }

        Assert.Contains("00:05:00", verifier.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_verifier_exposes_only_the_tolerance_and_Verify_publicly()
    {
        var members = typeof(WebhookSignatureVerifier)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => m is not MethodInfo { IsSpecialName: true })
            .Select(m => m.MemberType + " " + m.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "Constructor .ctor",
                "Field DefaultTolerance",
                "Method ToString",
                "Method Verify",
                "Property Tolerance",
            },
            members);
    }

    [Fact]
    public void There_is_no_Verify_overload_that_takes_the_body_as_text()
    {
        // Ricodificare il testo ricevuto rompe la firma: il corpo si passa solo come byte[].
        var overloads = typeof(WebhookSignatureVerifier).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.Name == "Verify")
            .ToArray();

        var single = Assert.Single(overloads);
        Assert.Equal(new[] { typeof(string), typeof(byte[]) }, single.GetParameters().Select(p => p.ParameterType).ToArray());
    }

    // ------------------------------------------------------------------------------------------ risultato

    [Fact]
    public void A_result_is_valid_exactly_when_it_has_no_failure()
    {
        var valid = WebhookSignatureResult.Valid;

        Assert.True(valid.IsValid);
        Assert.Equal(WebhookSignatureFailure.None, valid.Failure);
        Assert.Equal("Valid", valid.ToString());

        foreach (WebhookSignatureFailure failure in Enum.GetValues(typeof(WebhookSignatureFailure)))
        {
            if (failure == WebhookSignatureFailure.None)
            {
                continue;
            }

            var invalid = WebhookSignatureResult.Invalid(failure);
            Assert.False(invalid.IsValid);
            Assert.Equal(failure, invalid.Failure);
            Assert.Contains(failure.ToString(), invalid.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_failure_enum_has_the_documented_members_and_None_is_zero()
    {
        Assert.Equal(0, (int)WebhookSignatureFailure.None);
        Assert.Equal(
            new[] { "None", "MissingHeader", "MalformedHeader", "TimestampOutOfTolerance", "SignatureMismatch" },
            Enum.GetNames(typeof(WebhookSignatureFailure)));
    }

    // ------------------------------------------------------------------------------------------ aiuti interni

    [Fact]
    public void FixedTimeEquals_compares_every_byte_and_the_length()
    {
        var a = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };

        Assert.True(WebhookSignatureVerifier.FixedTimeEquals(a, (byte[])a.Clone()));
        Assert.True(WebhookSignatureVerifier.FixedTimeEquals(Array.Empty<byte>(), Array.Empty<byte>()));
        Assert.False(WebhookSignatureVerifier.FixedTimeEquals(a, a.Take(7).ToArray()));
        Assert.False(WebhookSignatureVerifier.FixedTimeEquals(a.Take(7).ToArray(), a));
        Assert.False(WebhookSignatureVerifier.FixedTimeEquals(a, Array.Empty<byte>()));
        for (var i = 0; i < a.Length; i++)
        {
            foreach (var flip in new byte[] { 0x01, 0x80, 0xFF })
            {
                var other = (byte[])a.Clone();
                other[i] ^= flip;
                Assert.False(WebhookSignatureVerifier.FixedTimeEquals(a, other), $"posizione {i}, maschera {flip}");
            }
        }
    }

    [Fact]
    public void TryDecodeHex_accepts_exactly_64_ASCII_hexadecimal_characters_of_either_case()
    {
        Assert.Equal(32, WebhookSignatureVerifier.TryDecodeHex(SigA)!.Length);
        Assert.Equal(SigA, Hex(WebhookSignatureVerifier.TryDecodeHex(SigA)!));
        Assert.Equal(WebhookSignatureVerifier.TryDecodeHex(SigA), WebhookSignatureVerifier.TryDecodeHex(SigA.ToUpperInvariant()));
        Assert.Equal(new byte[32], WebhookSignatureVerifier.TryDecodeHex(new string('0', 64)));
        Assert.Equal(Enumerable.Repeat((byte)0xFF, 32).ToArray(), WebhookSignatureVerifier.TryDecodeHex(new string('f', 64)));
        Assert.Equal(Enumerable.Repeat((byte)0xFF, 32).ToArray(), WebhookSignatureVerifier.TryDecodeHex(new string('F', 64)));

        Assert.Null(WebhookSignatureVerifier.TryDecodeHex(string.Empty));
        Assert.Null(WebhookSignatureVerifier.TryDecodeHex(new string('a', 63)));
        Assert.Null(WebhookSignatureVerifier.TryDecodeHex(new string('a', 65)));
        Assert.Null(WebhookSignatureVerifier.TryDecodeHex(new string('a', 62) + "g1"));
        Assert.Null(WebhookSignatureVerifier.TryDecodeHex(new string('a', 63) + "G"));
        Assert.Null(WebhookSignatureVerifier.TryDecodeHex(new string('\U0000FF11', 64)));
        Assert.Null(WebhookSignatureVerifier.TryDecodeHex(new string('a', 63) + "\U00000665"));
        Assert.Null(WebhookSignatureVerifier.TryDecodeHex(new string('a', 63) + "/")); // il carattere prima di '0'
        Assert.Null(WebhookSignatureVerifier.TryDecodeHex(new string('a', 63) + ":")); // il carattere dopo '9'
        Assert.Null(WebhookSignatureVerifier.TryDecodeHex(new string('a', 63) + "`")); // prima di 'a'
        Assert.Null(WebhookSignatureVerifier.TryDecodeHex(new string('a', 63) + "@")); // prima di 'A'
    }

    private static byte[] ReIndent(byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            document.RootElement.WriteTo(writer);
        }

        return buffer.ToArray();
    }
}
