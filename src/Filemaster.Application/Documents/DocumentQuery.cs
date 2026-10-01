using System.Text.Json;
using Filemaster.Domain;

namespace Filemaster.Application;

/// <summary>
/// I filtri dell'elenco dei documenti (<c>GET /documents</c>): tutti e soli quelli che il server dev accetta. Tutti opzionali e
/// in <c>AND</c>; senza filtri si elencano tutti i documenti dell'ente, dal piu' recente (data di creazione e poi id, in ordine
/// decrescente). E' un tipo di input: nessun argomento obbligatorio, i filtri si impostano con <c>set</c> (nessun
/// <c>init</c>, quindi si usa anche da C# 7.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Cosa non c'e'.</b> Il server non filtra per tipo MIME, per presenza del contenuto (<c>has_content</c>) ne' per "solo
/// i documenti senza cartella": un parametro che non conosce lo ignora in silenzio e restituisce tutto, quindi qui non ci sono
/// proprieta' per questi filtri. Si filtra sul risultato (<see cref="Document.MimeType"/>, <see cref="Document.HasContent"/>,
/// <see cref="Document.FolderId"/> null).
/// </para>
/// <para>
/// <b>Testo e confronto.</b> <see cref="Owner"/> e <see cref="Tag"/> sono confronti esatti (maiuscole, minuscole e accenti
/// distinti). <see cref="FileName"/>, <see cref="Sender"/>, <see cref="Recipient"/>, <see cref="Text"/> e
/// <see cref="MetadataText"/> sono "contiene", senza distinguere maiuscole e minuscole ma distinguendo gli accenti; i caratteri
/// speciali di <c>LIKE</c> (<c>%</c>, <c>_</c>, <c>[</c>) valgono letteralmente. Il server trimma ogni testo e ignora quelli
/// vuoti o di soli spazi: <see cref="Validate"/> li accetta (equivalgono a null). Nessun limite di lunghezza lato server, ma
/// i filtri viaggiano nella query string: oltre circa 8 KiB di riga di richiesta (il default del server web Kestrel, non
/// verificato dal client) la richiesta fallisce con un errore dell'host, non con <c>InvalidRequestException</c>.
/// </para>
/// </remarks>
public sealed class DocumentQuery
{
    /// <summary>
    /// Solo i documenti di questa cartella (<c>folder_id</c>): esattamente quella, non le sue sottocartelle. Null per non
    /// filtrare; <b>non</b> vuol dire "la radice" (il server non ha un filtro per i soli documenti senza cartella). Un codice
    /// valido ma inesistente da' un elenco vuoto, non un errore.
    /// </summary>
    public FolderCode? FolderId { get; set; }

    /// <summary>Solo i documenti con questo proprietario (<c>owner</c>), confronto esatto.</summary>
    public string? Owner { get; set; }

    /// <summary>Solo i documenti con questa etichetta (<c>tag</c>), confronto esatto.</summary>
    public string? Tag { get; set; }

    /// <summary>Solo i documenti il cui nome file contiene questo testo (<c>filename</c>).</summary>
    public string? FileName { get; set; }

    /// <summary>
    /// Solo i documenti il cui mittente, il testo libero indicato al caricamento (<see cref="Document.Sender"/>), contiene
    /// questo testo (<c>sender</c>). Per cercare per contatto dell'anagrafica si usa <see cref="SenderId"/>.
    /// </summary>
    public string? Sender { get; set; }

    /// <summary>
    /// Solo i documenti il cui destinatario, il testo libero indicato al caricamento (<see cref="Document.Recipient"/>),
    /// contiene questo testo (<c>recipient</c>). Per cercare per contatto dell'anagrafica si usa <see cref="RecipientId"/>.
    /// </summary>
    public string? Recipient { get; set; }

    /// <summary>Solo i documenti che hanno questo contatto dell'anagrafica come mittente (<c>sender_id</c>).</summary>
    public ContactId? SenderId { get; set; }

    /// <summary>Solo i documenti che hanno questo contatto dell'anagrafica come destinatario (<c>recipient_id</c>).</summary>
    public ContactId? RecipientId { get; set; }

    /// <summary>
    /// Ricerca libera (<c>q</c>): il documento compare se il testo e' contenuto nel nome del file, nel mittente, nel
    /// destinatario o nel testo dei metadati (il JSON cosi' com'e' salvato, chiavi comprese).
    /// </summary>
    public string? Text { get; set; }

