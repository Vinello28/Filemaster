using Filemaster.Application;

namespace Filemaster;

/// <summary>
/// Il client creato da <see cref="FilemasterClientFactory"/>: le porte di <see cref="IFilemasterClient"/> piu' la proprieta'
/// dell'<see cref="HttpClient"/> interno, che <see cref="Dispose"/> rilascia. Si crea <b>una volta per applicazione</b> e si tiene fino
/// alla chiusura (un client per richiesta esaurirebbe le porte del sistema); e' thread-safe.
/// </summary>
/// <remarks>
/// <para>
/// Il codice che usa il client puo' dipendere solo da <see cref="IFilemasterClient"/> (o da una porta, per esempio
/// <see cref="IDocumentStore"/>), che non e' <see cref="IDisposable"/>: smaltire e' compito di chi lo crea, di solito all'uscita
/// dall'applicazione.
/// </para>
/// <para>
/// Dopo <see cref="Dispose"/> le porte restano leggibili ma ogni chiamata fallisce con <see cref="ObjectDisposedException"/>. Il
/// gestore passato alla factory dall'utente <b>non</b> viene smaltito; quello creato dalla factory si'.
/// </para>
/// </remarks>
public sealed class FilemasterClient : IFilemasterClient, IDisposable
{
    private readonly IFilemasterClient _inner;
    private readonly HttpClient _http;

    internal FilemasterClient(IFilemasterClient inner, HttpClient http, HttpMessageHandler handler, bool ownsHandler)
    {
        _inner = inner;
        _http = http;
        Handler = handler;
        OwnsHandler = ownsHandler;
    }

    /// <inheritdoc />
    public IDocumentStore Documents => _inner.Documents;

    /// <inheritdoc />
    public IFolderCatalog Folders => _inner.Folders;

    /// <inheritdoc />
    public IContactDirectory Contacts => _inner.Contacts;

    /// <inheritdoc />
    public ITenantInfo Tenant => _inner.Tenant;

    /// <inheritdoc />
    public IFilemasterHealth Health => _inner.Health;

    /// <summary>Il gestore primario in uso (per i test).</summary>
    internal HttpMessageHandler Handler { get; }

    /// <summary>Vero se il gestore e' stato creato dalla factory e si smaltisce con il client (per i test).</summary>
    internal bool OwnsHandler { get; }

    /// <summary>
    /// Rilascia l'<see cref="HttpClient"/> interno e, se l'ha creato la factory, il gestore con le sue connessioni. Chiamarlo piu' volte
    /// non ha effetto.
    /// </summary>
    public void Dispose() => _http.Dispose();
}
