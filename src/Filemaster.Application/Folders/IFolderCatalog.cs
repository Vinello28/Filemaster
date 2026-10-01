using Filemaster.Domain;

namespace Filemaster.Application;

/// <summary>
/// Le cartelle dell'ente: creazione, rinomina, elenco dei figli e cancellazione. E' la porta per la risorsa
/// <c>/folders</c> del server dev; le cartelle sono logiche (contenitori di documenti), si identificano con un codice scelto
/// da chi le crea (<see cref="FolderCode"/>) e formano un albero che non si puo' ristrutturare: il server non sposta le
/// cartelle.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cosa il server non ha.</b> Non esiste la lettura di una cartella per codice (<c>GET /folders/{id}</c>) ne' un
/// elenco di tutte le cartelle: si legge un livello alla volta con <see cref="ListChildrenAsync"/>, e per sapere se una
/// cartella esiste si elenca il livello del suo padre. L'elenco non e' paginato.
/// </para>
/// <para>
/// Valgono le regole comuni di <see cref="IDocumentStore"/> (annullamento, argomenti null e codici vuoti, errori
/// 401/403/connessione/5xx, nessun ritentativo per le scritture); qui si documentano solo gli errori specifici. Creazione,
/// modifica e cancellazione richiedono lo scope <c>write</c>.
/// </para>
/// </remarks>
public interface IFolderCatalog
{
    /// <summary>Crea una cartella (<c>POST /folders</c>).</summary>
    /// <param name="request">Codice, nome ed eventuale cartella madre. Si valida con <see cref="CreateFolderRequest.Validate"/>.</param>
    /// <param name="cancellationToken">Per annullare la chiamata.</param>
    /// <returns>La cartella creata.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> e' null.</exception>
    /// <exception cref="ArgumentException">La richiesta non rispetta i limiti del server (vedi <see cref="CreateFolderRequest.Validate"/>).</exception>
    /// <exception cref="NotFoundException">La cartella madre non esiste (404).</exception>
    /// <exception cref="ConflictException">Il codice e' gia' usato nell'ente, oppure esiste gia' una cartella con lo stesso nome nello stesso padre (409).</exception>
    Task<Folder> CreateAsync(CreateFolderRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cambia il codice e/o il nome di una cartella (<c>PATCH /folders/{id}</c>, solo sul server dev). Il nuovo codice si
    /// propaga alle sottocartelle e ai documenti che la usano.
    /// </summary>
    /// <param name="id">Il codice attuale della cartella.</param>
    /// <param name="request">Le modifiche. Si valida con <see cref="UpdateFolderRequest.Validate"/>.</param>
    /// <param name="cancellationToken">Per annullare la chiamata.</param>
    /// <returns>La cartella com'e' dopo la modifica (con il codice nuovo, se e' cambiato).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> e' null.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> e' il codice vuoto (<c>default</c>), oppure la richiesta non rispetta i limiti del server (vedi <see cref="UpdateFolderRequest.Validate"/>).</exception>
    /// <exception cref="NotFoundException">La cartella non esiste (404).</exception>
    /// <exception cref="ConflictException">
    /// Il nuovo codice e' gia' usato, oppure il nuovo nome esiste gia' nello stesso padre, oppure e' in corso un collegamento con
    /// ARXivar per l'ente e il server, dopo circa cinque secondi di attesa, risponde 409 (in quest'ultimo caso si puo'
    /// riprovare fra poco).
    /// </exception>
    Task<Folder> UpdateAsync(FolderCode id, UpdateFolderRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Elenca le cartelle figlie di una cartella, o quelle di primo livello (<c>GET /folders</c>), in ordine di nome. Non
    /// scende nei livelli successivi e non e' paginato.
    /// </summary>
    /// <param name="parentId">Il codice della cartella madre; null per le cartelle di primo livello.</param>
    /// <param name="cancellationToken">Per annullare la chiamata.</param>
    /// <returns>Le cartelle figlie; lista vuota se non ce ne sono. Un padre inesistente da' una lista vuota, non un errore (il server non controlla che esista).</returns>
    /// <exception cref="ArgumentException"><paramref name="parentId"/> e' il codice vuoto (<c>default</c>): per il primo livello si passa null.</exception>
    Task<IReadOnlyList<Folder>> ListChildrenAsync(FolderCode? parentId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancella una cartella (<c>DELETE /folders/{id}</c>). Deve essere vuota: niente sottocartelle e niente documenti, che
    /// non si cancellano a cascata. Non e' idempotente: la seconda cancellazione e' un 404.
    /// </summary>
    /// <param name="id">Il codice della cartella.</param>
    /// <param name="cancellationToken">Per annullare la chiamata.</param>
    /// <returns>Un task completato alla cancellazione.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> e' il codice vuoto (<c>default</c>).</exception>
    /// <exception cref="NotFoundException">La cartella non esiste (anche perche' gia' cancellata).</exception>
    /// <exception cref="ConflictException">
    /// La cartella non e' vuota (409), oppure e' in corso un collegamento con ARXivar per l'ente (409, si puo' riprovare fra poco).
    /// </exception>
    Task DeleteAsync(FolderCode id, CancellationToken cancellationToken = default);
}
