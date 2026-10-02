using System.Text.Json;
using Filemaster.Domain;

namespace Filemaster.UnitTests.Domain;

/// <summary>
/// <see cref="ArxivarMetadata.From"/> legge la sezione <c>arxivar</c> dei metadati liberi di un documento, quella che
/// <c>arxivar-sync</c> di Sharp-a-File scrive (<c>ArxivarMapping.Metadata</c>). I metadati sono JSON arbitrario di chi ha
/// caricato il documento: la lettura non deve mai lanciare, e <c>docnumber</c> segue la regola del server per il filtro
/// <c>metadata={"arxivar":{"docnumber":N}}</c> (contenimento JSON: stesso tipo, numeri confrontati per valore).
/// </summary>
public sealed class ArxivarMetadataTests
{
    private static ArxivarMetadata? From(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ArxivarMetadata.From(document.RootElement);
    }

    private static ArxivarMetadata OnlyDocnumber(int docnumber) =>
        new(docnumber, null, null, null, null, null, null, null, null, null);

    [Fact]
    public void From_reads_every_field_that_arxivar_sync_writes()
    {
        // La forma di ArxivarMapping.Metadata: chiavi italiane, docnumber e revisione numeri, il resto stringhe,
        // data_documento come yyyy-MM-dd.
        var result = From("""
            {"arxivar":{"docnumber":61617,"categoria":"ASSOCIATO.DOC-CCCIA","oggetto":"Visura camerale","stato":"VALIDO",
            "numero":"42","data_documento":"2026-09-21","protocollo":"P-7","anno":"2026","revisione":3,
            "impronta":"545d3b8bd60954abc49fca0b2e007f83"}}
            """);

        Assert.Equal(
            new ArxivarMetadata(
                61617,
                "ASSOCIATO.DOC-CCCIA",
                "Visura camerale",
                "VALIDO",
                "42",
                new DateTime(2026, 9, 21, 0, 0, 0, DateTimeKind.Unspecified),
                "P-7",
                "2026",
                3,
                "545d3b8bd60954abc49fca0b2e007f83"),
            result);
    }

    [Fact]
    public void From_with_only_some_fields_leaves_the_others_null()
    {
        // Documento caricato a mano con una sezione arxivar parziale (fixture reale del server dev).
        var result = From("""{"arxivar":{"docnumber":12345,"categoria":"X"}}""");

        Assert.Equal(new ArxivarMetadata(12345, "X", null, null, null, null, null, null, null, null), result);
        Assert.Equal(OnlyDocnumber(7), From("""{"arxivar":{"docnumber":7}}"""));
    }

    [Fact]
    public void From_ignores_unknown_fields_and_the_rest_of_the_metadata()
    {
        var result = From("""{"tags":["a","b"],"arxivar":{"docnumber":7,"nuovo":{"x":[1,2]},"altro":true},"owner":"x"}""");

        Assert.Equal(OnlyDocnumber(7), result);
    }

