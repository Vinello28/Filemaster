using System.Text.Encodings.Web;
using System.Text.Json;

namespace Filemaster.Infrastructure;

/// <summary>
/// Il punto d'ingresso della lettura JSON del livello wire: dai byte di una risposta di successo a un modello, in memoria e senza
/// rete. Scelta di serializzazione (documentata qui, vale per tutto <c>Wire/</c>): lettura manuale con <see cref="JsonElement"/> e
/// scrittura manuale con <see cref="Utf8JsonWriter"/>, niente DTO per <c>System.Text.Json</c>, niente reflection e nessuna
/// <see cref="JsonSerializerOptions"/> condivisa. Il motivo e' il controllo: ogni errore di forma diventa un
/// <see cref="Filemaster.Domain.UnexpectedResponseException"/> con lo status vero, un campo assente e un <c>null</c> si distinguono
/// dai tipi sbagliati, i metadati si copiano (<c>Clone()</c>) prima che il documento JSON sia smaltito, e il comportamento e' lo
/// stesso su netstandard2.0, net8.0 e net10.0 (un serializzatore a reflection varia con la versione di <c>System.Text.Json</c>).
/// </summary>
internal static class WireJson
{
    /// <summary>Il <c>Content-Type</c> dei corpi JSON che il client spedisce: il server rifiuta con 415 un corpo senza (o con un altro) tipo.</summary>
    internal const string ContentType = "application/json";

    // Il server legge e scrive i metadati con la profondita' di default di System.Text.Json (64): radice + items + documento + metadati
    // arrivano al massimo a 67. 256 lascia margine e resta un tetto (JsonDocument.Parse e' iterativo: nessun rischio per lo stack).
    private const int MaxDepth = 256;

    // Decodifica rigida (lancia sui byte non validi); GetCharCount non costruisce nessuna stringa.
    private static readonly System.Text.UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // Un solo valore immutabile, nessuna JsonSerializerOptions: compatto (nessuno spazio), con il minimo di escape (il testo dell'utente
    // resta leggibile e piu' corto; e' JSON valido anche con '+', apici e lettere accentate non escapati). I surrogati isolati si
    // rifiutano prima di scrivere (PercentEncoding.RequireWellFormed): il writer non li accetterebbe.
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// Scrive un corpo JSON compatto in UTF-8 (senza BOM). Il chiamante scrive esattamente i campi che il server accetta: il server rifiuta
    /// con 400 una proprieta' sconosciuta, quindi un campo in piu' non e' mai innocuo.
    /// </summary>
    /// <param name="write">La scrittura dell'oggetto, da <c>WriteStartObject</c> a <c>WriteEndObject</c>.</param>
    internal static byte[] Write(Action<Utf8JsonWriter> write)
    {
        Guard.NotNull(write);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            write(writer);
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Legge il corpo di una risposta riuscita come un oggetto JSON e lo converte con <paramref name="read"/>. Il documento JSON e'
    /// aperto e smaltito qui: quello che <paramref name="read"/> restituisce non deve contenere elementi che puntano al documento
    /// (i metadati si copiano con <see cref="WireObject.OptionalObjectCopy"/>).
    /// </summary>
    /// <param name="body">I byte del corpo; null o vuoto e' non interpretabile.</param>
    /// <param name="context">Lo status e l'id di correlazione della risposta.</param>
    /// <param name="what">Che cosa si legge, in italiano, per i messaggi (<c>documento</c>).</param>
    /// <param name="read">La conversione dall'oggetto radice al modello.</param>
    /// <exception cref="Filemaster.Domain.UnexpectedResponseException">Il corpo non e' JSON valido, non e' un oggetto o non ha la forma attesa.</exception>
    internal static T ReadObject<T>(byte[]? body, WireContext context, string what, Func<WireObject, T> read)
    {
        Guard.NotNull(context);
        Guard.NotNull(read);
        using var document = Parse(body, context);
        return read(WireObject.Root(document.RootElement, what, context));
    }

    /// <summary>
    /// Apre il corpo come documento JSON. Tollera un BOM UTF-8 iniziale (<c>System.Text.Json</c> lo rifiuta; lo fa gia' <c>ProblemBody</c>)
    /// e la profondita' fino a 256; un corpo vuoto o non JSON e' non interpretabile, con l'eccezione del parser come causa.
    /// </summary>
    internal static JsonDocument Parse(byte[]? body, WireContext context)
    {
        Guard.NotNull(context);
        if (body is null || body.Length == 0)
        {
            throw context.Unexpected("Risposta non interpretabile: il corpo e' vuoto.");
        }

        var start = body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF ? 3 : 0;
        try
        {
            // JsonDocument.Parse accetta UTF-8 non valido e lo restituisce intatto: poi GetString(), GetRawText() e il ToString() dei record lanciano.
            // Un JSON e' UTF-8 per contratto e il server non ne produce di invalido: lo si rifiuta qui, in un colpo solo e per qualunque posizione
            // (valori, nomi di proprieta', metadati). I surrogati isolati scritti come escape (u+d800) sono invece JSON valido e il server li conserva:
            // restano, e li gestisce chi legge i testi (WireObject).
            _ = StrictUtf8.GetCharCount(body, start, body.Length - start);
            return JsonDocument.Parse(new ReadOnlyMemory<byte>(body, start, body.Length - start), new JsonDocumentOptions { MaxDepth = MaxDepth });
        }
        catch (JsonException exception)
        {
            throw context.Unexpected("Risposta non interpretabile: il corpo non e' JSON valido.", exception);
        }
        catch (ArgumentException exception)
        {
            // DecoderFallbackException e' un ArgumentException: UTF-8 non valido.
            throw context.Unexpected("Risposta non interpretabile: il corpo non e' UTF-8 valido.", exception);
        }
    }
}
