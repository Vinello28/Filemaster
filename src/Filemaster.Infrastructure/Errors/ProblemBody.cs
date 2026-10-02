using System.Text.Json;

namespace Filemaster.Infrastructure;

/// <summary>
/// Quello che interessa di un corpo <c>application/problem+json</c> del server (<c>{"type":"/problems/not-found","title":...,
/// "status":404,"detail":...,"request_id":...}</c>): lo slug del <c>type</c>, il <c>detail</c> e il <c>request_id</c>. La lettura
/// e' tollerante per costruzione: un corpo vuoto, troppo grande, non JSON, troncato, non un oggetto, o con campi del tipo sbagliato
/// non lancia mai (nessuna eccezione di <c>System.Text.Json</c> esce da qui) e dice solo "non ho trovato niente".
/// </summary>
internal sealed class ProblemBody
{
    /// <summary>
    /// Quanti byte di corpo si leggono al massimo (16 KiB). Un problem+json del server e' di poche centinaia di byte: un corpo piu'
    /// grande non e' un errore del server (e' una pagina di un proxy, o un tentativo di far allocare memoria) e si ignora.
    /// </summary>
    internal const int MaxBytes = 16 * 1024;

    private const string SlugMarker = "/problems/";
    private const int MaxSlugLength = 64;
    private const int MaxIdLength = 128;
    private const int MaxJsonDepth = 8;

    private ProblemBody(string? slug, string? detail, string? requestId)
    {
        Slug = slug;
        Detail = detail;
        RequestId = requestId;
    }

    /// <summary>Lo slug del <c>type</c> senza il prefisso <c>/problems/</c> (<c>not-found</c>); null se manca o non e' nella forma del server.</summary>
    internal string? Slug { get; }

    /// <summary>Il campo <c>detail</c>; null se manca, e' vuoto o non e' una stringa.</summary>
    internal string? Detail { get; }

    /// <summary>Il campo <c>request_id</c>; null se manca o non e' un id plausibile (vedi <see cref="CleanId"/>).</summary>
    internal string? RequestId { get; }

    /// <summary>
    /// Legge il corpo di una risposta d'errore. Restituisce null se il corpo e' assente, vuoto, piu' lungo di
    /// <see cref="MaxBytes"/>, non e' JSON o non e' un oggetto JSON; altrimenti un oggetto i cui campi mancanti o di tipo sbagliato
    /// sono null. Tollera un BOM UTF-8 all'inizio (STJ lo rifiuta).
    /// </summary>
    /// <param name="body">I byte del corpo, gia' limitati da chi legge dalla rete.</param>
    internal static ProblemBody? Parse(byte[]? body)
    {
        if (body is null || body.Length == 0 || body.Length > MaxBytes)
        {
            return null;
        }

        var start = body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF ? 3 : 0;
        try
        {
            using var document = JsonDocument.Parse(new ReadOnlyMemory<byte>(body, start, body.Length - start), new JsonDocumentOptions { MaxDepth = MaxJsonDepth });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return new ProblemBody(
                SlugOf(ReadString(root, "type")),
                NonEmpty(ReadString(root, "detail")),
                CleanId(ReadString(root, "request_id")));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Un id di correlazione plausibile, oppure null: non vuoto, al massimo 128 caratteri, senza caratteri di controllo. Vale per il
    /// <c>request_id</c> del corpo e per l'intestazione <c>X-Request-ID</c>: finiscono nelle eccezioni e nei log, quindi non devono
    /// poter portare testo arbitrario.
    /// </summary>
    internal static string? CleanId(string? value)
    {
        if (string.IsNullOrEmpty(value) || value!.Length > MaxIdLength)
        {
            return null;
        }

        foreach (var c in value)
        {
            if (c < ' ' || c == '\u007F')
            {
                return null;
            }
        }

        return value;
    }

    // Una stringa JSON, o null se la proprieta' manca o e' di un altro tipo. GetString() lancia InvalidOperationException su un
    // surrogato isolato (\ud800): il valore e' inutilizzabile e si tratta come assente.
    private static string? ReadString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        try
        {
            return value.GetString();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    // Lo slug e' cio' che segue l'ultimo "/problems/" del type (il server scrive "/problems/not-found"; un gateway potrebbe
    // anteporre un host). Solo lettere e cifre ASCII, punto, trattino e sottolineatura, al massimo 64: altrimenti non e' uno slug.
    private static string? SlugOf(string? type)
    {
        if (type is null)
        {
            return null;
        }

        var index = type.LastIndexOf(SlugMarker, StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }

        var slug = type.Substring(index + SlugMarker.Length);
        if (slug.Length == 0 || slug.Length > MaxSlugLength)
        {
            return null;
        }

        foreach (var c in slug)
        {
            var allowed = (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '-' || c == '_' || c == '.';
            if (!allowed)
            {
                return null;
            }
        }

        return slug;
    }
}
