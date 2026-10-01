namespace Filemaster.Domain;

/// <summary>
/// Un contatto dell'anagrafica collegato a un documento come mittente o destinatario. Il server lo include solo nel
/// dettaglio di un documento (<see cref="Document.Contacts"/>), con il nome che il contatto ha adesso.
/// </summary>
public sealed record DocumentContact(ContactRole Role, ContactId ContactId, string Name)
{
    /// <summary>Se il contatto e' mittente o destinatario del documento; <see cref="ContactRole.Unknown"/> per un ruolo che il client non conosce.</summary>
    public ContactRole Role { get; } = Role;

    /// <summary>L'id del contatto.</summary>
    public ContactId ContactId { get; } = ContactId;

    /// <summary>Il nome del contatto.</summary>
    public string Name { get; } = Name;
}
