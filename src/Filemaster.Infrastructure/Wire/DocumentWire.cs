using System.Text.Json;
using Filemaster.Application;
using Filemaster.Domain;

namespace Filemaster.Infrastructure;

/// <summary>
/// I documenti sul filo: <c>{"id","folder_id"?,"original_filename","mime_type","sha256"?,"size_bytes","owner"?,"tag"?,"sender"?,"recipient"?,
/// "metadata","created_at","has_content","contacts"?,"deduplicated"?}</c>. Il server omette i null; <c>contacts</c> c'e' solo nel dettaglio,
/// <c>deduplicated</c> solo nella risposta di un caricamento. Gli obblighi che il Domain impone a chi legge sono tutti qui:
/// <list type="bullet">
/// <item><description><c>metadata</c> si COPIA (<c>Clone()</c>) prima che il documento JSON sia smaltito, e se manca (o e' <c>null</c>) diventa <c>{}</c>, mai <c>default</c>.</description></item>
/// <item><description><c>has_content</c> si ignora del tutto (anche se ha il tipo sbagliato o contraddice <c>sha256</c>): <see cref="Document.HasContent"/> e' derivato da <see cref="Document.Sha256"/>.</description></item>
/// <item><description><c>contacts</c> assente (o <c>null</c>) e' passato come null: il <see cref="Document"/> ne fa una lista vuota.</description></item>
/// <item><description>Ogni id si legge con il <c>TryParse</c> del tipo forte: un id non valido e' una risposta non interpretabile.</description></item>
/// <item><description><c>sha256</c> deve essere di 64 cifre esadecimali minuscole, <c>size_bytes</c> non negativo: chi verifica il contenuto si fida di questi valori.</description></item>
/// </list>
/// </summary>
internal static class DocumentWire
{
    // Il {} da dare ai documenti senza "metadata": un elemento che sopravvive a qualunque documento JSON (copia, non un riferimento).
    private static readonly JsonElement EmptyObject = CreateEmptyObject();

    /// <summary>Legge un documento (dettaglio di <c>GET /documents/{id}</c>, con i contatti se ci sono).</summary>
    /// <param name="body">I byte del corpo.</param>
    /// <param name="context">Lo status e l'id di correlazione della risposta.</param>
    /// <exception cref="UnexpectedResponseException">Il corpo non ha la forma di un documento.</exception>
    internal static Document ReadDocument(byte[]? body, WireContext context) =>
        WireJson.ReadObject(body, context, "documento", ReadDocument);

    /// <summary>Legge la risposta di un caricamento: il documento creato e <c>deduplicated</c>, che per un caricamento e' obbligatorio.</summary>
    /// <param name="body">I byte del corpo.</param>
    /// <param name="context">Lo status e l'id di correlazione della risposta.</param>
    /// <exception cref="UnexpectedResponseException">Il corpo non ha la forma di un documento con <c>deduplicated</c>.</exception>
    internal static UploadResult ReadUploadResult(byte[]? body, WireContext context) =>
        WireJson.ReadObject(body, context, "documento", upload => new UploadResult(ReadDocument(upload), upload.RequiredBool("deduplicated")));

    /// <summary>Legge una pagina di documenti (<c>GET /documents</c>): i documenti dell'elenco non hanno <c>contacts</c>.</summary>
    /// <param name="body">I byte del corpo.</param>
    /// <param name="context">Lo status e l'id di correlazione della risposta.</param>
    /// <exception cref="UnexpectedResponseException">Il corpo non ha la forma di una pagina di documenti.</exception>
    internal static Page<Document> ReadPage(byte[]? body, WireContext context) =>
        PageWire.ReadPage(body, context, ReadDocument);

    /// <summary>Legge il risultato di uno spostamento in blocco: <c>{"moved":N}</c>, quanti documenti sono stati spostati davvero.</summary>
    /// <param name="body">I byte del corpo.</param>
    /// <param name="context">Lo status e l'id di correlazione della risposta.</param>
    /// <exception cref="UnexpectedResponseException">Manca <c>moved</c> o non e' un intero non negativo.</exception>
    internal static int ReadMoved(byte[]? body, WireContext context) =>
        WireJson.ReadObject(body, context, "risultato", result => result.RequiredCount("moved"));

