using System.Globalization;
using System.Text.Json;

namespace Filemaster.Domain;

/// <summary>
/// Cio' che <c>arxivar-sync</c> di Sharp-a-File scrive nei metadati di un documento importato da ARXivar: la sezione
/// <c>arxivar</c> dell'oggetto <see cref="Document.Metadata"/>. Si ottiene con <see cref="From"/>.
/// </summary>
/// <remarks>
/// <para>
/// I campi sono quelli che il server scrive (<c>ArxivarMapping.Metadata</c>), con i nomi italiani delle chiavi
/// tradotti: <c>docnumber</c> e' sempre presente, gli altri mancano se il profilo ARXivar non li ha. Il server non
/// valida questa sezione (i metadati sono JSON libero di chi carica), quindi chi legge deve essere tollerante: e'
/// il compito di <see cref="From"/>. Solo qui il Domain conosce nomi di chiavi del filo, perche' leggere i metadati
/// liberi e' il suo scopo.
/// </para>
/// <para>
/// Il record contiene solo stringhe e numeri copiati: non trattiene nessun <see cref="JsonElement"/>, quindi resta
/// valido anche dopo che il <see cref="JsonDocument"/> sorgente e' smaltito, e due risultati con gli stessi dati sono
/// uguali (a differenza di <see cref="Document"/>).
/// </para>
/// </remarks>
public sealed record ArxivarMetadata(
    int Docnumber,
    string? Category,
    string? Subject,
    string? Status,
    string? Number,
    DateTime? DocumentDate,
    string? Protocol,
    string? Year,
    int? Revision,
    string? Fingerprint)
{
    private const string SectionKey = "arxivar";
    private const string DocumentDateFormat = "yyyy-MM-dd";

    /// <summary>
    /// Il <c>DOCNUMBER</c> del profilo in ARXivar, che identifica il documento di origine (chiave <c>docnumber</c>).
    /// I profili che <c>arxivar-sync</c> importa hanno un <c>DOCNUMBER</c> maggiore di zero; il server non verifica il
    /// segno nei metadati, quindi un valore non positivo e' accettato.
    /// </summary>
    public int Docnumber { get; } = Docnumber;

    /// <summary>Il codice della categoria ARXivar del profilo (chiave <c>categoria</c>); null se assente o non e' testo.</summary>
    public string? Category { get; } = Category;

    /// <summary>L'oggetto del profilo (chiave <c>oggetto</c>); null se assente o non e' testo.</summary>
    public string? Subject { get; } = Subject;

    /// <summary>Lo stato del profilo (chiave <c>stato</c>), per esempio <c>VALIDO</c>; null se assente o non e' testo.</summary>
    public string? Status { get; } = Status;

    /// <summary>Il numero del documento (chiave <c>numero</c>), testo: gli zeri iniziali contano; null se assente o non e' testo.</summary>
    public string? Number { get; } = Number;

    /// <summary>
    /// La data del documento (chiave <c>data_documento</c>): una data di calendario, con ora zero e
    /// <see cref="DateTimeKind.Unspecified"/> perche' non ha fuso. Null se assente o se non e' nel formato esatto
    /// <c>yyyy-MM-dd</c> di una data esistente.
    /// </summary>
    public DateTime? DocumentDate { get; } = DocumentDate;

    /// <summary>Il numero di protocollo (chiave <c>protocollo</c>), testo; null se assente o non e' testo.</summary>
    public string? Protocol { get; } = Protocol;

    /// <summary>L'anno (chiave <c>anno</c>), testo come in ARXivar; null se assente o non e' testo.</summary>
    public string? Year { get; } = Year;

    /// <summary>La revisione del file (chiave <c>revisione</c>); null se assente o non e' un intero.</summary>
    public int? Revision { get; } = Revision;

    /// <summary>L'impronta del contenuto registrata da ARXivar (chiave <c>impronta</c>, un MD5 in esadecimale); null se assente o non e' testo.</summary>
    public string? Fingerprint { get; } = Fingerprint;

    /// <summary>
    /// Legge la sezione <c>arxivar</c> dei metadati di un documento. Non lancia mai: tollera sezione assente, tipi
    /// sbagliati, campi in piu' e metadati che non sono un oggetto.
    /// </summary>
    /// <param name="metadata">
    /// I metadati interi del documento (<see cref="Document.Metadata"/>), non la sola sezione. L'elemento deve essere
    /// valido durante la chiamata, cioe' il suo <see cref="JsonDocument"/> non ancora smaltito; la chiamata non lo
    /// trattiene (copia stringhe e numeri). Il <c>Clone()</c> necessario per tenere <see cref="Document.Metadata"/> oltre
    /// la vita del <see cref="JsonDocument"/> lo fa Infrastructure, non questo metodo.
    /// </param>
    /// <returns>
    /// Il risultato, oppure null se il documento non ha una sezione <c>arxivar</c> utilizzabile: <paramref name="metadata"/>
    /// non e' un oggetto (anche <c>default</c>, <c>ValueKind</c> <c>Undefined</c>, e anche i metadati vuoti <c>{}</c>),
    /// la sezione manca o non e' un oggetto, oppure non ha un <c>docnumber</c> valido.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Nomi e valori si leggono come fa il filtro del server (<c>metadata={"arxivar":{"docnumber":N}}</c>, contenimento
    /// JSON): i nomi distinguono maiuscole e minuscole e il tipo conta. <c>docnumber</c> deve essere un numero JSON il
    /// cui valore e' un intero nei limiti di <see cref="int"/>: <c>61617</c>, <c>61617.0</c> e <c>6.1617e4</c> valgono
    /// tutti 61617 (il filtro del server confronta i numeri per valore); la stringa <c>"61617"</c>, <c>12.5</c>,
    /// <c>99999999999</c>, <c>null</c> e ogni altro tipo no, e il risultato e' null. Con una chiave ripetuta vale
    /// l'ultima (come <see cref="JsonElement.TryGetProperty(string, out JsonElement)"/>), anche se non e' valida.
    /// </para>
    /// <para>
    /// Gli altri campi sono best-effort: se mancano o hanno il tipo sbagliato (un numero dove ci vuole testo, un
    /// <c>revisione</c> non intero, una <c>data_documento</c> che non e' <c>yyyy-MM-dd</c>) restano null e il resto
    /// si legge comunque. Il testo si conserva com'e', senza trim.
    /// </para>
    /// </remarks>
    public static ArxivarMetadata? From(JsonElement metadata)
    {
        if (metadata.ValueKind != JsonValueKind.Object
            || !metadata.TryGetProperty(SectionKey, out var section)
            || section.ValueKind != JsonValueKind.Object
            || !section.TryGetProperty("docnumber", out var docnumber)
            || !TryGetInt(docnumber, out var docnumberValue))
        {
            return null;
        }

        return new ArxivarMetadata(
            docnumberValue,
            GetString(section, "categoria"),
            GetString(section, "oggetto"),
            GetString(section, "stato"),
            GetString(section, "numero"),
            GetDate(section, "data_documento"),
            GetString(section, "protocollo"),
            GetString(section, "anno"),
            GetInt(section, "revisione"),
            GetString(section, "impronta"));
    }

    private static string? GetString(JsonElement section, string name) =>
        section.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? GetInt(JsonElement section, string name) =>
        section.TryGetProperty(name, out var value) && TryGetInt(value, out var number) ? number : null;

    private static DateTime? GetDate(JsonElement section, string name) =>
        GetString(section, name) is { } text
        && DateTime.TryParseExact(text, DocumentDateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    // Un numero JSON il cui valore e' un intero dentro l'intervallo di int: come lo confronta il filtro del server.
    private static bool TryGetInt(JsonElement value, out int result)
    {
        result = 0;
        if (value.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        if (value.TryGetInt32(out result))
        {
            return true;
        }

        // Forme equivalenti a un intero (61617.0, 1e3); un numero oltre il double e' infinito e cade nel controllo dei limiti.
        if (value.TryGetDouble(out var number) && number >= int.MinValue && number <= int.MaxValue && Math.Floor(number) == number)
        {
            result = (int)number;
            return true;
        }

        result = 0;
        return false;
    }
}
