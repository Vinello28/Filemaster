namespace Filemaster.Domain;

/// <summary>
/// Genere di un contatto dell'anagrafica (<see cref="Contact.Kind"/>). Un valore che questa versione del client non
/// conosce diventa <see cref="Unknown"/>: non e' mai un errore.
/// </summary>
public enum ContactKind
{
    /// <summary>Valore non riconosciuto o assente. E' anche il <c>default</c> dell'enum.</summary>
    Unknown = 0,

    /// <summary>Persona o organizzazione esterna.</summary>
    External = 1,

    /// <summary>Utente interno.</summary>
    User = 2,

    /// <summary>Gruppo o ufficio interno.</summary>
    Group = 3,
}
