using System.Text.Json;
using Filemaster.Domain;

namespace Filemaster.Application;

/// <summary>
/// Richiesta di caricamento di un documento: il contenuto (uno <see cref="Stream"/>), il nome del file e, a scelta,
/// cartella, tipo di contenuto, proprietario, etichetta, mittente, destinatario e metadati liberi. E' un tipo di input:
/// il costruttore prende gli argomenti obbligatori e gli opzionali si impostano con <c>set</c> (nessun <c>init</c>, quindi
/// si usa anche da C# 7.3, per esempio da .NET Framework 4.8).
/// </summary>
/// <remarks>
/// <para>
/// <b>Validazione.</b> <see cref="Validate"/> controlla i limiti esatti del server (Sharp-a-File, ramo dev) e lancia
/// <see cref="ArgumentException"/> (con <see cref="ArgumentException.ParamName"/> uguale al nome della proprieta'), mai
/// <see cref="InvalidRequestException"/>, che e' il 400 del server. L'adapter la chiama prima di aprire la connessione
/// e prima di leggere lo stream, che <see cref="Validate"/> non legge e non riposiziona: dello stream controlla solo
/// <see cref="Stream.CanRead"/>. Il server misura i campi cosi' come arrivano; l'adapter li spedisce senza modificarli,
/// quindi il limite si controlla sul valore che l'utente ha impostato.
/// </para>
/// <para>
/// <b>Campi testuali</b> (<see cref="Owner"/>, <see cref="Tag"/>, <see cref="Sender"/>, <see cref="Recipient"/>): il server
/// li trimma, e un valore vuoto o di soli spazi vale "assente"; il documento restituito ha il valore trimmato (o null).
/// </para>
/// <para>
/// <b>Lo stream</b> e' letto una volta, dalla posizione corrente alla fine; non va riavvolto dal client e non e' chiuso
/// dalla libreria. Un caricamento non si ritenta mai da solo e il server non ha idempotenza: lo stesso contenuto caricato
/// due volte da' due documenti (il secondo con <see cref="UploadResult.Deduplicated"/> vero).
/// </para>
/// </remarks>
public sealed class UploadDocumentRequest
{
    /// <summary>Lunghezza massima, in caratteri e dopo il trim, di <see cref="Owner"/> e <see cref="Tag"/>.</summary>
    public const int MaxIndexedTextLength = RequestChecks.IndexedTextLength;

    /// <summary>
    /// Dimensione massima, in byte UTF-8 del valore grezzo (senza trim), di ogni campo testuale: <see cref="Owner"/>,
    /// <see cref="Tag"/>, <see cref="Sender"/> e <see cref="Recipient"/>.
    /// </summary>
    public const int MaxFieldBytes = 4096;

    /// <summary>Dimensione massima del testo JSON di <see cref="Metadata"/>, in byte UTF-8 (64 KiB).</summary>
    public const int MaxMetadataBytes = RequestChecks.JsonObjectBytes;

    /// <summary>Crea la richiesta con gli argomenti obbligatori.</summary>
    /// <param name="content">Il contenuto del file. La libreria lo legge dalla posizione corrente e non lo chiude.</param>
    /// <param name="fileName">Il nome del file, per esempio <c>fattura.pdf</c>; il server tiene la parte dopo l'ultimo <c>/</c> o <c>\</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> o <paramref name="fileName"/> e' null.</exception>
    public UploadDocumentRequest(Stream content, string fileName)
    {
        Guard.NotNull(content);
        Guard.NotNull(fileName);
        Content = content;
        FileName = fileName;
    }

    /// <summary>Il contenuto del file (obbligatorio). Deve essere leggibile (<see cref="Stream.CanRead"/>).</summary>
    public Stream Content { get; }

    /// <summary>
    /// Il nome del file (obbligatorio), che diventa <see cref="Document.OriginalFilename"/>. Il server toglie il percorso
    /// (tutto fino all'ultimo <c>/</c> o <c>\</c>: <c>C:\scansioni\a.pdf</c> diventa <c>a.pdf</c>) e rifiuta un nome vuoto o
    /// di soli spazi, anche <c>cartella/</c>, che dopo il taglio e' vuoto.
    /// </summary>
    public string FileName { get; }

