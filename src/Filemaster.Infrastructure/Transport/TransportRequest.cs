namespace Filemaster.Infrastructure;

/// <summary>
/// Cosa chiedere al server: metodo, percorso relativo (con l'eventuale query, gia' codificata) e due punti di personalizzazione.
/// Il trasporto costruisce un <see cref="HttpRequestMessage"/> <b>nuovo a ogni tentativo</b> (un messaggio non si reinvia) e chiama
/// <see cref="Customize"/> su ognuno: li' l'adapter imposta <c>Accept</c>, <c>Range</c> o il corpo. I ritentativi valgono solo per i
/// <c>GET</c>, quindi un corpo non viene mai ricostruito per un secondo tentativo.
/// </summary>
internal sealed class TransportRequest
{
    /// <summary>Crea la richiesta.</summary>
    /// <param name="method">Il metodo HTTP. <c>PATCH</c> si crea a mano (<c>new HttpMethod("PATCH")</c>): <c>HttpMethod.Patch</c> non esiste su netstandard2.0.</param>
    /// <param name="relativeUri">
    /// Il percorso relativo all'indirizzo base, <b>senza barra iniziale</b> (una barra iniziale scarterebbe l'eventuale prefisso di
    /// percorso dell'indirizzo base), per esempio <c>documents/doc_X/content</c> o <c>folders?parent_id=FATTURE</c>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="method"/> o <paramref name="relativeUri"/> e' null.</exception>
    /// <exception cref="ArgumentException"><paramref name="relativeUri"/> e' vuoto, comincia per <c>/</c> o non e' un percorso relativo.</exception>
    internal TransportRequest(HttpMethod method, string relativeUri)
    {
        Guard.NotNull(method);
        Guard.NotNull(relativeUri);
        if (relativeUri.Length == 0
            || relativeUri[0] == '/'
            || !Uri.TryCreate(relativeUri, UriKind.Relative, out _))
        {
            throw new ArgumentException(
                "Il percorso deve essere relativo all'indirizzo base e non cominciare per '/' (la barra iniziale scarta il prefisso di percorso dell'indirizzo base).",
                nameof(relativeUri));
        }

        Method = method;
        RelativeUri = relativeUri;
    }

    /// <summary>Il metodo HTTP.</summary>
    internal HttpMethod Method { get; }

    /// <summary>Il percorso relativo, senza barra iniziale, con l'eventuale query.</summary>
    internal string RelativeUri { get; }

    /// <summary>
    /// Chiamata su ogni <see cref="HttpRequestMessage"/> appena creato, dopo le intestazioni del trasporto (<c>X-API-Key</c>,
    /// <c>X-Request-ID</c>, <c>User-Agent</c>): imposta cio' che e' proprio della chiamata. Non deve toccare quelle tre.
    /// </summary>
    internal Action<HttpRequestMessage>? Customize { get; set; }

    /// <summary>
    /// Quali status sono l'esito della chiamata e si restituiscono cosi', senza mapparli in eccezioni e senza ritentarli. Se e' null
    /// valgono tutti i 2xx. Per <c>/readyz</c> sono 200 e 503 (il 503 e' l'esito della sonda, non un errore).
    /// </summary>
    internal Func<int, bool>? IsExpectedStatus { get; set; }

    /// <summary>
    /// Vero per le chiamate anonime (le sonde <c>/healthz</c> e <c>/readyz</c>): il trasporto non aggiunge <c>X-API-Key</c> (le altre
    /// intestazioni restano). Serve perche' il server autentica OGNI richiesta che porta una chiave, anche verso una rotta anonima, e
    /// per farlo interroga il database: con il database giu' <c>/readyz</c> risponderebbe 500 invece del suo 503, e ogni sonda
    /// costerebbe una lettura (e a volte una scrittura) del database. Di default falso: la chiave va su tutto il resto.
    /// </summary>
    internal bool OmitApiKey { get; set; }
}