    /// <summary>
    /// Il percorso dell'elenco dei documenti: <c>documents</c> piu' i filtri nell'ordine di <see cref="DocumentQuery"/> (<c>folder_id</c>,
    /// <c>owner</c>, <c>tag</c>, <c>filename</c>, <c>sender</c>, <c>recipient</c>, <c>sender_id</c>, <c>recipient_id</c>, <c>q</c>,
    /// <c>metadata_query</c>, <c>metadata</c>, <c>created_from</c>, <c>created_to</c>) e poi <c>limit</c> e <c>cursor</c>. Solo i nomi canonici (mai
    /// gli alias italiani del server). Un filtro null non produce nessun parametro; un testo si manda cosi' com'e' (il server lo trimma). Il
    /// filtro sui metadati e' il testo JSON dell'elemento (<c>GetRawText</c>); le date sono istanti UTC con la <c>Z</c>, mai solo-data.
    /// </summary>
    /// <param name="query">I filtri; null per non filtrare. Si valida con <see cref="DocumentQuery.Validate"/> prima di costruire.</param>
    /// <param name="page">La pagina; null per la prima con il limite di default del server.</param>
    /// <exception cref="ArgumentException">I filtri non sono validi, o un testo ha un surrogato isolato.</exception>
    internal static string ListPath(DocumentQuery? query, PageRequest? page)
    {
        query?.Validate();
        var builder = new QueryBuilder();
        if (query is not null)
        {
            builder
                .Add("folder_id", query.FolderId?.Value, nameof(DocumentQuery.FolderId))
                .Add("owner", query.Owner, nameof(DocumentQuery.Owner))
                .Add("tag", query.Tag, nameof(DocumentQuery.Tag))
                .Add("filename", query.FileName, nameof(DocumentQuery.FileName))
                .Add("sender", query.Sender, nameof(DocumentQuery.Sender))
                .Add("recipient", query.Recipient, nameof(DocumentQuery.Recipient))
                .Add("sender_id", query.SenderId?.Value, nameof(DocumentQuery.SenderId))
                .Add("recipient_id", query.RecipientId?.Value, nameof(DocumentQuery.RecipientId))
                .Add("q", query.Text, nameof(DocumentQuery.Text))
                .Add("metadata_query", query.MetadataText, nameof(DocumentQuery.MetadataText))
                .Add("metadata", query.Metadata?.GetRawText(), nameof(DocumentQuery.Metadata))
                .Add("created_from", query.CreatedFrom is { } from ? WireDates.Format(from) : null, nameof(DocumentQuery.CreatedFrom))
                .Add("created_to", query.CreatedBefore is { } before ? WireDates.Format(before) : null, nameof(DocumentQuery.CreatedBefore));
        }

        PageWire.AddPage(builder, page);
        return Routes.Documents + builder.Build();
    }

    /// <summary>
    /// Il corpo di <c>PATCH documents/{id}/folder</c>: <c>{"folder_id":"CODICE"}</c>, oppure <c>{"folder_id":null}</c> per la radice (il
    /// <c>null</c> e' esplicito, come nella cattura 138: il server accetta anche <c>{}</c> ma non e' il modo di dirlo).
    /// </summary>
    /// <param name="folder">La cartella di destinazione; null per la radice.</param>
    /// <exception cref="ArgumentException"><paramref name="folder"/> e' il codice vuoto: per la radice si passa null.</exception>
    internal static byte[] MoveBody(FolderCode? folder)
    {
        RequireNotEmpty(folder, nameof(folder));
        return WireJson.Write(writer =>
        {
            writer.WriteStartObject();
            WriteFolder(writer, folder);
            writer.WriteEndObject();
        });
    }

    /// <summary>
    /// Il corpo di <c>POST documents/bulk/move</c>: <c>{"document_ids":[...],"folder_id":"CODICE"|null}</c> (come nelle catture 133 e 134). Gli
    /// id si mandano nell'ordine dato, senza togliere i ripetuti (il server li conta una volta).
    /// </summary>
    /// <param name="ids">Gli id; almeno uno, nessuno vuoto.</param>
    /// <param name="folder">La cartella di destinazione; null per la radice.</param>
    /// <exception cref="ArgumentNullException"><paramref name="ids"/> e' null.</exception>
    /// <exception cref="ArgumentException"><paramref name="ids"/> e' vuota o ha un id vuoto, oppure <paramref name="folder"/> e' il codice vuoto.</exception>
    internal static byte[] BulkMoveBody(IReadOnlyCollection<DocumentId> ids, FolderCode? folder)
    {
        RequireIds(ids);
        RequireNotEmpty(folder, nameof(folder));
        return WireJson.Write(writer =>
        {
            writer.WriteStartObject();
            WriteIds(writer, ids);
            WriteFolder(writer, folder);
            writer.WriteEndObject();
        });
    }

    /// <summary>Il corpo di <c>POST documents/bulk/verify</c>: <c>{"document_ids":[...]}</c> (come nella cattura 129).</summary>
    /// <param name="ids">Gli id; almeno uno, nessuno vuoto.</param>
    /// <exception cref="ArgumentNullException"><paramref name="ids"/> e' null.</exception>
    /// <exception cref="ArgumentException"><paramref name="ids"/> e' vuota o ha un id vuoto.</exception>
    internal static byte[] BulkVerifyBody(IReadOnlyCollection<DocumentId> ids)
    {
        RequireIds(ids);
        return WireJson.Write(writer =>
        {
            writer.WriteStartObject();
            WriteIds(writer, ids);
            writer.WriteEndObject();
        });
    }

