using Filemaster.Infrastructure;

namespace Filemaster.UnitTests.Wire;

/// <summary>
/// <c>Content-Disposition</c> in lettura (il nome del file di un download: <c>filename*</c> UTF-8 percent-codificato, preferito, e il ripiego
/// ASCII <c>filename</c>, come li emette il server) e in scrittura (le parti del modulo multipart di un caricamento). Vettori scritti a mano e
/// ricopiati dalle catture 115, 116 e 54; la scrittura e' confrontata con <c>ContentDisposition.Build</c> del server per il percorso inverso.
/// </summary>
public sealed class ContentDispositionHeaderTests
{
    private static string? Read(string? value) => ContentDispositionHeader.ReadFileName(value);

    // ----- lettura: le intestazioni vere del server -----

    [Fact]
    public void The_captured_headers_give_the_real_name_from_filename_star_and_not_the_ASCII_fallback()
    {
        // 115: filename="perch_ _.pdf"; filename*=UTF-8''perch%C3%A9%20%C3%A8.pdf  <->  original_filename "perche' e'.pdf" (fixture 100)
        var raw115 = WireFixtures.Headers("115-doc-content-nonascii-name")["Content-Disposition"];
        var raw116 = WireFixtures.Headers("116-doc-content-star-name")["Content-Disposition"];

        Assert.Contains("filename=\"perch_ _.pdf\"", raw115, StringComparison.Ordinal);
        Assert.Equal("perch\U000000E9 \U000000E8.pdf", Read(raw115));
        Assert.Equal("perch\U000000E9 \U000000E8 star.pdf", Read(raw116));
    }

    [Fact]
    public void The_captured_ASCII_names_and_the_inline_preview_are_read()
    {
        Assert.Equal("fattura.pdf", Read(WireFixtures.Headers("108-doc-content")["Content-Disposition"]));
        Assert.Equal("note.txt", Read(WireFixtures.Headers("118-doc-content-text")["Content-Disposition"]));
        Assert.Equal("fattura.pdf", Read(WireFixtures.Headers("121-doc-preview-pdf")["Content-Disposition"])); // inline; filename=...; filename*=...
    }

    // ----- lettura: forme e varianti -----

    [Theory]
    [InlineData("attachment; filename=\"fattura.pdf\"; filename*=UTF-8''fattura.pdf", "fattura.pdf")]
    [InlineData("attachment; filename*=UTF-8''perch%C3%A9.pdf", "perch\U000000E9.pdf")]
    [InlineData("attachment; filename*=UTF-8''perch%C3%A9.pdf; filename=\"perch_.pdf\"", "perch\U000000E9.pdf")]
    [InlineData("attachment; filename=\"a b.pdf\"", "a b.pdf")]
    [InlineData("attachment; filename=plain.pdf", "plain.pdf")]
    [InlineData("attachment; filename=\"a;b.pdf\"", "a;b.pdf")]
    [InlineData("attachment; filename=\"a\\\"b\\\\c.pdf\"", "a\"b\\c.pdf")]
    [InlineData("attachment ;  filename = \"x.pdf\"", "x.pdf")]
    [InlineData("ATTACHMENT; FILENAME*=utf-8''x.pdf", "x.pdf")]
    [InlineData("attachment; filename*=Utf-8''x.pdf", "x.pdf")]
    [InlineData("attachment; filename*=UTF-8'it'perch%C3%A9.pdf", "perch\U000000E9.pdf")]
    [InlineData("attachment; filename*=ISO-8859-1''perch%E9.pdf", "perch\U000000E9.pdf")]
    [InlineData("attachment; filename*=iso-8859-1''perch%E9.pdf", "perch\U000000E9.pdf")]
    [InlineData("attachment; filename*=UTF-8''%F0%9F%98%80.pdf", "\U0001F600.pdf")]
    [InlineData("attachment; filename*=\"UTF-8''x.pdf\"", "x.pdf")]
    [InlineData("attachment; filename=\"100%.pdf\"", "100%.pdf")]
    [InlineData("attachment; filename=\"a.pdf\"; filename=\"b.pdf\"", "a.pdf")]
    [InlineData("attachment; filename*=UTF-8''%61%62%63.pdf", "abc.pdf")]
    [InlineData("attachment; filename*=UTF-8''%e2%82%ac.pdf", "\U000020AC.pdf")]
    public void A_file_name_is_read_from_the_right_parameter(string header, string expected)
    {
        Assert.Equal(expected, Read(header));
    }

