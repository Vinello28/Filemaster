using System.Globalization;
using System.Text.Json;
using Filemaster.Domain;

namespace Filemaster.Infrastructure;

/// <summary>Come si interpreta un testo del filo come id forte (<c>DocumentId.TryParse</c>, <c>FolderCode.TryParse</c>...): la forma dei <c>TryParse</c> del Domain.</summary>
/// <typeparam name="T">Il tipo dell'id.</typeparam>
internal delegate bool WireIdParser<T>(string? text, out T value)
    where T : struct;

/// <summary>
/// Un oggetto JSON di una risposta, con le letture tipizzate che i lettori del livello wire usano. Ogni lettura rispetta le stesse
/// regole, cosi' i lettori restano brevi e non si dimentica un caso:
/// <list type="bullet">
/// <item><description>Una proprieta' assente e una proprieta' <c>null</c> sono la stessa cosa (il server omette i null).</description></item>
/// <item><description>Una proprieta' obbligatoria assente, o di un tipo sbagliato, e' <see cref="Filemaster.Domain.UnexpectedResponseException"/> con lo status vero e il percorso del campo nel messaggio (mai il valore).</description></item>
/// <item><description>Le proprieta' sconosciute sono ignorate: il server puo' aggiungerne.</description></item>
/// <item><description>Un testo con UTF-8 non valido o con un surrogato isolato (<c>GetString()</c> lancia <see cref="InvalidOperationException"/>) e' una risposta non interpretabile, non un'eccezione che esce.</description></item>
/// <item><description>Un numero deve essere un intero JSON nel suo intervallo: <c>590.0</c>, <c>1e3</c> e un numero fuori intervallo non lo sono.</description></item>
/// </list>
/// </summary>
internal readonly struct WireObject
{
    private const int Sha256HexLength = 64;

    private readonly JsonElement _element;

    internal WireObject(JsonElement element, string path, WireContext context)
    {
        _element = element;
        Path = path;
        Context = context;
    }

    /// <summary>Dove ci si trova, per i messaggi d'errore (<c>documento</c>, <c>pagina.items[2]</c>).</summary>
    internal string Path { get; }

    /// <summary>Lo status e l'id di correlazione della risposta.</summary>
    internal WireContext Context { get; }

    /// <summary>La radice di una risposta: deve essere un oggetto JSON (non un array, un valore semplice, <c>null</c>).</summary>
    /// <param name="element">La radice del documento.</param>
    /// <param name="what">Il nome di cio' che si legge, in italiano, per i messaggi (<c>documento</c>).</param>
    /// <param name="context">Il contesto della risposta.</param>
    internal static WireObject Root(JsonElement element, string what, WireContext context)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw context.Unexpected("Risposta non interpretabile: '" + what + "' non e' un oggetto JSON.");
        }

        return new WireObject(element, what, context);
    }

    /// <summary>Un testo obbligatorio; la stringa vuota e' un testo valido.</summary>
    internal string RequiredString(string name) => OptionalString(name) ?? throw Missing(name);

    /// <summary>Un testo facoltativo: null se la proprieta' manca o e' <c>null</c>.</summary>
    internal string? OptionalString(string name) => TryFind(name, out var value) ? ReadString(name, value) : null;

    /// <summary>Un intero a 32 bit obbligatorio.</summary>
    internal int RequiredInt32(string name) => OptionalInt32(name) ?? throw Missing(name);

    /// <summary>Un intero a 32 bit facoltativo.</summary>
    internal int? OptionalInt32(string name)
    {
        if (!TryFind(name, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : throw Wrong(name, "non e' un intero a 32 bit");
    }

    /// <summary>Un intero a 32 bit obbligatorio, non negativo (un conteggio).</summary>
    internal int RequiredCount(string name) => NonNegative(name, RequiredInt32(name));

    /// <summary>Un intero a 32 bit facoltativo, non negativo (un conteggio).</summary>
    internal int? OptionalCount(string name) => OptionalInt32(name) is { } number ? NonNegative(name, number) : null;

    /// <summary>Un intero a 64 bit obbligatorio, non negativo (una dimensione in byte).</summary>
    internal long RequiredSize(string name)
    {
        if (!TryFind(name, out var value))
        {
            throw Missing(name);
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number))
        {
            throw Wrong(name, "non e' un intero a 64 bit");
        }

        return number >= 0 ? number : throw Wrong(name, "e' negativo");
    }

    /// <summary>Un booleano obbligatorio (<c>true</c> o <c>false</c>).</summary>
    internal bool RequiredBool(string name)
    {
        if (!TryFind(name, out var value))
        {
            throw Missing(name);
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Wrong(name, "non e' un booleano"),
        };
    }

    /// <summary>Una data RFC 3339 con fuso obbligatoria (vedi <see cref="WireDates.TryParse"/>).</summary>
    internal DateTimeOffset RequiredDate(string name) =>
        WireDates.TryParse(RequiredString(name), out var date)
            ? date
            : throw Wrong(name, "non e' una data RFC 3339 con il fuso (Z oppure +hh:mm)");

    /// <summary>Un id forte obbligatorio. Un testo che il <paramref name="parse"/> del Domain rifiuta (anche l'id vuoto) e' non interpretabile.</summary>
    /// <param name="name">La proprieta'.</param>
    /// <param name="parse">Il <c>TryParse</c> del tipo.</param>
    /// <param name="what">Che cos'e', per il messaggio (<c>un id di documento</c>).</param>
    internal T RequiredId<T>(string name, WireIdParser<T> parse, string what)
        where T : struct =>
        OptionalId(name, parse, what) ?? throw Missing(name);

    /// <summary>Un id forte facoltativo: null se la proprieta' manca o e' <c>null</c>; un testo non valido e' non interpretabile.</summary>
    internal T? OptionalId<T>(string name, WireIdParser<T> parse, string what)
        where T : struct
    {
        var text = OptionalString(name);
        if (text is null)
        {
            return null;
        }

        return parse(text, out var id) ? id : throw Wrong(name, "non e' " + what + " valido");
    }

    /// <summary>Un SHA-256 obbligatorio: esattamente 64 cifre esadecimali minuscole ASCII.</summary>
    internal string RequiredSha256(string name) => OptionalSha256(name) ?? throw Missing(name);

    /// <summary>
    /// Un SHA-256 facoltativo: null se manca o e' <c>null</c>, altrimenti esattamente 64 cifre esadecimali minuscole ASCII. Un altro
    /// valore e' non interpretabile: chi lo usa per verificare il contenuto (<c>VerifiedContentStream</c>) lancerebbe piu' tardi un
    /// <see cref="ArgumentException"/> per un difetto del server.
    /// </summary>
    internal string? OptionalSha256(string name)
    {
        var text = OptionalString(name);
        if (text is null)
        {
            return null;
        }

        return IsLowerHex(text) ? text : throw Wrong(name, "non e' un SHA-256 (64 cifre esadecimali minuscole)");
    }

    /// <summary>Un array di oggetti obbligatorio, letto elemento per elemento; un elemento che non e' un oggetto e' non interpretabile.</summary>
    internal IReadOnlyList<T> RequiredList<T>(string name, Func<WireObject, T> read) =>
        OptionalList(name, read) ?? throw Missing(name);

    /// <summary>Un array di oggetti facoltativo (null se manca o e' <c>null</c>), letto elemento per elemento.</summary>
    internal IReadOnlyList<T>? OptionalList<T>(string name, Func<WireObject, T> read)
    {
        if (!TryFind(name, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw Wrong(name, "non e' un array");
        }

        var list = new List<T>(value.GetArrayLength());
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            var path = Path + "." + name + "[" + index.ToString(CultureInfo.InvariantCulture) + "]";
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw Context.Unexpected("Risposta non interpretabile: '" + path + "' non e' un oggetto JSON.");
            }

            list.Add(read(new WireObject(item, path, Context)));
            index++;
        }

        return list;
    }

    /// <summary>
    /// Un oggetto JSON facoltativo, COPIATO (<c>Clone()</c>): l'elemento resta valido dopo che il documento che lo conteneva e'
    /// smaltito. Null se la proprieta' manca o e' <c>null</c>; un valore che non e' un oggetto (array, testo, numero) e' non interpretabile.
    /// </summary>
    internal JsonElement? OptionalObjectCopy(string name)
    {
        if (!TryFind(name, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Object ? value.Clone() : throw Wrong(name, "non e' un oggetto JSON");
    }

    /// <summary>Vero se la stringa e' fatta di 64 cifre esadecimali minuscole ASCII (mai <c>char.IsDigit</c>: accetta cifre Unicode).</summary>
    internal static bool IsLowerHex(string text)
    {
        if (text.Length != Sha256HexLength)
        {
            return false;
        }

        foreach (var c in text)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    // La proprieta' se c'e' e non e' null. Per il server assente e null sono lo stesso (i DTO omettono i null).
    private bool TryFind(string name, out JsonElement value)
    {
        bool found;
        try
        {
            found = _element.TryGetProperty(name, out value);
        }
        catch (InvalidOperationException exception)
        {
            // Anche i NOMI delle proprieta' possono avere UTF-8 non valido o un surrogato isolato: confrontarli lancia come GetString().
            throw Context.Unexpected(
                "Risposta non interpretabile: l'oggetto '" + Path + "' ha un nome di proprieta' non valido (UTF-8 non valido o surrogato isolato).",
                exception);
        }

        if (found && value.ValueKind != JsonValueKind.Null)
        {
            return true;
        }

        value = default;
        return false;
    }

    private string ReadString(string name, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw Wrong(name, "non e' un testo");
        }

        try
        {
            return value.GetString()!;
        }
        catch (InvalidOperationException exception)
        {
            // UTF-8 non valido o surrogato isolato: il parser li accetta, e' GetString() a lanciare.
            throw Context.Unexpected(
                "Risposta non interpretabile: il campo '" + FieldPath(name) + "' ha un testo non valido (UTF-8 non valido o surrogato isolato).",
                exception);
        }
    }

    private int NonNegative(string name, int number) => number >= 0 ? number : throw Wrong(name, "e' negativo");

    private string FieldPath(string name) => Path + "." + name;

    private UnexpectedResponseException Missing(string name) =>
        Context.Unexpected("Risposta non interpretabile: manca il campo '" + FieldPath(name) + "'.");

    private UnexpectedResponseException Wrong(string name, string problem) =>
        Context.Unexpected("Risposta non interpretabile: il campo '" + FieldPath(name) + "' " + problem + ".");
}