    [Theory]
    [InlineData("{}")] // nessun metadato: il server emette sempre almeno questo
    [InlineData("""{"tags":["a"],"owner":"x"}""")] // metadati di altri, nessuna sezione
    [InlineData("""{"arxivar":null}""")]
    [InlineData("""{"arxivar":[]}""")]
    [InlineData("""{"arxivar":[{"docnumber":1}]}""")]
    [InlineData("""{"arxivar":"docnumber"}""")]
    [InlineData("""{"arxivar":5}""")]
    [InlineData("""{"arxivar":true}""")]
    [InlineData("""{"arxivar":{}}""")] // sezione vuota: nessun docnumber, non e' un profilo ARXivar
    [InlineData("""{"arxivar":{"categoria":"X","oggetto":"Senza numero"}}""")]
    [InlineData("""{"ARXIVAR":{"docnumber":1}}""")] // i nomi sono case-sensitive, come nel filtro del server
    [InlineData("""{"Arxivar":{"docnumber":1}}""")]
    [InlineData("""{"arxivar":{"DocNumber":1}}""")]
    [InlineData("""{"arxivar":{"Docnumber":1}}""")]
    [InlineData("""{"x":{"arxivar":{"docnumber":1}}}""")] // la sezione sta al primo livello, non annidata
    public void From_without_a_usable_arxivar_section_returns_null(string json)
    {
        Assert.Null(From(json));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"arxivar\":{\"docnumber\":1}}]")]
    [InlineData("\"arxivar\"")]
    [InlineData("5")]
    [InlineData("true")]
    [InlineData("null")]
    public void From_with_metadata_that_is_not_an_object_returns_null(string json)
    {
        Assert.Null(From(json));
    }

    [Fact]
    public void From_with_an_undefined_element_returns_null_without_throwing()
    {
        // default(JsonElement) non e' un valore prodotto dal client, ma chi costruisce un Document a mano puo' passarlo.
        Assert.Equal(JsonValueKind.Undefined, default(JsonElement).ValueKind);
        Assert.Null(ArxivarMetadata.From(default));
    }

    [Theory]
    [InlineData("\"61617\"")] // stringa: per il filtro del server "61617" non e' 61617
    [InlineData("\"\"")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("[61617]")]
    [InlineData("{\"value\":61617}")]
    [InlineData("12.5")] // decimale non intero
    [InlineData("61617.5")]
    [InlineData("0.5")]
    [InlineData("-0.5")]
    [InlineData("1.0000001")]
    [InlineData("99999999999")] // numero grande: oltre l'int
    [InlineData("2147483648")] // int.MaxValue + 1
    [InlineData("-2147483649")] // int.MinValue - 1
    [InlineData("1e10")]
    [InlineData("1e400")] // oltre il double: STJ lo legge come infinito
    [InlineData("-1e400")]
    [InlineData("123456789012345678901234567890")]
    public void From_with_a_docnumber_that_is_not_an_int_returns_null(string docnumber)
    {
        Assert.Null(From("{\"arxivar\":{\"docnumber\":" + docnumber + ",\"categoria\":\"X\"}}"));
    }

    [Theory]
    [InlineData("61617", 61617)]
    [InlineData("1", 1)]
    [InlineData("61617.0", 61617)] // il filtro del server confronta i numeri per valore: 61617.0 combacia con 61617
    [InlineData("6.1617e4", 61617)]
    [InlineData("1e3", 1000)]
    [InlineData("1E+3", 1000)]
    [InlineData("2147483647", int.MaxValue)]
    [InlineData("2147483647.0", int.MaxValue)]
    [InlineData("-2147483648", int.MinValue)]
    [InlineData("0", 0)] // il server non valida il segno: accettato, anche se ARXivar scrive solo valori >= 1
    [InlineData("-5", -5)]
    [InlineData("-0.0", 0)]
    public void From_accepts_a_docnumber_that_is_an_integer_value_within_int_range(string docnumber, int expected)
    {
        var result = From("{\"arxivar\":{\"docnumber\":" + docnumber + "}}");

        Assert.Equal(OnlyDocnumber(expected), result);
    }

    [Theory]
    [InlineData("""{"arxivar":{"docnumber":1,"docnumber":2}}""", 2)]
    [InlineData("""{"arxivar":{"docnumber":2,"docnumber":1}}""", 1)]
    [InlineData("""{"arxivar":{"docnumber":1},"arxivar":{"docnumber":2}}""", 2)]
    [InlineData("""{"arxivar":{"docnumber":"x","docnumber":3}}""", 3)]
    public void From_with_a_duplicate_key_uses_the_last_one_like_JsonElement_TryGetProperty(string json, int expected)
    {
        // Il server non toglie le chiavi duplicate dai metadati. Qui vale l'ultima, come per JsonElement.TryGetProperty
        // (verificato su ogni runtime dei test, quindi anche su System.Text.Json 8.0.5 dell'asset netstandard2.0).
        Assert.Equal(OnlyDocnumber(expected), From(json));
    }

    [Fact]
    public void From_with_a_duplicate_key_whose_last_value_is_invalid_returns_null()
    {
        // L'ultima vince anche quando e' quella sbagliata: nessun ripiego sul valore precedente.
        Assert.Null(From("""{"arxivar":{"docnumber":3,"docnumber":"x"}}"""));
        Assert.Null(From("""{"arxivar":{"docnumber":3},"arxivar":null}"""));
    }

    [Fact]
    public void From_with_a_field_of_the_wrong_type_leaves_that_field_null_and_keeps_the_rest()
    {
        var result = From("""
            {"arxivar":{"docnumber":1,"categoria":5,"oggetto":{"a":1},"stato":["x"],"numero":7,"data_documento":20260921,
            "protocollo":true,"anno":2026,"revisione":"3","impronta":null}}
            """);

        Assert.Equal(OnlyDocnumber(1), result);
    }

    [Theory]
    [InlineData("3", 3)]
    [InlineData("0", 0)]
    [InlineData("-1", -1)]
    [InlineData("3.0", 3)]
    [InlineData("1e2", 100)]
    [InlineData("3.5", null)]
    [InlineData("\"3\"", null)]
    [InlineData("99999999999", null)]
    [InlineData("null", null)]
    [InlineData("true", null)]
    public void From_reads_the_revision_as_an_int_or_leaves_it_null(string revision, int? expected)
    {
        var result = From("{\"arxivar\":{\"docnumber\":1,\"revisione\":" + revision + "}}");

        Assert.NotNull(result);
        Assert.Equal(expected, result.Revision);
    }

    [Theory]
    [InlineData("2026-09-21", 2026, 9, 21)]
    [InlineData("2026-02-28", 2026, 2, 28)]
    [InlineData("2024-02-29", 2024, 2, 29)]
    [InlineData("0001-01-01", 1, 1, 1)]
    public void From_reads_the_document_date_as_a_calendar_date(string text, int year, int month, int day)
    {
        var result = From("{\"arxivar\":{\"docnumber\":1,\"data_documento\":\"" + text + "\"}}");

        Assert.NotNull(result);
        var date = Assert.IsType<DateTime>(result.DocumentDate);
        Assert.Equal(new DateTime(year, month, day), date);
        Assert.Equal(TimeSpan.Zero, date.TimeOfDay); // solo la data, nessuna ora
        Assert.Equal(DateTimeKind.Unspecified, date.Kind); // una data di calendario non ha fuso
    }

    [Theory]
    [InlineData("2026-9-21")]
    [InlineData("2026-09-1")]
    [InlineData("21/09/2026")]
    [InlineData("09/21/2026")]
    [InlineData("20260921")]
    [InlineData("2026-02-30")] // data inesistente
    [InlineData("2026-13-01")]
    [InlineData("2023-02-29")] // non bisestile
    [InlineData("2026-09-21T10:00:00")]
    [InlineData("2026-09-21T10:00:00Z")]
    [InlineData("2026-09-21 ")]
    [InlineData(" 2026-09-21")]
    [InlineData("2026-09-21\\n")] // newline finale (JSON \n): nessun trim, come per gli id
    [InlineData("")]
    [InlineData("oggi")]
    public void From_leaves_the_document_date_null_unless_it_is_exactly_yyyy_MM_dd(string text)
    {
        var result = From("{\"arxivar\":{\"docnumber\":1,\"data_documento\":\"" + text + "\"}}");

        Assert.NotNull(result);
        Assert.Null(result.DocumentDate);
    }

    [Fact]
    public void From_keeps_text_fields_exactly_as_stored_without_trimming_or_conversion()
    {
        var subject = "  Citt\U000000E0 dell'associato <b>  "; // a accentata, apostrofo, markup: nessuna normalizzazione
        var result = From("{\"arxivar\":{\"docnumber\":1,\"oggetto\":\"" + subject + "\",\"anno\":\"\",\"numero\":\"007\"}}");

        Assert.NotNull(result);
        Assert.Equal(subject, result.Subject);
        Assert.Equal(string.Empty, result.Year); // vuoto non e' null: il server non scrive mai un campo vuoto, ma se c'e' si conserva
        Assert.Equal("007", result.Number); // numero e' testo: gli zeri iniziali restano
    }

    [Fact]
    public void From_result_stays_valid_after_the_source_JsonDocument_is_disposed()
    {
        // Il risultato contiene solo stringhe e numeri copiati: nessun JsonElement trattenuto. Se ne trattenesse uno,
        // dopo il Dispose del JsonDocument ogni lettura (anche ToString) lancerebbe ObjectDisposedException.
        ArxivarMetadata? result;
        using (var document = JsonDocument.Parse("""{"arxivar":{"docnumber":9,"oggetto":"Visura","revisione":1,"data_documento":"2026-09-21"}}"""))
        {
            result = ArxivarMetadata.From(document.RootElement);
        }

        Assert.NotNull(result);
        Assert.Equal(9, result.Docnumber);
        Assert.Equal("Visura", result.Subject);
        Assert.Equal(1, result.Revision);
        Assert.Equal(new DateTime(2026, 9, 21), result.DocumentDate);
        Assert.Contains("Visura", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void From_with_an_isolated_surrogate_escape_in_a_value_leaves_that_field_null_and_keeps_the_rest()
    {
        // Il server conserva qualunque JSON valido per il suo parser, compreso "\ud800" (surrogato isolato): System.Text.Json lo legge,
        // ma GetString lancia InvalidOperationException. La promessa "non lancia mai" vale anche qui: il campo illeggibile e' null.
        var result = From("""{"arxivar":{"docnumber":1,"categoria":"a\ud800","oggetto":"ok","data_documento":"\ud800","anno":"2026"}}""");

        Assert.Equal(
            new ArxivarMetadata(1, null, "ok", null, null, null, null, "2026", null, null),
            result);
    }

    [Theory]
    [InlineData("""{"arxivar":{"docnumber":1,"x\ud800":2,"oggetto":"ok"}}""")]
    [InlineData("""{"y\ud800":1,"arxivar":{"docnumber":1,"oggetto":"ok"}}""")]
    [InlineData("""{"arxivar":{"docnumber":1,"oggetto":"ok"},"z\ud800":1}""")]
    [InlineData("""{"arxivar":{"docnumber":1,"oggetto":"ok"},"\ud800\ud800":0}""")]
    [InlineData("""{"arxivar":{"docnumber":1,"oggetto":"ok","\ud800\ud800":2}}""")]
    [InlineData("""{"arxivar":{"docnumber":1,"oggett\ud800":2,"oggetto":"ok"}}""")]
    [InlineData("""{"arxivar":{"\ud800":2,"docnumber":1,"oggetto":"ok"}}""")]
    [InlineData("""{"arxivar":{"\ud800\ud800":2,"docnumber":1,"oggetto":"ok"}}""")]
    [InlineData("""{"arxiva\ud800":0,"arxivar":{"docnumber":1,"oggetto":"ok"}}""")]
    [InlineData("""{"\ud800\ud800":0,"arxivar":{"docnumber":1,"oggetto":"ok"}}""")]
    [InlineData("""{"arxivar":{"docnumbe\ud800":0,"docnumber":1,"oggetto":"ok"}}""")]
    [InlineData("""{"\ud800\ud800\ud800":0,"arxivar":{"\ud800\ud800\ud800\ud800":0,"docnumber":1,"oggetto":"ok"}}""")]
    public void From_with_an_isolated_surrogate_escape_in_a_name_never_throws_and_never_invents_a_docnumber(string json)
    {
        // Anche un NOME con un surrogato isolato fa lanciare TryGetProperty: il confronto con il nome cercato procede carattere per
        // carattere e decodifica l'escape solo se quanto lo precede coincide (e la ricerca parte dall'ultima proprieta'). Cosa si riesce
        // ancora a leggere dipende dal parser: il contratto e' non lanciare e non inventare un docnumber.
        var result = Record.Exception(() => From(json));
        Assert.Null(result);

        var metadata = From(json);
        Assert.True(metadata is null || metadata.Docnumber == 1);
    }

    [Fact]
    public void From_results_are_equal_by_value_unlike_Document()
    {
        // Solo scalari: due letture dello stesso testo, da JsonDocument diversi, danno record uguali.
        var json = """{"arxivar":{"docnumber":5,"categoria":"X","revisione":2,"data_documento":"2026-01-02"}}""";

        var first = From(json);
        var second = From(json);

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second!.GetHashCode());
        Assert.NotEqual(first, From("""{"arxivar":{"docnumber":6,"categoria":"X","revisione":2,"data_documento":"2026-01-02"}}"""));
    }
}
