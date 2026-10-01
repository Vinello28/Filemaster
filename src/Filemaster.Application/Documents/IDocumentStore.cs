using Filemaster.Domain;

namespace Filemaster.Application;

/// <summary>
/// I documenti dell'ente: caricamento, elenco, dettaglio, contenuto, anteprima, spostamento, verifica d'integrita' e
/// cancellazione. E' la porta per la risorsa <c>/documents</c> del server; l'implementazione HTTP sta in Infrastructure e
/// questa interfaccia si puo' sostituire con un fake nei test di chi usa la libreria.
/// </summary>
/// <remarks>
/// <para>
/// <b>Regole comuni</b> (non si ripetono in ogni metodo). Ogni metodo e' asincrono e finisce con un
/// <see cref="CancellationToken"/>: annullarlo lancia <see cref="OperationCanceledException"/>, non
/// <see cref="FilemasterTimeoutException"/> (quella e' del tempo scaduto del client). Un argomento null e'
/// <see cref="ArgumentNullException"/>; un id o un codice vuoto (<c>default</c>, per esempio <c>default(DocumentId)</c>,
/// che costruirebbe l'URL <c>/documents/</c>) e' <see cref="ArgumentException"/>; entrambe sono lanciate prima di toccare
/// la rete. Un id valido ma sconosciuto, di un altro ente o di una risorsa cancellata e' <see cref="NotFoundException"/>
/// (404: il server non distingue per non rivelare che l'id esiste).
/// </para>
/// <para>
/// <b>Errori su ogni chiamata</b>: <see cref="UnauthorizedException"/> (401: chiave assente, errata o revocata),
/// <see cref="ForbiddenException"/> (403: scope insufficiente, perche' le scritture richiedono lo scope <c>write</c>, oppure
/// ente sospeso), <see cref="ConnectionException"/> e <see cref="FilemasterTimeoutException"/> (nessuna risposta),
/// <see cref="ServerErrorException"/> (5xx) e <see cref="UnexpectedResponseException"/> (risposta non interpretabile).
/// Sono documentati solo gli errori specifici di ogni metodo.
/// </para>
/// <para>
/// <b>Ritentativi.</b> Le letture si possono ritentare da sole su errori di connessione; le scritture (caricamento,
/// spostamento, cancellazione) e le verifiche (che scrivono nello storico del server) mai: dopo un errore di rete l'esito
/// puo' essere ignoto, e ritentare un <see cref="DeleteAsync"/> riuscito darebbe un 404 fuorviante.
/// </para>
/// </remarks>
public interface IDocumentStore
{
    /// <summary>
    /// Carica un documento (<c>POST /documents</c>, multipart in streaming). Il documento e' sempre nuovo: se gli stessi byte
    /// c'erano gia' nell'ente il server non li riscrive ma crea comunque un documento, e lo dice in
    /// <see cref="UploadResult.Deduplicated"/>. Il server non ha idempotenza: due chiamate uguali sono due documenti.
    /// Richiede lo scope <c>write</c>.
    /// </summary>
    /// <param name="request">
    /// Cosa caricare. Si valida con <see cref="UploadDocumentRequest.Validate"/> prima di aprire la connessione e prima di
    /// leggere lo stream; il contenuto si legge dalla posizione corrente alla fine e non si chiude.
    /// </param>
    /// <param name="cancellationToken">Per annullare la chiamata, anche a meta' trasferimento.</param>
    /// <returns>Il documento creato e se il contenuto c'era gia'.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> e' null.</exception>
    /// <exception cref="ArgumentException">La richiesta non rispetta i limiti del server (vedi <see cref="UploadDocumentRequest.Validate"/>).</exception>
    /// <exception cref="NotFoundException">La cartella indicata non esiste (404).</exception>
    /// <exception cref="InvalidRequestException">Il server ha rifiutato la richiesta (400) per una regola che il client non conosce.</exception>
    /// <exception cref="RequestTooLargeException">Il contenuto supera il limite del server (413, per default 500 MiB; il server puo' anche chiudere la connessione prima).</exception>
    /// <exception cref="StorageNotConfiguredException">Il server non ha la password dell'archivio, quindi non puo' salvare nulla (503).</exception>
    Task<UploadResult> UploadAsync(UploadDocumentRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Elenca i documenti che soddisfano i filtri, dal piu' recente (<c>GET /documents</c>). I documenti dell'elenco non hanno i
    /// contatti dell'anagrafica (<see cref="Document.Contacts"/> e' vuota): per averli si legge il dettaglio con
    /// <see cref="GetAsync"/>.
    /// </summary>
    /// <param name="query">I filtri; null per non filtrare. Si valida con <see cref="DocumentQuery.Validate"/>.</param>
    /// <param name="page">Quale pagina e quanti elementi; null per la prima pagina con il limite di default del server (50).</param>
    /// <param name="cancellationToken">Per annullare la chiamata.</param>
    /// <returns>La pagina e il cursore per la successiva (null se e' l'ultima).</returns>
    /// <exception cref="ArgumentException">I filtri non rispettano i limiti del server (vedi <see cref="DocumentQuery.Validate"/>).</exception>
    /// <exception cref="InvalidRequestException">Il server ha rifiutato i parametri (400): per esempio un cursore non suo.</exception>
    Task<Page<Document>> ListAsync(
        DocumentQuery? query = null,
        PageRequest? page = null,
        CancellationToken cancellationToken = default);

    /// <summary>Legge un documento, con i contatti dell'anagrafica collegati (<c>GET /documents/{id}</c>).</summary>
    /// <param name="id">L'id del documento.</param>
    /// <param name="cancellationToken">Per annullare la chiamata.</param>
    /// <returns>Il documento, con <see cref="Document.Contacts"/> popolata.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> e' l'id vuoto (<c>default</c>).</exception>
    /// <exception cref="NotFoundException">Il documento non esiste.</exception>
    Task<Document> GetAsync(DocumentId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Apre il contenuto di un documento in lettura (<c>GET /documents/{id}/content</c>), tutto o una parte. Il risultato
    /// va smaltito (<see cref="DocumentContent"/>): finche' non lo e', la connessione resta occupata.
    /// </summary>
    /// <param name="id">L'id del documento.</param>
    /// <param name="range">
    /// Quale parte leggere; null per tutto il contenuto. Con un intervallo la risposta e' di norma parziale
    /// (<see cref="DocumentContent.IsPartial"/>), ma il client non lo garantisce: se il server mandasse tutto, si leggerebbe
    /// <see cref="DocumentContent.IsPartial"/> falso.
    /// </param>
    /// <param name="cancellationToken">Per annullare la chiamata (e la lettura dello stream restituito, se l'adapter lo collega).</param>
    /// <returns>Lo stream e le intestazioni.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> e' l'id vuoto (<c>default</c>).</exception>
    /// <exception cref="NotFoundException">Il documento non esiste.</exception>
    /// <exception cref="ContentUnavailableException">Il documento non ha un file: importato con i soli metadati (409 <c>content-unavailable</c>, <see cref="Document.HasContent"/> falso).</exception>
    /// <exception cref="UnexpectedResponseException">
    /// <paramref name="range"/> parte oltre la fine del contenuto: il server risponde 416 senza corpo (<see cref="FilemasterException.StatusCode"/> 416).
    /// </exception>
    /// <exception cref="ContentIntegrityException">Lanciata dalla lettura dello stream, non da questo metodo: il download si e' interrotto prima della fine.</exception>
    Task<DocumentContent> OpenContentAsync(
        DocumentId id,
        ByteRange? range = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Apre l'anteprima di un documento (<c>GET /documents/{id}/preview</c>): lo stesso contenuto, per la visualizzazione in
    /// linea, solo se e' un PDF. Il risultato va smaltito (<see cref="DocumentContent"/>). Il server accetta anche un
    /// intervallo di byte sull'anteprima; qui non e' esposto (per leggere una parte si usa <see cref="OpenContentAsync"/>).
    /// </summary>
    /// <param name="id">L'id del documento.</param>
    /// <param name="cancellationToken">Per annullare la chiamata.</param>
    /// <returns>Lo stream e le intestazioni.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> e' l'id vuoto (<c>default</c>).</exception>
    /// <exception cref="NotFoundException">Il documento non esiste.</exception>
    /// <exception cref="ContentUnavailableException">Il documento non ha un file (409 <c>content-unavailable</c>).</exception>
    /// <exception cref="UnsupportedMediaTypeException">Il documento non e' un PDF (<see cref="Document.MimeType"/> diverso da <c>application/pdf</c>): 415.</exception>
    Task<DocumentContent> OpenPreviewAsync(DocumentId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancella un documento (<c>DELETE /documents/{id}</c>). Il contenuto resta nell'archivio finche' un altro documento
    /// dello stesso ente (per deduplica) lo usa. Non e' idempotente: la seconda cancellazione e' un 404. Richiede lo scope
    /// <c>write</c>.
    /// </summary>
    /// <param name="id">L'id del documento.</param>
    /// <param name="cancellationToken">Per annullare la chiamata.</param>
    /// <returns>Un task completato alla cancellazione.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> e' l'id vuoto (<c>default</c>).</exception>
    /// <exception cref="NotFoundException">Il documento non esiste (anche perche' gia' cancellato).</exception>
    Task DeleteAsync(DocumentId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sposta un documento in una cartella, o nella radice (<c>PATCH /documents/{id}/folder</c>). Richiede lo scope
    /// <c>write</c>.
    /// </summary>
    /// <param name="id">L'id del documento.</param>
    /// <param name="folder">Il codice della cartella di destinazione; null per la radice (nessuna cartella).</param>
    /// <param name="cancellationToken">Per annullare la chiamata.</param>
    /// <returns>Un task completato allo spostamento.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> o <paramref name="folder"/> e' il valore vuoto (<c>default</c>): per la radice si passa null.</exception>
    /// <exception cref="NotFoundException">Il documento non esiste, oppure la cartella di destinazione non esiste.</exception>
    Task MoveAsync(DocumentId id, FolderCode? folder, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifica l'integrita' di un documento (<c>POST /documents/{id}/verify</c>): il server ricalcola lo SHA-256 del
    /// contenuto che ha su disco e lo confronta con quello registrato. Un contenuto alterato o mancante e' un esito
    /// (<see cref="IntegrityCheck.Ok"/> falso), non un'eccezione. Ha effetti: registra l'esito nello storico del server e, se
    /// negativo, genera l'evento webhook <c>document.integrity_failed</c>; per questo non si ritenta da sola.
    /// </summary>
    /// <param name="id">L'id del documento.</param>
    /// <param name="cancellationToken">Per annullare la chiamata.</param>
    /// <returns>L'esito della verifica.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> e' l'id vuoto (<c>default</c>).</exception>
    /// <exception cref="NotFoundException">Il documento non esiste.</exception>
    /// <exception cref="ContentUnavailableException">Il documento non ha un file: non si verifica (409 <c>content-unavailable</c>).</exception>
    /// <exception cref="ServerErrorException">Il server non e' riuscito a leggere il contenuto per un errore di I/O che non e' una corruzione (500).</exception>
    Task<IntegrityCheck> VerifyAsync(DocumentId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sposta piu' documenti in una cartella, o nella radice (<c>POST /documents/bulk/move</c>). Gli id che non esistono (o
    /// sono di un altro ente) sono ignorati senza errore: il risultato dice quanti documenti sono stati spostati davvero.
    /// Un id ripetuto vale una volta. Richiede lo scope <c>write</c>.
    /// </summary>
    /// <remarks>
    /// Non c'e' un tetto esplicito sul numero di id e il client non divide in blocchi, ma il server limita a 1 MiB il corpo JSON
    /// delle richieste: con circa 33 byte per id sono circa 31.000 id per chiamata, oltre i quali il server risponde 413
    /// (<see cref="RequestTooLargeException"/>) o chiude la connessione. Chi ha piu' id li divide.
    /// </remarks>
    /// <param name="ids">Gli id dei documenti; almeno uno, nessuno vuoto (<c>default</c>).</param>
    /// <param name="folder">Il codice della cartella di destinazione; null per la radice.</param>
    /// <param name="cancellationToken">Per annullare la chiamata.</param>
    /// <returns>Quanti documenti sono stati spostati (gli id sconosciuti non contano).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="ids"/> e' null.</exception>
    /// <exception cref="ArgumentException"><paramref name="ids"/> e' vuota o contiene un id vuoto (<c>default</c>), oppure <paramref name="folder"/> e' vuoto (<c>default</c>).</exception>
    /// <exception cref="NotFoundException">La cartella di destinazione non esiste (nessun documento e' stato spostato).</exception>
    /// <exception cref="RequestTooLargeException">Troppi id per il limite di 1 MiB del corpo.</exception>
    Task<int> MoveManyAsync(
        IReadOnlyCollection<DocumentId> ids,
        FolderCode? folder,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifica l'integrita' di piu' documenti insieme (<c>POST /documents/bulk/verify</c>): solo i conteggi, vedi
    /// <see cref="BulkVerifyResult"/>. A differenza dello spostamento, <b>un id sconosciuto fa fallire l'intero lotto</b> con
    /// 404, prima di verificare qualunque documento. Un id ripetuto vale una volta. Ha gli stessi effetti di
    /// <see cref="VerifyAsync"/> (storico ed eventi webhook per ogni esito negativo), quindi non si ritenta da sola.
    /// </summary>
    /// <remarks>
    /// Per il tetto sul numero di id vale la nota di <see cref="MoveManyAsync"/>: circa 31.000 id per chiamata.
    /// </remarks>
    /// <param name="ids">Gli id dei documenti; almeno uno, nessuno vuoto (<c>default</c>).</param>
    /// <param name="cancellationToken">Per annullare la chiamata.</param>
    /// <returns>I conteggi del lotto.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="ids"/> e' null.</exception>
    /// <exception cref="ArgumentException"><paramref name="ids"/> e' vuota o contiene un id vuoto (<c>default</c>).</exception>
    /// <exception cref="NotFoundException">Almeno un id non esiste: il lotto non e' stato verificato.</exception>
    /// <exception cref="RequestTooLargeException">Troppi id per il limite di 1 MiB del corpo.</exception>
    Task<BulkVerifyResult> VerifyManyAsync(
        IReadOnlyCollection<DocumentId> ids,
        CancellationToken cancellationToken = default);
}
