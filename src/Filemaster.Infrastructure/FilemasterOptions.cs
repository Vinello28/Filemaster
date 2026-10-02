namespace Filemaster.Infrastructure;

/// <summary>
/// Le opzioni del client HTTP di Filemaster: dove e' il server, con quale chiave si parla, quanto si aspetta e se si ritenta. E'
/// un tipo di input: classe con proprieta' <c>get; set;</c> (nessun <c>init</c> e nessun <c>required</c>, quindi si usa anche da
/// C# 7.3, per esempio da .NET Framework 4.8) e <see cref="Validate"/>.
/// </summary>
/// <remarks>
/// <para>
/// Il client controlla le opzioni una volta, quando lo si crea, e ne tiene una copia: cambiare questo oggetto dopo non ha effetto
/// su un client gia' creato.
/// </para>
/// <para>
/// <b>La chiave API non esce mai</b> dal client verso chi la leggerebbe per sbaglio: <see cref="ToString"/> la nasconde, e non
/// compare nei messaggi di log, nelle eccezioni e nei messaggi di <see cref="Validate"/>.
/// </para>
/// <para>
/// <b>Il tempo.</b> <see cref="RequestTimeout"/> e <see cref="TransferTimeout"/> sono per chiamata, non per l'<c>HttpClient</c>: il
/// <c>HttpClient.Timeout</c> dell'<c>HttpClient</c> passato al client dev'essere infinito (<see cref="Timeout.InfiniteTimeSpan"/>),
/// altrimenti un trasferimento lungo viene interrotto al suo scadere (100 secondi per default) qualunque cosa dicano queste opzioni;
/// un'interruzione di quel tipo arriva comunque come <see cref="Filemaster.Domain.FilemasterTimeoutException"/>.
/// </para>
/// </remarks>
public sealed class FilemasterOptions
{
    private FilemasterRetryOptions _retry = new();

    /// <summary>
    /// L'indirizzo del server, assoluto e con schema <c>http</c> o <c>https</c>, senza query ne' frammento (per esempio
    /// <c>https://filemaster.example.test/</c>). Puo' avere un prefisso di percorso, per esempio dietro un reverse proxy
    /// (<c>https://example.test/storage</c>): i percorsi delle chiamate si aggiungono dopo quel prefisso. Il client aggiunge da solo
    /// la barra finale (il valore di questa proprieta' non cambia). Obbligatorio. Con <c>http</c> su un indirizzo che non e' di
    /// loopback la chiave API viaggia in chiaro: il client lo segnala nel log una volta.
    /// </summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>
    /// La chiave API (header <c>X-API-Key</c>), obbligatoria: non vuota e fatta solo di caratteri ASCII visibili (niente spazi,
    /// a capo o caratteri di controllo, che permetterebbero di iniettare altri header). Viene spedita solo da questo client, per
    /// richiesta, mai impostata sull'<c>HttpClient</c>.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Quanto si aspetta il server prima di rinunciare: per le chiamate JSON, dall'inizio alla risposta intera letta; per un
    /// download, dall'inizio alle intestazioni della risposta. Vale per l'<b>intera chiamata</b>, ritentativi e attese comprese
    /// (non per ogni tentativo). Default 30 secondi. Positivo; non puo' essere infinito.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Quanto puo' durare un trasferimento di contenuto: l'intero caricamento di un documento, oppure il resto di un download
    /// dopo le intestazioni. Default 30 minuti. Positivo, oppure <see cref="Timeout.InfiniteTimeSpan"/> per non porre un limite
    /// (e' l'unica opzione di tempo che lo ammette).
    /// </summary>
    public TimeSpan TransferTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>I ritentativi automatici delle letture. Mai null: assegnare null lancia <see cref="ArgumentNullException"/>.</summary>
    public FilemasterRetryOptions Retry
    {
        get => _retry;
        set
        {
            Guard.NotNull(value);
            _retry = value;
        }
    }

