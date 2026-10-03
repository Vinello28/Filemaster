using Filemaster.Application;
using Filemaster.Domain;

namespace Filemaster.Infrastructure;

/// <summary>
/// L'adapter HTTP di <see cref="IFolderCatalog"/>: <c>POST folders</c> (201), <c>PATCH folders/{id}</c> (200), <c>GET folders</c> con
/// l'eventuale <c>parent_id</c> (200, non paginato) e <c>DELETE folders/{id}</c> (204), attraverso il <see cref="FilemasterTransport"/>
/// e il livello wire (<see cref="FolderWire"/>, <see cref="Routes"/>). Non tiene stato oltre al trasporto.
/// </summary>
/// <remarks>
/// <para>
/// <b>Validazione, sempre prima della rete e dentro il Task</b> (come <see cref="HttpDocumentStore"/>): una richiesta null
/// (<see cref="ArgumentNullException"/>), un codice vuoto o una richiesta che non rispetta i limiti del server
/// (<see cref="ArgumentException"/> con il nome del parametro o della proprieta') escono dal <see cref="Task"/> restituito, senza
/// che nessuna richiesta parta.
/// </para>
/// <para>
/// <b>Il 409 non si ritenta mai.</b> Copre casi diversi che si distinguono solo dal <see cref="FilemasterException.Detail"/>: codice o
/// nome gia' usati, cartella non vuota, oppure il collegamento con ARXivar che tiene il lock dell'ente (transitorio: "riprova tra
/// poco"). In tutti i casi e' <see cref="ConflictException"/>: decidere se riprovare spetta a chi chiama. Creazione, modifica e
/// cancellazione non si ritentano nemmeno su un errore di rete (il trasporto ritenta solo i <c>GET</c>): un secondo
/// <c>DELETE</c> dopo un successo darebbe un 404 fuorviante, un secondo <c>POST</c> un 409.
/// </para>
/// </remarks>
internal sealed class HttpFolderCatalog : IFolderCatalog
{
    private static readonly HttpMethod Patch = new("PATCH");

    private readonly FilemasterTransport _transport;

    /// <summary>Crea l'adapter sul trasporto indicato (condivisibile con gli altri adapter).</summary>
    /// <param name="transport">Il trasporto.</param>
    /// <exception cref="ArgumentNullException"><paramref name="transport"/> e' null.</exception>
    internal HttpFolderCatalog(FilemasterTransport transport)
    {
        Guard.NotNull(transport);
        _transport = transport;
    }

    /// <inheritdoc />
    /// <remarks>Corpo <c>{"id","parent_id"?,"name"}</c> (<see cref="FolderWire.CreateBody"/>, come nelle catture 21 e 22); risposta 201.</remarks>
    public async Task<Folder> CreateAsync(CreateFolderRequest request, CancellationToken cancellationToken = default)
    {
        var body = FolderWire.CreateBody(request);
        var response = await JsonCalls.SendAsync(_transport, HttpMethod.Post, Routes.Folders, body, cancellationToken).ConfigureAwait(false);
        return FolderWire.ReadFolder(response.Body, WireContext.Of(response));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Corpo con i soli campi impostati, il nuovo codice come <c>id</c> (<see cref="FolderWire.UpdateBody"/>, come nelle catture 28 e 44).
    /// Si controlla prima la richiesta null, poi il codice, poi la richiesta.
    /// </remarks>
    public async Task<Folder> UpdateAsync(FolderCode id, UpdateFolderRequest request, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(request);
        var path = FolderPath(id);
        var body = FolderWire.UpdateBody(request);
        var response = await JsonCalls.SendAsync(_transport, Patch, path, body, cancellationToken).ConfigureAwait(false);
        return FolderWire.ReadFolder(response.Body, WireContext.Of(response));
    }

    /// <inheritdoc />
    /// <remarks><c>GET folders</c> per il primo livello, <c>GET folders?parent_id=CODICE</c> per i figli (catture 24 e 26).</remarks>
    public async Task<IReadOnlyList<Folder>> ListChildrenAsync(FolderCode? parentId = null, CancellationToken cancellationToken = default)
    {
        if (parentId is { IsEmpty: true })
        {
            throw new ArgumentException("Il codice della cartella madre e' vuoto (default): per il primo livello si passa null.", nameof(parentId));
        }

        var path = FolderWire.ListPath(parentId);
        var response = await JsonCalls.SendAsync(_transport, HttpMethod.Get, path, body: null, cancellationToken).ConfigureAwait(false);
        return FolderWire.ReadFolders(response.Body, WireContext.Of(response));
    }

    /// <inheritdoc />
    /// <remarks><c>DELETE folders/{id}</c> senza corpo; risposta 204.</remarks>
    public async Task DeleteAsync(FolderCode id, CancellationToken cancellationToken = default)
    {
        var path = FolderPath(id);
        await JsonCalls.SendAsync(_transport, HttpMethod.Delete, path, body: null, cancellationToken).ConfigureAwait(false);
    }

    // Routes.Folder rifiuta il codice vuoto col nome del SUO parametro ("code"); la porta chiama il parametro "id".
    private static string FolderPath(FolderCode id) =>
        id.IsEmpty
            ? throw new ArgumentException("Il codice della cartella e' vuoto (default): indicare un valore valido.", nameof(id))
            : Routes.Folder(id);
}