    [Theory]
    [InlineData("attachment; filename*=windows-1252''x.pdf; filename=\"fallback.pdf\"")]
    [InlineData("attachment; filename*=UTF-8''%FF.pdf; filename=\"fallback.pdf\"")]
    [InlineData("attachment; filename*=UTF-8''%C3.pdf; filename=\"fallback.pdf\"")]
    [InlineData("attachment; filename*=UTF-8''abc%A; filename=\"fallback.pdf\"")]
    [InlineData("attachment; filename*=UTF-8''abc%; filename=\"fallback.pdf\"")]
    [InlineData("attachment; filename*=UTF-8''%GG.pdf; filename=\"fallback.pdf\"")]
    [InlineData("attachment; filename*=UTF-8; filename=\"fallback.pdf\"")]
    [InlineData("attachment; filename*=''x.pdf; filename=\"fallback.pdf\"")]
    [InlineData("attachment; filename*=UTF-8''; filename=\"fallback.pdf\"")]
    public void A_filename_star_that_is_not_valid_falls_back_to_filename(string header)
    {
        Assert.Equal("fallback.pdf", Read(header));
    }

    [Theory]
    [InlineData("attachment; filename*=windows-1252''x.pdf")]
    [InlineData("attachment; filename*=UTF-8''%FF.pdf")]
    [InlineData("attachment; filename*=UTF-8''")]
    [InlineData("attachment; filename=\"\"")]
    [InlineData("attachment; filename=")]
    [InlineData("attachment")]
    [InlineData("inline")]
    [InlineData("attachment; foo=bar")]
    [InlineData("attachment; filenames=\"a.pdf\"")]
    [InlineData("")]
    [InlineData("   ")]
    public void Without_a_usable_name_the_result_is_null(string header)
    {
        Assert.Null(Read(header));
    }

    [Fact]
    public void A_null_header_gives_null()
    {
        Assert.Null(Read(null));
    }

    [Fact]
    public void The_name_is_not_cleaned_up_it_is_the_name_the_server_has()
    {
        Assert.Equal("../../etc/passwd", Read("attachment; filename*=UTF-8''..%2F..%2Fetc%2Fpasswd"));
        Assert.Equal("a\r\nb", Read("attachment; filename*=UTF-8''a%0D%0Ab"));
        Assert.Equal("C:\\scansioni\\a.pdf", Read("attachment; filename*=UTF-8''C%3A%5Cscansioni%5Ca.pdf"));
    }

    [Fact]
    public void A_long_name_is_read_whole()
    {
        var name = new string('n', 4096) + ".pdf";

        Assert.Equal(name, Read("attachment; filename*=UTF-8''" + name));
    }

    [Fact]
    public void A_name_with_a_raw_non_ASCII_character_in_filename_star_is_not_an_ext_value_and_falls_back()
    {
        Assert.Equal("fallback.pdf", Read("attachment; filename*=UTF-8''perch\U000000E9.pdf; filename=\"fallback.pdf\""));
        Assert.Null(Read("attachment; filename*=UTF-8''perch\U000000E9.pdf"));
    }

    // ----- scrittura -----

    private static string Part(string fileName) => ContentDispositionHeader.FilePart("file", fileName);

    [Fact]
    public void An_ASCII_name_is_written_in_both_forms()
    {
        Assert.Equal("form-data; name=\"file\"; filename=\"fattura.pdf\"; filename*=utf-8''fattura.pdf", Part("fattura.pdf"));
    }

