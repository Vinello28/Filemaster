using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Filemaster.Domain;
using Filemaster.Infrastructure;

namespace Filemaster.UnitTests.Wire;

/// <summary>
/// Le fixture golden del livello wire (<c>Wire/Fixtures/</c>): copie scrubbate di risposte catturate dal server vero (<c>captured/</c>) e
/// risposte derivate dal codice del server dove non esiste una cattura (<c>derived/</c>). Sono dati: si leggono dalla cartella accanto
/// alla DLL, con un percorso relativo a <see cref="AppContext.BaseDirectory"/>. Il nome e' quello della cattura (<c>47-doc-upload</c>).
/// </summary>
internal static class WireFixtures
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "Wire", "Fixtures");

    /// <summary>Il corpo catturato (<c>.json</c> di default), byte per byte.</summary>
    internal static byte[] Captured(string name, string extension = "json") => File.ReadAllBytes(CapturedPath(name, extension));

    /// <summary>Un file di una cattura come testo UTF-8.</summary>
    internal static string CapturedText(string name, string extension) => File.ReadAllText(CapturedPath(name, extension), Encoding.UTF8);

    /// <summary>Il corpo di una risposta derivata dal codice del server (non catturata).</summary>
    internal static byte[] Derived(string name) => File.ReadAllBytes(Path.Combine(Root, "derived", name + ".json"));

    /// <summary>Quanti file ha la cartella delle catture: serve a provare che le fixture sono state copiate.</summary>
    internal static int CapturedFileCount() => Directory.GetFiles(Path.Combine(Root, "captured")).Length;

    /// <summary>La prima riga del <c>.request</c> (<c>GET /documents?limit=1</c>): metodo e percorso con la query come l'ha spedita la cattura.</summary>
    internal static string RequestLine(string name) => CapturedText(name, "request").Split('\n')[0];

    /// <summary>Il percorso (con la query) del <c>.request</c>.</summary>
    internal static string RequestPath(string name) => RequestLine(name).Substring(RequestLine(name).IndexOf(' ') + 1);

    /// <summary>
    /// Il corpo JSON spedito dalla cattura: la riga che segue <c># curl: -d</c> nel <c>.request</c>. Il server ha risposto con successo a questo
    /// corpo, quindi e' l'oracolo dei costruttori di richieste.
    /// </summary>
    internal static string RequestBody(string name)
    {
        var lines = CapturedText(name, "request").Split('\n');
        var index = Array.IndexOf(lines, "# curl: -d");
        Assert.True(index >= 0 && index + 1 < lines.Length, name + ": nessun corpo -d nel .request");
        const string Prefix = "# curl: ";
        Assert.StartsWith(Prefix, lines[index + 1], StringComparison.Ordinal);
        return lines[index + 1].Substring(Prefix.Length);
    }

    /// <summary>Lo status HTTP della cattura (dal file <c>.status</c>, per esempio <c>HTTP/1.1 206 Partial Content</c>).</summary>
    internal static int Status(string name)
    {
        var first = CapturedText(name, "status").Split('\n')[0];
        return int.Parse(first.Split(' ')[1], CultureInfo.InvariantCulture);
    }

    /// <summary>Le intestazioni della cattura (<c>.headers</c>): nome e valore come il server li ha scritti, un nome una volta sola.</summary>
    internal static IReadOnlyDictionary<string, string> Headers(string name)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in CapturedText(name, "headers").Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon > 0)
            {
                headers[line.Substring(0, colon)] = line.Substring(colon + 1).Trim();
            }
        }

        return headers;
    }

    private static string CapturedPath(string name, string extension) => Path.Combine(Root, "captured", name + "." + extension);
}

/// <summary>Il contesto di lettura dei test (status 200, id di correlazione fisso) e le asserzioni sull'errore dei lettori.</summary>
internal static class WireTest
{
    internal const string RequestId = "req-test-0001";

    internal static WireContext Context(int status = 200) => new(status, RequestId);

    internal static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);

    /// <summary>Lancia il lettore e controlla che l'errore sia un <see cref="UnexpectedResponseException"/> con lo status e l'id di correlazione veri.</summary>
    internal static UnexpectedResponseException Unexpected(Action read, int status = 200) => Check(Assert.Throws<UnexpectedResponseException>(read), status);

    /// <summary>Lo stesso, per un lettore che restituisce un valore (se non lancia, e' un guasto del test).</summary>
    internal static UnexpectedResponseException Unexpected<T>(Func<T> read, int status = 200) => Check(Assert.Throws<UnexpectedResponseException>(() => read()), status);

    private static UnexpectedResponseException Check(UnexpectedResponseException exception, int status)
    {
        Assert.Equal(status, exception.StatusCode);
        Assert.Equal(RequestId, exception.RequestId);
        Assert.Null(exception.ProblemType);
        Assert.False(string.IsNullOrWhiteSpace(exception.Message));
        return exception;
    }

    /// <summary>Un istante UTC scritto a mano (oracolo indipendente dal parser).</summary>
    internal static DateTimeOffset Utc(int year, int month, int day, int hour, int minute, int second, long ticks = 0) =>
        new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero).AddTicks(ticks);

    /// <summary>Esegue <paramref name="action"/> con la cultura corrente indicata e poi la ripristina.</summary>
    internal static void WithCulture(string culture, Action action)
    {
        var saved = CultureInfo.CurrentCulture;
        var savedUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            CultureInfo.CurrentUICulture = new CultureInfo(culture);
            action();
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
            CultureInfo.CurrentUICulture = savedUi;
        }
    }
}

/// <summary>
/// Varianti di una fixture ottenute con <see cref="JsonNode"/>: si toglie o si cambia UNA proprieta' di una risposta catturata, cosi' l'oracolo
/// resta la fixture e il codice sotto test non e' mai usato per costruire i suoi input.
/// </summary>
internal static class Variants
{
    /// <summary>Toglie la proprieta' di primo livello.</summary>
    internal static byte[] Without(byte[] json, string name) => Edit(json, o => o.Remove(name));

    /// <summary>Imposta la proprieta' di primo livello al JSON grezzo indicato (<c>"x"</c>, <c>123</c>, <c>null</c>, <c>[]</c>).</summary>
    internal static byte[] With(byte[] json, string name, string rawJson) => Edit(json, o => o[name] = JsonNode.Parse(rawJson));

    /// <summary>Applica una modifica all'oggetto radice.</summary>
    internal static byte[] Edit(byte[] json, Action<JsonObject> edit)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        edit(root);
        return WireTest.Utf8(root.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    /// <summary>Applica una modifica all'elemento <paramref name="index"/> dell'array <c>items</c>.</summary>
    internal static byte[] EditItem(byte[] json, int index, Action<JsonObject> edit) =>
        Edit(json, root => edit(root["items"]!.AsArray()[index]!.AsObject()));
}