    /// <summary>
    /// Solo i documenti il cui testo dei metadati (il JSON cosi' com'e' salvato, chiavi comprese) contiene questo testo
    /// (<c>metadata_query</c>). Per un filtro sulla struttura si usa <see cref="Metadata"/>.
    /// </summary>
    public string? MetadataText { get; set; }

    /// <summary>
    /// Filtro per contenimento sui metadati (<c>metadata</c>): un oggetto JSON che i metadati del documento devono
    /// contenere. Un oggetto contiene un altro se ne ha tutte le chiavi con un valore che contiene quello del filtro; un array
    /// contiene un altro se ogni elemento del filtro e' in almeno un suo elemento (ordine e duplicati non contano); i valori
    /// semplici devono avere lo stesso tipo e lo stesso valore (<c>"1"</c> non e' <c>1</c>; i numeri si confrontano per
    /// valore, <c>1</c> e <c>1.0</c> sono uguali); chiavi e testi si confrontano byte per byte, maiuscole comprese. Per
    /// esempio <c>{"arxivar":{"docnumber":61617}}</c> trova il documento importato dal profilo ARXivar 61617. Null per non
    /// filtrare.
    /// </summary>
    /// <remarks>
    /// <see cref="Validate"/> applica i limiti del server: un oggetto JSON (non <c>null</c>, non un array, non un valore
    /// semplice) di al massimo 64 KiB, con al massimo 64 valori in tutto (radice, oggetti, array e valori semplici) e 16
    /// livelli di annidamento. Il filtro viaggia nella query string, quindi in pratica il tetto e' la riga di richiesta del
    /// server web (vedi le note del tipo). L'elemento deve restare valido (il suo <see cref="JsonDocument"/> non smaltito)
    /// fino alla fine della chiamata.
    /// </remarks>
    public JsonElement? Metadata { get; set; }

    /// <summary>
    /// Solo i documenti creati da questo istante, <b>incluso</b> (<c>created_from</c>). E' un <see cref="DateTimeOffset"/>
    /// perche' il server vuole l'istante con il fuso: <c>created_from</c> senza offset e' un 400 sul server dev. Il server
    /// accetta anche una data senza ora (<c>2026-09-21</c>), ma la legge nel fuso del server: non e' esprimibile qui di
    /// proposito, perche' dipende da una configurazione che il client non conosce.
    /// </summary>
    public DateTimeOffset? CreatedFrom { get; set; }

    /// <summary>
    /// Solo i documenti creati prima di questo istante, <b>escluso</b> (<c>created_to</c>): il nome dice che il limite non
    /// e' incluso, a differenza di <see cref="CreatedFrom"/>. Deve essere successivo a <see cref="CreatedFrom"/> se
    /// entrambi sono impostati. Vale la stessa nota sul fuso di <see cref="CreatedFrom"/>.
    /// </summary>
    public DateTimeOffset? CreatedBefore { get; set; }

    /// <summary>Controlla i valori prima di spedire; lancia alla prima violazione, nell'ordine delle proprieta'.</summary>
    /// <exception cref="ArgumentException">
    /// <see cref="FolderId"/>, <see cref="SenderId"/> o <see cref="RecipientId"/> e' il valore vuoto (<c>default</c>);
    /// <see cref="Metadata"/> non e' un oggetto JSON o supera i limiti del server (64 KiB, 64 valori, 16 livelli);
    /// <see cref="CreatedFrom"/> non precede <see cref="CreatedBefore"/> (il confronto e' fra istanti, quindi indipendente dagli
    /// offset: il server risponde 400 se l'inizio non e' strettamente prima della fine). <see cref="ArgumentException.ParamName"/>
    /// e' il nome della proprieta'; per le date e' <see cref="CreatedBefore"/>.
    /// </exception>
    public void Validate()
    {
        RequestChecks.NotEmpty(FolderId?.IsEmpty, nameof(FolderId), "Il codice della cartella");
        RequestChecks.NotEmpty(SenderId?.IsEmpty, nameof(SenderId), "L'id del contatto mittente");
        RequestChecks.NotEmpty(RecipientId?.IsEmpty, nameof(RecipientId), "L'id del contatto destinatario");
        if (Metadata is { } metadata)
        {
            RequestChecks.JsonObject(metadata, nameof(Metadata));
            RequestChecks.JsonFilterComplexity(metadata, nameof(Metadata));
        }

        if (CreatedFrom is { } start && CreatedBefore is { } end && start >= end)
        {
            throw new ArgumentException("CreatedFrom deve precedere CreatedBefore (l'inizio e' incluso, la fine esclusa).", nameof(CreatedBefore));
        }
    }
}