    /// <summary>Controlla le opzioni; lancia alla prima violazione, nell'ordine delle proprieta' (poi quelle di <see cref="Retry"/>).</summary>
    /// <exception cref="ArgumentException">
    /// <see cref="BaseAddress"/> manca, non e' assoluto, non e' <c>http</c> o <c>https</c>, ha una query o un frammento;
    /// oppure <see cref="ApiKey"/> manca, e' vuota o ha caratteri non ammessi. <see cref="ArgumentException.ParamName"/> e' il nome
    /// della proprieta'; il messaggio non riporta mai il valore della chiave.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Un tempo non e' valido (<see cref="RequestTimeout"/> e <see cref="TransferTimeout"/>: positivi e al massimo
    /// <c>int.MaxValue</c> millisecondi; solo <see cref="TransferTimeout"/> puo' essere infinito), o un valore di <see cref="Retry"/>
    /// e' fuori limite.
    /// </exception>
    public void Validate()
    {
        var baseAddress = BaseAddress;
        OptionChecks.Required(baseAddress is not null, nameof(BaseAddress), "L'indirizzo del server");
        OptionChecks.Holds(baseAddress!.IsAbsoluteUri, nameof(BaseAddress), "L'indirizzo del server deve essere assoluto (schema, host).");
        OptionChecks.Holds(
            baseAddress.Scheme == Uri.UriSchemeHttp || baseAddress.Scheme == Uri.UriSchemeHttps,
            nameof(BaseAddress),
            "L'indirizzo del server deve avere schema http o https.");
        OptionChecks.Holds(
            baseAddress.Query.Length == 0
                && baseAddress.Fragment.Length == 0
                && baseAddress.OriginalString.IndexOf('?') < 0
                && baseAddress.OriginalString.IndexOf('#') < 0,
            nameof(BaseAddress),
            "L'indirizzo del server non deve avere query ne' frammento.");

        var apiKey = ApiKey;
        OptionChecks.Required(!string.IsNullOrEmpty(apiKey), nameof(ApiKey), "La chiave API");
        OptionChecks.Holds(
            IsVisibleAscii(apiKey!),
            nameof(ApiKey),
            "La chiave API puo' contenere solo caratteri ASCII visibili (niente spazi, a capo o caratteri di controllo).");

        OptionChecks.TimeoutValue(RequestTimeout, allowInfinite: false, nameof(RequestTimeout));
        OptionChecks.TimeoutValue(TransferTimeout, allowInfinite: true, nameof(TransferTimeout));
        Retry.Validate();
    }

    /// <summary>Una riga leggibile con le opzioni, <b>senza la chiave API</b> (se c'e', al suo posto compaiono tre asterischi).</summary>
    /// <returns>Il testo.</returns>
    public override string ToString() =>
        FormattableString.Invariant(
            $"FilemasterOptions {{ BaseAddress = {BaseAddress}, ApiKey = {(string.IsNullOrEmpty(ApiKey) ? "(non impostata)" : "***")}, RequestTimeout = {RequestTimeout}, TransferTimeout = {TransferTimeout}, Retry = {Retry} }}");

    /// <summary>
    /// L'indirizzo del server con la barra finale nel percorso, pronto per risolverci sopra i percorsi relativi delle chiamate (che
    /// non iniziano per <c>/</c>): <c>https://h/proxy</c> diventa <c>https://h/proxy/</c>, cosi' <c>documents</c> si risolve in
    /// <c>https://h/proxy/documents</c> invece di <c>https://h/documents</c>. Va chiamato dopo <see cref="Validate"/>.
    /// </summary>
    internal Uri GetNormalizedBaseAddress()
    {
        var baseAddress = BaseAddress!;
        var text = baseAddress.AbsoluteUri;
        // AbsoluteUri non e' mai vuoto per un indirizzo assoluto; EndsWith(char) non esiste su netstandard2.0.
        return text[text.Length - 1] == '/' ? baseAddress : new Uri(text + "/", UriKind.Absolute);
    }

    // Una chiave e' fatta solo di caratteri ASCII visibili (0x21-0x7E): confronto numerico, mai char.IsLetterOrDigit (Unicode).
    private static bool IsVisibleAscii(string value)
    {
        foreach (var c in value)
        {
            if (c < '!' || c > '~')
            {
                return false;
            }
        }

        return true;
    }
}