    [Fact]
    public void A_non_ASCII_name_has_the_percent_encoded_UTF8_in_filename_star_and_underscores_in_the_fallback()
    {
        // Come il Content-Disposition di risposta della cattura 115 (fallback "perch_ _.pdf", star "perch%C3%A9%20%C3%A8.pdf").
        Assert.Equal(
            "form-data; name=\"file\"; filename=\"perch_ _.pdf\"; filename*=utf-8''perch%C3%A9%20%C3%A8.pdf",
            Part("perch\U000000E9 \U000000E8.pdf"));
        Assert.EndsWith("filename*=utf-8''perch%C3%A9%20%C3%A8%20both.pdf", Part("perch\U000000E9 \U000000E8 both.pdf"), StringComparison.Ordinal); // cattura 54 (script T0.3)
    }

    [Fact]
    public void The_two_part_header_of_the_captured_upload_54_has_the_same_shape()
    {
        // Lo script T0.3 ha spedito: form-data; name="file"; filename="perche_ e_ both.pdf"; filename*=utf-8''perch%C3%A9%20%C3%A8%20both.pdf
        // e il server ha risposto 201 con original_filename "perch\U000000E9 \U000000E8 both.pdf" (fixture 54). Stessa forma: nome, filename, filename*.
        var header = Part("perch\U000000E9 \U000000E8 both.pdf");

        Assert.StartsWith("form-data; name=\"file\"; filename=\"", header, StringComparison.Ordinal);
        Assert.Contains("\"; filename*=utf-8''perch%C3%A9%20%C3%A8%20both.pdf", header, StringComparison.Ordinal);
    }

    public static TheoryData<string, string, string> WriteVectors() => new()
    {
        // nome, fallback atteso, filename* atteso
        { "a.pdf", "a.pdf", "a.pdf" },
        { "A-b_c.d~e.pdf", "A-b_c.d~e.pdf", "A-b_c.d~e.pdf" },
        { "a b.pdf", "a b.pdf", "a%20b.pdf" },
        { "l'a*b%c.pdf", "l'a*b%c.pdf", "l%27a%2Ab%25c.pdf" },
        { "a\"b\\c.pdf", "a_b_c.pdf", "a%22b%5Cc.pdf" },
        { "a;b=c.pdf", "a;b=c.pdf", "a%3Bb%3Dc.pdf" },
        { "(a)[b]{c}.pdf", "(a)[b]{c}.pdf", "%28a%29%5Bb%5D%7Bc%7D.pdf" },
        { "a,b@c:d/e?f.pdf", "a,b@c:d/e?f.pdf", "a%2Cb%40c%3Ad%2Fe%3Ff.pdf" },
        { "!#$&+-.^_`|~", "!#$&+-.^_`|~", "!#$&+-.^_`|~" },
        { "C:\\scansioni\\a.pdf", "C:_scansioni_a.pdf", "C%3A%5Cscansioni%5Ca.pdf" },
        { "a\r\nb.pdf", "a__b.pdf", "a%0D%0Ab.pdf" },
        { "a\0b\tc.pdf", "a_b_c.pdf", "a%00b%09c.pdf" },
        { "\U00000080.pdf", "_.pdf", "%C2%80.pdf" },
        { "\U000020AC.pdf", "_.pdf", "%E2%82%AC.pdf" },
        { "\U0001F600.pdf", "_.pdf", "%F0%9F%98%80.pdf" },
        { "x\U0001F600\U0001F4C1y.pdf", "x__y.pdf", "x%F0%9F%98%80%F0%9F%93%81y.pdf" },
        { "\U000000E9", "_", "%C3%A9" },
        { " a.pdf ", "a.pdf", "%20a.pdf%20" },
        { "   ", "documento", "%20%20%20" },
    };

    [Theory]
    [MemberData(nameof(WriteVectors))]
    public void A_name_is_written_with_the_attr_char_set_in_filename_star_and_the_server_style_fallback(string name, string fallback, string star)
    {
        Assert.Equal("form-data; name=\"file\"; filename=\"" + fallback + "\"; filename*=utf-8''" + star, Part(name));
    }

