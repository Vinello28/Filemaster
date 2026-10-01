namespace Filemaster.Application;

/// <summary>
/// Il contenuto di un documento aperto in lettura: lo stream dei byte piu' cio' che il server dice nelle intestazioni (tipo,
/// lunghezza, nome del file, ultima modifica e, se e' arrivata solo una parte, quale). Possiede lo stream e, se c'e', la
/// risorsa che lo alimenta (di solito la risposta HTTP): <b>va smaltito</b>, perche' finche' non lo e' la connessione resta
/// occupata. Si usa con <c>using</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lo stream</b> (<see cref="Content"/>) e' di sola lettura e in avanti: non si assume che si possa riposizionare
/// (<c>CanSeek</c>), e <c>Length</c> puo' non essere disponibile (usare <see cref="ContentLength"/>). Una lettura puo'
/// lanciare <see cref="Filemaster.Domain.ContentIntegrityException"/> se la connessione si interrompe prima della
/// fine (download troncato): chi legge fino in fondo senza eccezioni ha tutti i byte annunciati. Per leggere solo una parte
/// si passa un <see cref="ByteRange"/> all'apertura.
/// </para>
/// <para>
/// <b>Dispose.</b> <see cref="Dispose"/> chiude lo stream e poi rilascia la risorsa che lo possiede; se lo smaltimento dello
/// stream lancia, la risorsa viene rilasciata comunque e l'eccezione si propaga. E' idempotente: la seconda chiamata e le
/// successive non fanno nulla e non lanciano (anche se la prima aveva lanciato), ed e' sicura se due thread la chiamano
/// insieme. Dopo lo smaltimento <see cref="Content"/> restituisce ancora lo stesso stream, ormai chiuso: ogni lettura lancia
/// <see cref="ObjectDisposedException"/>. Le altre proprieta' restano leggibili.
/// </para>
/// <para>
/// Se si e' chiesto un intervallo e <see cref="IsPartial"/> e' falso, il server ha mandato tutto il contenuto (ignora un
/// intervallo che non sa servire): si controlla sempre prima di assumere che i byte siano quelli richiesti.
/// </para>
/// </remarks>
public sealed class DocumentContent : IDisposable
{
    private readonly IDisposable? _owner;
    private int _disposed;

    /// <summary>Crea il contenuto; lo chiama l'adapter (o un test con un fake).</summary>
    /// <param name="content">Lo stream dei byte, che questo oggetto possiede e smaltisce.</param>
    /// <param name="contentType">Il tipo di contenuto (MIME) com'e' nell'intestazione, per esempio <c>application/pdf</c>.</param>
    /// <param name="fileName">Il nome del file dall'intestazione <c>Content-Disposition</c>; null se manca.</param>
    /// <param name="contentLength">Quanti byte consegnera' lo stream (<c>Content-Length</c>); null se non e' noto.</param>
    /// <param name="lastModified">L'intestazione <c>Last-Modified</c>; null se manca.</param>
    /// <param name="range">Quale parte del contenuto e' arrivata (206); null se e' arrivato tutto (200).</param>
    /// <param name="owner">
    /// La risorsa che alimenta lo stream (di solito la risposta HTTP), da smaltire dopo lo stream; null se lo stream basta.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> o <paramref name="contentType"/> e' null.</exception>
    public DocumentContent(
        Stream content,
        string contentType,
        string? fileName = null,
        long? contentLength = null,
        DateTimeOffset? lastModified = null,
        ContentRange? range = null,
        IDisposable? owner = null)
    {
        Guard.NotNull(content);
        Guard.NotNull(contentType);
        Content = content;
        ContentType = contentType;
        FileName = fileName;
        ContentLength = contentLength;
        LastModified = lastModified;
        Range = range;
        _owner = owner;
    }

    /// <summary>Lo stream dei byte, di sola lettura e in avanti. Lo smaltisce <see cref="Dispose"/>: non va chiuso a parte.</summary>
    public Stream Content { get; }

    /// <summary>Il tipo di contenuto (MIME) del documento, per esempio <c>application/pdf</c>; puo' avere parametri (<c>text/plain; charset=utf-8</c>).</summary>
    public string ContentType { get; }

    /// <summary>
    /// Il nome del file come l'ha dato il server in <c>Content-Disposition</c> (il nome originale del documento, preferendo la
    /// forma UTF-8); null se l'intestazione manca.
    /// </summary>
    public string? FileName { get; }

    /// <summary>
    /// Quanti byte consegnera' lo stream (<c>Content-Length</c>): la dimensione del documento se e' arrivato tutto, quella
    /// della parte se <see cref="IsPartial"/>. Null se il server non l'ha detto.
    /// </summary>
    public long? ContentLength { get; }

    /// <summary>
    /// L'intestazione <c>Last-Modified</c>: per il server e' la data di creazione del documento, con precisione al secondo; null
    /// se manca.
    /// </summary>
    public DateTimeOffset? LastModified { get; }

    /// <summary>Quale parte del contenuto e' arrivata (risposta 206); null se e' arrivato tutto il contenuto.</summary>
    public ContentRange? Range { get; }

    /// <summary>Vero se e' arrivata solo una parte del contenuto (<see cref="Range"/> c'e'); falso se e' arrivato tutto.</summary>
    public bool IsPartial => Range is not null;

    /// <summary>Chiude lo stream e rilascia cio' che lo possiede. Vedi le note del tipo: e' idempotente.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            Content.Dispose();
        }
        finally
        {
            _owner?.Dispose();
        }
    }
}
