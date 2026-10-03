using Filemaster.Application;
using Filemaster.Domain;

namespace Filemaster.Infrastructure;

/// <summary>
/// L'adapter HTTP di <see cref="IDocumentStore"/>: traduce ogni metodo della porta in una richiesta a <c>/documents</c> del server e la
/// risposta nei tipi dell'Application, attraverso il <see cref="FilemasterTransport"/> (tempi, ritentativi, mappatura degli errori) e
/// il livello wire (percorsi, corpi, lettori). Non tiene stato oltre al trasporto: e' thread-safe se lo e' il trasporto.
/// </summary>
/// <remarks>
/// <para>
/// <b>Validazione, sempre prima della rete e dentro il Task.</b> Ogni metodo e' <c>async</c>: un argomento null
/// (<see cref="ArgumentNullException"/>), un id o un codice vuoto, una richiesta o dei filtri non validi (<see cref="ArgumentException"/>)
/// escono dal <see cref="Task"/> restituito, non in modo sincrono dalla chiamata, e senza che nessuna richiesta parta. E' la stessa
/// scelta delle estensioni async dell'Application (T3.2: solo <c>EnumerateAsync</c> valida in modo eager, perche' restituisce un
/// enumerabile e non un task): chi fa <c>await</c> vede l'eccezione nello stesso punto in entrambi i casi.
/// </para>
/// <para>
/// <b>Status attesi.</b> Le chiamate JSON accettano ogni 2xx (il server risponde 201 al caricamento, 204 a cancellazione e
/// spostamento, 200 al resto). Il contenuto e l'anteprima senza intervallo accettano solo 200; con un intervallo 200 o 206 (un 200 vuol
/// dire che il server ha mandato tutto: <see cref="DocumentContent.IsPartial"/> falso). Ogni altro status passa da
/// <see cref="ProblemMapper"/>: un 206 non chiesto, o un 416 per un intervallo oltre la fine, e' <see cref="UnexpectedResponseException"/>
/// con lo status vero.
/// </para>
/// <para>
/// <b>Ritentativi.</b> Li decide il trasporto: solo i <c>GET</c> (elenco, dettaglio, contenuto, anteprima) e solo prima di consegnare
/// la risposta; caricamento, spostamenti, cancellazione e verifiche (<c>POST</c>/<c>PATCH</c>/<c>DELETE</c>) mai.
/// </para>
/// <para>
/// <b>Download.</b> Lo stream del <see cref="DocumentContent"/> e' il <see cref="DownloadStream"/> del trasporto, che possiede la
/// risposta HTTP (smaltirlo la rilascia: per questo il <c>owner</c> del contenuto e' null) e conta i byte contro
/// <c>Content-Length</c> (download troncato = <see cref="ContentIntegrityException"/> alla lettura). Se le intestazioni non si
/// interpretano (un 206 senza <c>Content-Range</c> valido) lo stream, e con lui la risposta, si smaltisce prima di lanciare.
/// </para>
/// </remarks>
internal sealed class HttpDocumentStore : IDocumentStore
{
    private const string FileField = "file";

    private static readonly HttpMethod Patch = new("PATCH");

    private readonly FilemasterTransport _transport;