    /// <summary>
    /// Il tipo di contenuto (MIME) dichiarato, per esempio <c>application/pdf</c>; null per lasciarlo riconoscere al server.
    /// Il server lo registra cosi' com'e' (un tipo dichiarato vince sul contenuto: un file di testo dichiarato
    /// <c>text/plain</c> resta <c>text/plain</c> anche con estensione <c>.pdf</c>) e lo riconosce dai primi byte solo se manca
    /// o e' <c>application/octet-stream</c>. L'anteprima e' possibile solo se il tipo risultante e' <c>application/pdf</c>.
    /// Deve avere la forma <c>tipo/sottotipo</c> e solo caratteri ASCII stampabili: lo richiede la spedizione come header,
    /// non il server.
    /// </summary>
    public string? ContentType { get; set; }

    /// <summary>
    /// Il codice della cartella di destinazione; null per la radice. La cartella deve esistere (altrimenti il server risponde
    /// 404, <see cref="NotFoundException"/>).
    /// </summary>
    public FolderCode? FolderId { get; set; }

    /// <summary>Il proprietario (testo libero), al massimo <see cref="MaxIndexedTextLength"/> caratteri dopo il trim; null per ometterlo.</summary>
    public string? Owner { get; set; }

    /// <summary>L'etichetta (testo libero), al massimo <see cref="MaxIndexedTextLength"/> caratteri dopo il trim; null per ometterla.</summary>
    public string? Tag { get; set; }

    /// <summary>
    /// Il mittente come testo libero (non un contatto dell'anagrafica), al massimo <see cref="MaxFieldBytes"/> byte UTF-8;
    /// null per ometterlo.
    /// </summary>
    public string? Sender { get; set; }

    /// <summary>
    /// Il destinatario come testo libero (non un contatto dell'anagrafica), al massimo <see cref="MaxFieldBytes"/> byte UTF-8;
    /// null per ometterlo.
    /// </summary>
    public string? Recipient { get; set; }

    /// <summary>
    /// I metadati liberi: un oggetto JSON (non un array, non un valore semplice, non <c>null</c>) di al massimo
    /// <see cref="MaxMetadataBytes"/> byte UTF-8, misurati sul testo di <see cref="JsonElement.GetRawText"/>, che e' quello
    /// che l'adapter spedisce. Null per non averne: il documento avra' <c>{}</c>. L'elemento deve restare valido
    /// (il suo <see cref="JsonDocument"/> non smaltito) fino alla fine della chiamata.
    /// </summary>
    public JsonElement? Metadata { get; set; }

    /// <summary>
    /// Controlla i limiti del server senza leggere ne' riposizionare lo stream (ne controlla solo
    /// <see cref="Stream.CanRead"/>). Lancia alla prima violazione, nell'ordine delle proprieta'.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Una proprieta' non rispetta i limiti: lo stream non e' leggibile, il nome file e' vuoto (anche dopo aver tolto il
    /// percorso), il tipo di contenuto e' malformato, <see cref="FolderId"/> e' il codice vuoto (<c>default</c>), un testo
    /// supera i suoi limiti (255 caratteri per owner e tag dopo il trim; 4096 byte UTF-8 sul valore grezzo per tutti e
    /// quattro), i metadati non sono un oggetto JSON o superano 64 KiB. <see cref="ArgumentException.ParamName"/> e' il nome
    /// della proprieta'.
    /// </exception>
    public void Validate()
    {
        if (!Content.CanRead)
        {
            throw new ArgumentException("Lo stream non e' leggibile (gia' chiuso, o aperto in sola scrittura).", nameof(Content));
        }

        var baseName = FileName.Substring(Math.Max(FileName.LastIndexOf('/'), FileName.LastIndexOf('\\')) + 1);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            throw new ArgumentException("Il nome del file e' vuoto (anche dopo aver tolto il percorso).", nameof(FileName));
        }

        RequestChecks.MediaType(ContentType, nameof(ContentType));
        RequestChecks.NotEmpty(FolderId?.IsEmpty, nameof(FolderId), "Il codice di cartella");
        RequestChecks.MaxUtf8Bytes(Owner, MaxFieldBytes, nameof(Owner));
        RequestChecks.MaxTrimmedLength(Owner, MaxIndexedTextLength, nameof(Owner));
        RequestChecks.MaxUtf8Bytes(Tag, MaxFieldBytes, nameof(Tag));
        RequestChecks.MaxTrimmedLength(Tag, MaxIndexedTextLength, nameof(Tag));
        RequestChecks.MaxUtf8Bytes(Sender, MaxFieldBytes, nameof(Sender));
        RequestChecks.MaxUtf8Bytes(Recipient, MaxFieldBytes, nameof(Recipient));
        if (Metadata is { } metadata)
        {
            RequestChecks.JsonObject(metadata, nameof(Metadata));
        }
    }
}
