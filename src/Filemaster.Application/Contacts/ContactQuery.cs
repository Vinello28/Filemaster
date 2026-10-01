using Filemaster.Domain;

namespace Filemaster.Application;

/// <summary>
/// I filtri dell'elenco dei contatti (<c>GET /contacts</c>): testo libero, categoria e genere. Tutti opzionali e in
/// <c>AND</c>; senza filtri si elencano tutti i contatti dell'ente. E' un tipo di input: nessun argomento obbligatorio, i
/// filtri si impostano con <c>set</c> (nessun <c>init</c>, quindi si usa anche da C# 7.3). L'elenco e' ordinato per nome e
/// poi per id.
/// </summary>
public sealed class ContactQuery
{
    /// <summary>
    /// Cerca un testo (<c>q</c>) nel nome, nel codice, nella localita', nella partita IVA e nel codice fiscale: il contatto
    /// compare se almeno uno dei cinque lo contiene, senza distinguere maiuscole e minuscole (ma distinguendo gli accenti).
    /// Il server trimma il testo e ignora un testo vuoto. Nessun limite di lunghezza.
    /// </summary>
    public string? Text { get; set; }

    /// <summary>
    /// Il codice di una categoria (<c>category_id</c>, vedi <see cref="ContactCategory.Id"/>): solo i contatti di quella
    /// categoria, confronto esatto. Ha la forma di un <see cref="FolderCode"/> (e' una stringa e non un
    /// <see cref="FolderCode"/> per non confondere le due cose: una categoria non e' una cartella). Una forma non valida e' un
    /// 404 sul server, quindi <see cref="Validate"/> la rifiuta. Una categoria che non esiste da' un elenco vuoto, non un errore.
    /// </summary>
    public string? CategoryId { get; set; }

    /// <summary>
    /// Il genere (<c>kind</c>): solo i contatti di quel genere. Sono ammessi <see cref="ContactKind.External"/>,
    /// <see cref="ContactKind.User"/> e <see cref="ContactKind.Group"/>; <see cref="ContactKind.Unknown"/> non e' un genere che
    /// il server conosca (un valore sconosciuto e' un 400), quindi <see cref="Validate"/> lo rifiuta.
    /// </summary>
    public ContactKind? Kind { get; set; }

    /// <summary>Controlla i valori prima di spedire; lancia alla prima violazione, nell'ordine delle proprieta'.</summary>
    /// <exception cref="ArgumentException">
    /// <see cref="CategoryId"/> non e' un codice valido (da 1 a 50 caratteri fra lettere e cifre ASCII, <c>_</c>, <c>.</c> e
    /// <c>-</c>, il primo alfanumerico; vuoto compreso), oppure <see cref="Kind"/> e' <see cref="ContactKind.Unknown"/> o un
    /// valore fuori dall'enum. <see cref="ArgumentException.ParamName"/> e' il nome della proprieta'.
    /// </exception>
    public void Validate()
    {
        if (CategoryId is not null && !FolderCode.IsValid(CategoryId))
        {
            throw new ArgumentException(
                "Il codice della categoria non e' valido: da 1 a 50 caratteri fra lettere e cifre ASCII, '_', '.' e '-', il primo alfanumerico.",
                nameof(CategoryId));
        }

        if (Kind is { } kind && kind != ContactKind.External && kind != ContactKind.User && kind != ContactKind.Group)
        {
            throw new ArgumentException("Il genere deve essere External, User o Group: Unknown non e' un genere che il server conosca.", nameof(Kind));
        }
    }
}
