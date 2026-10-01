using Filemaster.Domain;

namespace Filemaster.Application;

/// <summary>
/// Richiesta di creazione di una cartella (<c>POST /folders</c>): il codice che la identifica, il nome e, a scelta, la
/// cartella madre. E' un tipo di input: il costruttore prende gli argomenti obbligatori e il resto si imposta con
/// <c>set</c> (nessun <c>init</c>, quindi si usa anche da C# 7.3).
/// </summary>
/// <remarks>
/// Il codice lo sceglie chi crea la cartella e non si genera: e' unico nell'ente (un duplicato e' un conflitto, 409) e si puo'
/// cambiare dopo con <see cref="UpdateFolderRequest.NewCode"/>. Il padre si fissa alla creazione e non si cambia piu'
/// (il server non sposta le cartelle). Due cartelle con lo stesso nome nello stesso padre non sono ammesse (409), con
/// distinzione fra maiuscole e minuscole.
/// </remarks>
public sealed class CreateFolderRequest
{
    /// <summary>Lunghezza massima del nome, in caratteri e dopo il trim.</summary>
    public const int MaxNameLength = RequestChecks.IndexedTextLength;

    /// <summary>Crea la richiesta con gli argomenti obbligatori.</summary>
    /// <param name="code">Il codice scelto per la cartella, per esempio <c>FATTURE</c>; unico nell'ente.</param>
    /// <param name="name">Il nome per le persone, per esempio <c>Fatture</c>; il server lo trimma.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> e' null.</exception>
    public CreateFolderRequest(FolderCode code, string name)
    {
        Guard.NotNull(name);
        Code = code;
        Name = name;
    }

    /// <summary>Il codice della nuova cartella (obbligatorio, non vuoto).</summary>
    public FolderCode Code { get; }

    /// <summary>
    /// Il nome della cartella (obbligatorio): non vuoto dopo il trim e di al massimo <see cref="MaxNameLength"/> caratteri
    /// (caratteri, non byte). Deve essere unico fra le cartelle con lo stesso padre.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Il codice della cartella madre; null per una cartella di primo livello. La madre deve esistere (altrimenti il server
    /// risponde 404, <see cref="NotFoundException"/>).
    /// </summary>
    public FolderCode? ParentId { get; set; }

    /// <summary>Controlla i limiti del server prima di spedire; lancia alla prima violazione, nell'ordine delle proprieta'.</summary>
    /// <exception cref="ArgumentException">
    /// <see cref="Code"/> o <see cref="ParentId"/> e' il codice vuoto (<c>default</c>), oppure <see cref="Name"/> e' vuoto o
    /// di soli spazi, o supera <see cref="MaxNameLength"/> caratteri dopo il trim. <see cref="ArgumentException.ParamName"/> e'
    /// il nome della proprieta'.
    /// </exception>
    public void Validate()
    {
        RequestChecks.NotEmpty(Code.IsEmpty, nameof(Code), "Il codice della cartella");
        RequestChecks.NotBlank(Name, nameof(Name));
        RequestChecks.MaxTrimmedLength(Name, MaxNameLength, nameof(Name));
        RequestChecks.NotEmpty(ParentId?.IsEmpty, nameof(ParentId), "Il codice della cartella madre");
    }
}