    /// <summary>Crea l'adapter sul trasporto indicato (condivisibile con gli altri adapter).</summary>
    /// <param name="transport">Il trasporto.</param>
    /// <exception cref="ArgumentNullException"><paramref name="transport"/> e' null.</exception>
    internal HttpDocumentStore(FilemasterTransport transport)
    {
        Guard.NotNull(transport);
        _transport = transport;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Corpo <c>multipart/form-data</c> in streaming: prima i campi di testo (<see cref="DocumentWire.UploadFields"/>, UTF-8 grezzo,
    /// senza <c>Content-Type</c>), per ultima la parte <c>file</c> con <see cref="ContentDispositionHeader.FilePart"/> e il
    /// <see cref="UploadDocumentRequest.ContentType"/> dichiarato (se null la parte non ha <c>Content-Type</c> e il server riconosce il
    /// tipo dai primi byte). Lo stream e' letto dalla posizione corrente e non e' chiuso, nemmeno in caso di errore
    /// (<see cref="UploadStreamContent"/>).
    /// </remarks>
    public async Task<UploadResult> UploadAsync(UploadDocumentRequest request, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(request);
        var fields = DocumentWire.UploadFields(request);
        PercentEncoding.RequireWellFormed(request.FileName, nameof(UploadDocumentRequest.FileName));
        var fileDisposition = ContentDispositionHeader.FilePart(FileField, request.FileName);
        var fileContentType = request.ContentType;
        var source = request.Content;

        var transportRequest = new TransportRequest(HttpMethod.Post, Routes.Documents)
        {
            Customize = message =>
            {
                JsonCalls.AcceptJson(message);
                message.Content = UploadBody(fields, fileDisposition, fileContentType, source);

                // Lunghezza ignota (stream non riposizionabile): chunked esplicito. Su .NET e' gia' il default; HttpClientHandler di
                // .NET Framework invece, senza TransferEncodingChunked, carica l'intero corpo in memoria per calcolarne il
                // Content-Length (LoadIntoBufferAsync), misurato sulla CI net48.
                if (message.Content.Headers.ContentLength is null)
                {
                    message.Headers.TransferEncodingChunked = true;
                }
            },
        };
        var response = await _transport.SendUploadAsync(transportRequest, cancellationToken).ConfigureAwait(false);
        return DocumentWire.ReadUploadResult(response.Body, WireContext.Of(response));
    }

    /// <inheritdoc />
    public async Task<Page<Document>> ListAsync(
        DocumentQuery? query = null,
        PageRequest? page = null,
        CancellationToken cancellationToken = default)
    {
        var path = DocumentWire.ListPath(query, page);
        var response = await SendJsonAsync(HttpMethod.Get, path, body: null, cancellationToken).ConfigureAwait(false);
        return DocumentWire.ReadPage(response.Body, WireContext.Of(response));
    }

    /// <inheritdoc />
    public async Task<Document> GetAsync(DocumentId id, CancellationToken cancellationToken = default)
    {
        var path = Routes.Document(id);
        var response = await SendJsonAsync(HttpMethod.Get, path, body: null, cancellationToken).ConfigureAwait(false);
        return DocumentWire.ReadDocument(response.Body, WireContext.Of(response));
    }

    /// <inheritdoc />
    public async Task<DocumentContent> OpenContentAsync(
        DocumentId id,
        ByteRange? range = null,
        CancellationToken cancellationToken = default)
    {
        var path = Routes.DocumentContent(id);
        return await OpenAsync(path, range, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<DocumentContent> OpenPreviewAsync(DocumentId id, CancellationToken cancellationToken = default)
    {
        var path = Routes.DocumentPreview(id);
        return await OpenAsync(path, range: null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(DocumentId id, CancellationToken cancellationToken = default)
    {
        var path = Routes.Document(id);
        await SendJsonAsync(HttpMethod.Delete, path, body: null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task MoveAsync(DocumentId id, FolderCode? folder, CancellationToken cancellationToken = default)
    {
        var path = Routes.DocumentFolder(id);
        var body = DocumentWire.MoveBody(folder);
        await SendJsonAsync(Patch, path, body, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IntegrityCheck> VerifyAsync(DocumentId id, CancellationToken cancellationToken = default)
    {
        var path = Routes.DocumentVerify(id);
        var response = await SendJsonAsync(HttpMethod.Post, path, body: null, cancellationToken).ConfigureAwait(false);
        return VerifyWire.ReadIntegrityCheck(response.Body, WireContext.Of(response));
    }

    /// <inheritdoc />
    public async Task<int> MoveManyAsync(
        IReadOnlyCollection<DocumentId> ids,
        FolderCode? folder,
        CancellationToken cancellationToken = default)
    {
        var body = DocumentWire.BulkMoveBody(ids, folder);
        var response = await SendJsonAsync(HttpMethod.Post, Routes.DocumentsBulkMove, body, cancellationToken).ConfigureAwait(false);
        return DocumentWire.ReadMoved(response.Body, WireContext.Of(response));
    }

    /// <inheritdoc />
    public async Task<BulkVerifyResult> VerifyManyAsync(
        IReadOnlyCollection<DocumentId> ids,
        CancellationToken cancellationToken = default)
    {
        var body = DocumentWire.BulkVerifyBody(ids);
        var response = await SendJsonAsync(HttpMethod.Post, Routes.DocumentsBulkVerify, body, cancellationToken).ConfigureAwait(false);
        return VerifyWire.ReadBulkVerify(response.Body, WireContext.Of(response));
    }

    // Una chiamata JSON letta in memoria (Accept JSON e, se c'e' un corpo, Content-Type JSON): la stessa degli altri adapter.
    private Task<TransportResponse> SendJsonAsync(HttpMethod method, string path, byte[]? body, CancellationToken cancellationToken) =>
        JsonCalls.SendAsync(_transport, method, path, body, cancellationToken);

    // Contenuto o anteprima in streaming. Il DownloadStream possiede la risposta: se le intestazioni non si leggono va smaltito qui.
    private async Task<DocumentContent> OpenAsync(string path, ByteRange? range, CancellationToken cancellationToken)
    {
        var rangeHeader = range?.ToHeaderValue();
        var request = new TransportRequest(HttpMethod.Get, path)
        {
            IsExpectedStatus = rangeHeader is null ? IsWhole : IsWholeOrPartial,
        };
        if (rangeHeader is not null)
        {
            request.Customize = message => message.Headers.TryAddWithoutValidation("Range", rangeHeader);
        }

        var download = await _transport.SendDownloadAsync(request, cancellationToken).ConfigureAwait(false);
        try
        {
            return DownloadHeaders.Read(download).ToContent(download.Content, owner: null);
        }
        catch
        {
            download.Content.Dispose();
            throw;
        }
    }

    private static bool IsWhole(int status) => status == 200;

    private static bool IsWholeOrPartial(int status) => status == 200 || status == 206;

    // Il modulo: i campi di testo nell'ordine del wire, poi il file per ultimo (il server ha cosi' gia' letto i campi quando arriva al
    // contenuto). Le intestazioni delle parti si scrivono senza validazione: sono gia' ASCII sicuro (ContentDispositionHeader) e una
    // rilettura di System.Net.Http cambierebbe forma tra .NET Framework e .NET.
    private static MultipartContent UploadBody(
        IReadOnlyList<KeyValuePair<string, string>> fields,
        string fileDisposition,
        string? fileContentType,
        Stream source)
    {
        var form = new MultipartContent("form-data");
        try
        {
            foreach (var field in fields)
            {
                var part = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(field.Value));
                part.Headers.TryAddWithoutValidation("Content-Disposition", ContentDispositionHeader.FormField(field.Key));
                form.Add(part);
            }

            var file = new UploadStreamContent(source);
            file.Headers.TryAddWithoutValidation("Content-Disposition", fileDisposition);
            if (fileContentType is not null)
            {
                file.Headers.TryAddWithoutValidation("Content-Type", fileContentType);
            }

            form.Add(file);
            return form;
        }
        catch
        {
            form.Dispose();
            throw;
        }
    }
}