    /// <summary>
    /// I campi di testo del modulo multipart di un caricamento, nell'ordine <c>folder_id</c>, <c>owner</c>, <c>tag</c>, <c>sender</c>,
    /// <c>recipient</c>, <c>metadata</c> e senza quelli null: il file e' una parte a parte (si manda per ultima, cosi' il server ha gia'
    /// letto i campi). I valori si mandano cosi' come sono (il server li trimma e tratta il vuoto come assente); i metadati sono il testo
    /// JSON dell'elemento (<c>GetRawText</c>, lo stesso misurato da <see cref="UploadDocumentRequest.Validate"/>).
    /// </summary>
    /// <param name="request">La richiesta; si valida con <see cref="UploadDocumentRequest.Validate"/> prima di costruire (senza leggere lo stream).</param>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> e' null.</exception>
    /// <exception cref="ArgumentException">La richiesta non e' valida, o un testo ha un surrogato isolato.</exception>
    internal static IReadOnlyList<KeyValuePair<string, string>> UploadFields(UploadDocumentRequest request)
    {
        Guard.NotNull(request);
        request.Validate();
        var fields = new List<KeyValuePair<string, string>>(6);
        Add(fields, "folder_id", request.FolderId?.Value, nameof(UploadDocumentRequest.FolderId));
        Add(fields, "owner", request.Owner, nameof(UploadDocumentRequest.Owner));
        Add(fields, "tag", request.Tag, nameof(UploadDocumentRequest.Tag));
        Add(fields, "sender", request.Sender, nameof(UploadDocumentRequest.Sender));
        Add(fields, "recipient", request.Recipient, nameof(UploadDocumentRequest.Recipient));
        Add(fields, "metadata", request.Metadata?.GetRawText(), nameof(UploadDocumentRequest.Metadata));
        return fields;
    }

    private static void Add(List<KeyValuePair<string, string>> fields, string name, string? value, string paramName)
    {
        if (value is null)
        {
            return;
        }

        PercentEncoding.RequireWellFormed(value, paramName);
        fields.Add(new KeyValuePair<string, string>(name, value));
    }

    private static void RequireNotEmpty(FolderCode? folder, string paramName)
    {
        if (folder is { IsEmpty: true })
        {
            throw new ArgumentException("Il codice della cartella e' vuoto (default): per la radice si passa null.", paramName);
        }
    }

    private static void RequireIds(IReadOnlyCollection<DocumentId> ids)
    {
        Guard.NotNull(ids);
        if (ids.Count == 0)
        {
            throw new ArgumentException("Serve almeno un id di documento.", nameof(ids));
        }

        foreach (var id in ids)
        {
            if (id.IsEmpty)
            {
                throw new ArgumentException("Un id di documento e' vuoto (default).", nameof(ids));
            }
        }
    }

    private static void WriteIds(Utf8JsonWriter writer, IReadOnlyCollection<DocumentId> ids)
    {
        writer.WriteStartArray("document_ids");
        foreach (var id in ids)
        {
            writer.WriteStringValue(id.Value);
        }

        writer.WriteEndArray();
    }

    // La radice e' un null ESPLICITO: {"folder_id":null}.
    private static void WriteFolder(Utf8JsonWriter writer, FolderCode? folder)
    {
        if (folder is { } code)
        {
            writer.WriteString("folder_id", code.Value);
        }
        else
        {
            writer.WriteNull("folder_id");
        }
    }

    private static Document ReadDocument(WireObject document) =>
        new(
            document.RequiredId<DocumentId>("id", DocumentId.TryParse, "un id di documento"),
            document.OptionalId<FolderCode>("folder_id", FolderCode.TryParse, "un codice di cartella"),
            document.RequiredString("original_filename"),
            document.RequiredString("mime_type"),
            document.OptionalSha256("sha256"),
            document.RequiredSize("size_bytes"),
            document.OptionalString("owner"),
            document.OptionalString("tag"),
            document.OptionalString("sender"),
            document.OptionalString("recipient"),
            document.OptionalObjectCopy("metadata") ?? EmptyObject,
            document.RequiredDate("created_at"),
            document.OptionalList("contacts", ReadContact)!);

    private static DocumentContact ReadContact(WireObject contact) =>
        new(
            RoleOf(contact.OptionalString("role")),
            contact.RequiredId<ContactId>("id", ContactId.TryParse, "un id di contatto"),
            contact.RequiredString("name"));

    // Confronto ordinale. Assente o non riconosciuto e' Unknown (e' il default dell'enum).
    private static ContactRole RoleOf(string? role) =>
        role switch
        {
            "sender" => ContactRole.Sender,
            "recipient" => ContactRole.Recipient,
            _ => ContactRole.Unknown,
        };

    private static JsonElement CreateEmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
}