    [Fact]
    public void The_written_header_is_printable_ASCII_only_and_can_never_carry_a_line_break_or_a_closing_quote()
    {
        foreach (var name in new[] { "a\r\nX-Evil: 1.pdf", "a\"; x=\"y.pdf", "\0\U0000001F\U0000007F.pdf", "perch\U000000E9\U0001F600\"\\", "\n" })
        {
            var header = Part(name);

            Assert.All(header, c => Assert.InRange(c, ' ', '~'));
            Assert.DoesNotContain('\r', header);
            Assert.DoesNotContain('\n', header);
            // Il valore tra virgolette del ripiego non contiene mai una virgoletta interna ne' un backslash.
            var start = header.IndexOf("filename=\"", StringComparison.Ordinal) + "filename=\"".Length;
            var end = header.IndexOf("\"; filename*=", StringComparison.Ordinal);
            var inner = header.Remove(end).Remove(0, start);
            Assert.DoesNotContain('"', inner);
            Assert.DoesNotContain('\\', inner);
        }
    }

    [Theory]
    [InlineData("a.pdf")]
    [InlineData("perch\U000000E9 \U000000E8.pdf")]
    [InlineData("a\"b\\c;d=e'f*g%h.pdf")]
    [InlineData("\U0001F600 \U0001F4C1.pdf")]
    [InlineData("a\r\nb.pdf")]
    [InlineData("C:\\scansioni\\a.pdf")]
    [InlineData("  spazi  .pdf")]
    [InlineData("\U000020AC\U000000E9\U00004E2D.pdf")]
    public void A_written_name_is_read_back_exactly(string name)
    {
        // Coerenza lettura/scrittura: filename* ha sempre il nome intero, anche quando il ripiego e' stato storpiato.
        Assert.Equal(name, Read(Part(name)));
    }

    [Fact]
    public void A_name_of_4096_characters_is_written_whole_and_read_back()
    {
        var name = new string('n', 4000) + "\U000000E9\U0001F600.pdf";

        var header = Part(name);

        Assert.EndsWith("%C3%A9%F0%9F%98%80.pdf", header, StringComparison.Ordinal);
        Assert.Equal(name, Read(header));
    }

    [Fact]
    public void An_empty_name_and_a_lone_surrogate_are_refused()
    {
        Assert.Equal("fileName", Assert.Throws<ArgumentException>(() => Part(string.Empty)).ParamName);
        Assert.Equal("fileName", Assert.Throws<ArgumentException>(() => Part("a" + new string((char)0xD800, 1) + ".pdf")).ParamName);
        Assert.Equal("fileName", Assert.Throws<ArgumentException>(() => Part(new string((char)0xDC00, 1))).ParamName);
        Assert.Throws<ArgumentNullException>(() => ContentDispositionHeader.FilePart("file", null!));
        Assert.Throws<ArgumentNullException>(() => ContentDispositionHeader.FilePart(null!, "a.pdf"));
    }

    [Fact]
    public void A_text_field_part_is_form_data_with_its_name()
    {
        Assert.Equal("form-data; name=\"owner\"", ContentDispositionHeader.FormField("owner"));
        Assert.Equal("form-data; name=\"folder_id\"", ContentDispositionHeader.FormField("folder_id"));
        Assert.Throws<ArgumentNullException>(() => ContentDispositionHeader.FormField(null!));
    }

    [Fact]
    public void The_percent_set_is_the_one_of_the_server_for_every_ASCII_character()
    {
        // Oracolo: IsAttrChar di ContentDisposition.cs del server (RFC 5987 attr-char), scritto qui in modo indipendente.
        var allowed = new HashSet<char>("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!#$&+-.^_`|~");
        for (var code = 0x21; code <= 0x7E; code++)
        {
            var c = (char)code;
            var header = Part("a" + c + "b");
            var star = header.Substring(header.IndexOf("filename*=utf-8''", StringComparison.Ordinal) + "filename*=utf-8''".Length);

            var expected = allowed.Contains(c) ? "a" + c + "b" : "a%" + code.ToString("X2", System.Globalization.CultureInfo.InvariantCulture) + "b";
            Assert.Equal(expected, star);
        }
    }
}
