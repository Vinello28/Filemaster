using System.Text;
using System.Text.Json;

namespace Filemaster.Application;

/// <summary>
/// Controlli condivisi dai metodi <c>Validate()</c> di richieste e query. Ognuno riceve il nome dell'argomento (la
/// proprieta' controllata) e lo mette in <see cref="ArgumentException.ParamName"/>: in un <c>Validate()</c> senza
/// parametri un <c>new ArgumentException(messaggio, nameof(Proprieta))</c> scritto a mano farebbe scattare CA2208.
/// I limiti sono quelli esatti del server (Sharp-a-File, ramo dev): una richiesta che li supera il server la rifiuta con
/// un 400, quindi il client la ferma prima, con un'eccezione di argomento e non con <c>InvalidRequestException</c>.
/// </summary>
internal static class RequestChecks
{
    /// <summary>Lunghezza massima, in caratteri, dei testi indicizzati dal server: nome di cartella, owner e tag.</summary>
    internal const int IndexedTextLength = 255;

    /// <summary>Dimensione massima di un oggetto JSON di metadati (o di filtro sui metadati), in byte UTF-8.</summary>
    internal const int JsonObjectBytes = 64 * 1024;

    /// <summary>Quanti valori JSON al massimo in un filtro sui metadati (<see cref="JsonFilterComplexity"/>).</summary>
    internal const int FilterNodes = 64;

    /// <summary>Quanti livelli di annidamento al massimo in un filtro sui metadati, radice compresa.</summary>
    internal const int FilterDepth = 16;

    /// <summary>Rifiuta un id o un codice vuoto (<c>default</c>); <paramref name="isEmpty"/> null (valore assente) passa.</summary>
    internal static void NotEmpty(bool? isEmpty, string paramName, string what)
    {
        if (isEmpty == true)
        {
            throw new ArgumentException(
                $"{what} e' vuoto (default): indicare un valore valido, oppure null per ometterlo.",
                paramName);
        }
    }

    /// <summary>Rifiuta un testo il cui valore grezzo supera <paramref name="maxBytes"/> byte UTF-8 (nessun trim).</summary>
    internal static void MaxUtf8Bytes(string? value, int maxBytes, string paramName)
    {
        if (value is not null && Encoding.UTF8.GetByteCount(value) > maxBytes)
        {
            throw new ArgumentException($"Il valore supera il limite di {maxBytes} byte (UTF-8).", paramName);
        }
    }

    /// <summary>Rifiuta un testo che, tolti gli spazi ai bordi, supera <paramref name="maxLength"/> caratteri.</summary>
    internal static void MaxTrimmedLength(string? value, int maxLength, string paramName)
    {
        if (value is not null && value.Trim().Length > maxLength)
        {
            throw new ArgumentException(
                $"Il valore supera il limite di {maxLength} caratteri (spazi ai bordi esclusi).",
                paramName);
        }
    }

    /// <summary>
    /// Rifiuta un elemento JSON che non e' un oggetto, o il cui testo (<see cref="JsonElement.GetRawText"/>) supera
    /// <see cref="JsonObjectBytes"/> byte UTF-8. <c>default(JsonElement)</c> ha <c>ValueKind</c> <c>Undefined</c> e non e'
    /// un oggetto: si controlla prima di leggerne il testo, che altrimenti lancerebbe <see cref="InvalidOperationException"/>.
    /// </summary>
    internal static void JsonObject(JsonElement value, string paramName)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Il valore deve essere un oggetto JSON (non null, non un array, non un valore semplice).", paramName);
        }

        if (Encoding.UTF8.GetByteCount(value.GetRawText()) > JsonObjectBytes)
        {
            throw new ArgumentException($"L'oggetto JSON supera il limite di {JsonObjectBytes} byte (UTF-8).", paramName);
        }
    }

    /// <summary>
    /// Rifiuta un filtro JSON troppo complesso, con le regole del server: al massimo <see cref="FilterNodes"/> valori in
    /// tutto (radice, oggetti, array e valori semplici, contati uno per uno; i nomi delle proprieta' no) e
    /// <see cref="FilterDepth"/> livelli di annidamento, con la radice a profondita' 1. Il server genera una condizione SQL per
    /// ogni valore, e SQL Server limita a 32 le subquery annidate.
    /// </summary>
    internal static void JsonFilterComplexity(JsonElement filter, string paramName)
    {
        var nodes = 0;
        Visit(filter, 1, ref nodes, paramName);
    }

    /// <summary>Rifiuta un testo vuoto o di soli spazi (null compreso).</summary>
    internal static void NotBlank(string? value, string paramName)
    {
        if (value is null || value.Trim().Length == 0)
        {
            throw new ArgumentException("Il valore non puo' essere vuoto ne' di soli spazi.", paramName);
        }
    }

    /// <summary>
    /// Rifiuta un tipo di contenuto (MIME) che l'adapter non potrebbe spedire come header: solo ASCII stampabile (niente
    /// a capo ne' caratteri di controllo, che permetterebbero di iniettare altri header) e forma <c>tipo/sottotipo</c>
    /// con caratteri di <c>token</c> (RFC 9110), anche seguita da parametri (<c>; charset=utf-8</c>). Un valore vuoto o
    /// di soli spazi vale "assente", come per il server, che in quel caso riconosce il tipo dai primi byte.
    /// </summary>
    internal static void MediaType(string? value, string paramName)
    {
        // Il controllo su null e' ridondante per il compilatore dei TFM moderni, ma su netstandard2.0 IsNullOrWhiteSpace
        // non ha l'annotazione NotNullWhen.
        if (value is null || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        foreach (var c in value)
        {
            if (c < 0x20 || c > 0x7E)
            {
                throw new ArgumentException("Il tipo di contenuto puo' avere solo caratteri ASCII stampabili.", paramName);
            }
        }

        var semicolon = value.IndexOf(';');
        var essence = (semicolon < 0 ? value : value.Substring(0, semicolon)).Trim();
        var slash = essence.IndexOf('/');
        var wellFormed = slash > 0
            && slash < essence.Length - 1
            && IsToken(essence, 0, slash)
            && IsToken(essence, slash + 1, essence.Length - slash - 1);
        if (!wellFormed)
        {
            throw new ArgumentException("Il tipo di contenuto deve avere la forma 'tipo/sottotipo', per esempio 'application/pdf'.", paramName);
        }
    }

    private static void Visit(JsonElement element, int depth, ref int nodes, string paramName)
    {
        nodes++;
        if (nodes > FilterNodes || depth > FilterDepth)
        {
            throw new ArgumentException(
                $"Il filtro sui metadati e' troppo complesso: al massimo {FilterNodes} valori e {FilterDepth} livelli di annidamento.",
                paramName);
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    Visit(property.Value, depth + 1, ref nodes, paramName);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Visit(item, depth + 1, ref nodes, paramName);
                }

                break;
        }
    }

    // Caratteri di token (RFC 9110): cifre e lettere ASCII e !#$%&'*+-.^_`|~ . Un confronto ASCII esplicito, mai
    // char.IsLetterOrDigit (accetterebbe lettere e cifre Unicode).
    private static bool IsToken(string text, int start, int length)
    {
        for (var i = start; i < start + length; i++)
        {
            var c = text[i];
            var isTokenChar = (c >= '0' && c <= '9')
                || (c >= 'A' && c <= 'Z')
                || (c >= 'a' && c <= 'z')
                || c is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';
            if (!isTokenChar)
            {
                return false;
            }
        }

        return true;
    }
}
