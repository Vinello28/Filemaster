using System.Text.Json;

namespace Filemaster.Domain;

/// <summary>
/// Un documento: nome, tipo, impronta e dimensione del contenuto, attributi liberi e metadati. E' cio' che restituiscono
/// il caricamento, l'elenco e il dettaglio; il contenuto (i byte) si legge a parte.
/// </summary>
/// <remarks>
/// <para>
/// Come ogni record di output del Domain e' immutabile: il costruttore e' posizionale, le proprieta' sono solo in
/// lettura (nessun <c>init</c>, quindi nessun <c>with</c> che le modifichi) e si leggono da qualunque versione di C#.
/// </para>
/// <para>
/// Nessun contenuto. Un documento importato con i soli metadati (il file del profilo ARXivar non c'e') ha
/// <see cref="Sha256"/> null e <see cref="SizeBytes"/> zero: <see cref="HasContent"/> e' falso. Si cerca, si sposta e si
/// cancella, ma non si scarica ne' si verifica (il server risponde 409, una <see cref="ContentUnavailableException"/>).
/// </para>
/// <para>
/// Nessun metadato. <see cref="Metadata"/> e' sempre un oggetto JSON e "nessun metadato" e' l'oggetto vuoto <c>{}</c>:
/// e' cosi' che lo emette il server (mai null, mai omesso). <c>default(JsonElement)</c>, cioe' <c>ValueKind</c>
/// <c>Undefined</c>, non e' un valore che il client produce; chi costruisce un documento a mano e lo passa deve
/// aspettarsi che <see cref="ArxivarMetadata.From"/> lo tratti come assenza di metadati. Attenzione alla durata: un
/// <see cref="JsonElement"/> ricavato da un <see cref="JsonDocument"/> smaltito non e' piu' valido (ogni lettura lancia
/// <see cref="ObjectDisposedException"/>, e anche <c>ToString()</c> del record, che stampa i metadati). Perche' il
/// documento possa vivere oltre la lettura della risposta, Infrastructure fa <c>Clone()</c> dell'elemento prima di
/// consegnarlo: non lo fa questo tipo.
/// </para>
/// <para>
/// Contatti. <see cref="Contacts"/> e' vuota (mai null) quando il server non li manda. Il server li include solo nel
/// dettaglio di un documento: nei documenti di un elenco e nel risultato di un caricamento sono sempre vuoti, anche se
/// il documento ha dei contatti. Per averli si legge il dettaglio.
/// </para>
/// <para>
/// <b>Uguaglianza.</b> Come ogni record, <c>Equals</c> e <c>==</c> confrontano le proprieta' una per una, ma due di
/// queste non si confrontano per valore. <see cref="Metadata"/> (<see cref="JsonElement"/>) e' uguale a un altro solo se
/// viene dallo stesso <see cref="JsonDocument"/> nella stessa posizione: nemmeno <c>Clone()</c> restituisce un elemento
/// uguale all'originale. <see cref="Contacts"/> e' un <see cref="IReadOnlyList{T}"/> e si confronta per riferimento.
/// Quindi due documenti con gli stessi dati, letti da due risposte diverse, non sono uguali. Per confrontare i
/// documenti si usano i campi scalari (<see cref="Id"/>, <see cref="Sha256"/>, ...) e, per i metadati, il testo
/// (<c>Metadata.GetRawText()</c>).
/// </para>
/// <para>
/// Il caricamento restituisce anche se il contenuto era gia' presente (deduplica): e' un'informazione del risultato
/// del caricamento, non del documento, e non sta qui.
/// </para>
/// </remarks>
public sealed record Document(
    DocumentId Id,
    FolderCode? FolderId,
    string OriginalFilename,
    string MimeType,
    string? Sha256,
    long SizeBytes,
    string? Owner,
    string? Tag,
    string? Sender,
    string? Recipient,
    JsonElement Metadata,
    DateTimeOffset CreatedAt,
    IReadOnlyList<DocumentContact> Contacts)
{
    /// <summary>L'id del documento.</summary>
    public DocumentId Id { get; } = Id;

    /// <summary>Il codice della cartella che contiene il documento; null se sta nella radice (nessuna cartella).</summary>
    public FolderCode? FolderId { get; } = FolderId;

    /// <summary>Il nome del file com'era al caricamento.</summary>
    public string OriginalFilename { get; } = OriginalFilename;

    /// <summary>Il tipo di contenuto (MIME), per esempio <c>application/pdf</c>.</summary>
    public string MimeType { get; } = MimeType;

    /// <summary>
    /// Lo SHA-256 del contenuto, in esadecimale minuscolo; null se il documento non ha contenuto
    /// (<see cref="HasContent"/> falso).
    /// </summary>
    public string? Sha256 { get; } = Sha256;

    /// <summary>La dimensione del contenuto in byte; zero se il documento non ha contenuto.</summary>
    public long SizeBytes { get; } = SizeBytes;

    /// <summary>Il proprietario indicato al caricamento (testo libero); null se assente.</summary>
    public string? Owner { get; } = Owner;

    /// <summary>L'etichetta indicata al caricamento (testo libero); null se assente.</summary>
    public string? Tag { get; } = Tag;

    /// <summary>Il mittente indicato al caricamento (testo libero, non l'anagrafica: vedi <see cref="Contacts"/>); null se assente.</summary>
    public string? Sender { get; } = Sender;

    /// <summary>Il destinatario indicato al caricamento (testo libero, non l'anagrafica: vedi <see cref="Contacts"/>); null se assente.</summary>
    public string? Recipient { get; } = Recipient;

    /// <summary>
    /// I metadati liberi, al massimo 64 KiB: un oggetto JSON, <c>{}</c> se non ce ne sono. La sezione ARXivar si legge con
    /// <see cref="ArxivarMetadata.From"/>. Vedi le note del tipo su durata e uguaglianza.
    /// </summary>
    public JsonElement Metadata { get; } = Metadata;

    /// <summary>Quando il documento e' stato creato (UTC).</summary>
    public DateTimeOffset CreatedAt { get; } = CreatedAt;

    /// <summary>
    /// I mittenti e i destinatari in anagrafica. Mai null: vuota quando il server non li manda (elenchi e caricamenti) e
    /// quando il documento non ne ha. Un valore null passato al costruttore diventa una lista vuota.
    /// </summary>
    public IReadOnlyList<DocumentContact> Contacts { get; } = Contacts ?? Array.Empty<DocumentContact>();

    /// <summary>
    /// Vero se il documento ha un contenuto da scaricare e verificare, cioe' se <see cref="Sha256"/> c'e'. E' il
    /// <c>has_content</c> del server, che dice la stessa cosa, ed e' derivato: non puo' contraddire <see cref="Sha256"/>.
    /// </summary>
    public bool HasContent => Sha256 is not null;
}
