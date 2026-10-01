namespace Filemaster.Domain;

/// <summary>
/// Ruolo di un contatto in un documento (<see cref="DocumentContact.Role"/>). Un valore che questa versione del client
/// non conosce diventa <see cref="Unknown"/>: non e' mai un errore.
/// </summary>
public enum ContactRole
{
    /// <summary>Valore non riconosciuto o assente. E' anche il <c>default</c> dell'enum.</summary>
    Unknown = 0,

    /// <summary>Il contatto e' un mittente del documento.</summary>
    Sender = 1,

    /// <summary>Il contatto e' un destinatario del documento.</summary>
    Recipient = 2,
}
