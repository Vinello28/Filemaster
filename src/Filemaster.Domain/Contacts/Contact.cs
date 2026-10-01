namespace Filemaster.Domain;

/// <summary>
/// Voce dell'anagrafica dell'ente: un mittente o destinatario che i documenti possono citare
/// (<see cref="DocumentContact"/>). Il client la legge soltanto. Le voci importate da ARXivar hanno
/// <see cref="ArxivarId"/>. Di tutti i campi descrittivi il server omette quelli vuoti, quindi sono nullable.
/// </summary>
public sealed record Contact(
    ContactId Id,
    string Name,
    ContactKind Kind,
    string? CategoryId,
    string? Code,
    string? Address,
    string? PostalCode,
    string? City,
    string? Province,
    string? Country,
    string? Email,
    string? Pec,
    string? Phone,
    string? Fax,
    string? Mobile,
    string? VatNumber,
    string? TaxCode,
    string? IpaCode,
    string? OfficeCode,
    string? Notes,
    int? ArxivarId,
    DateTimeOffset CreatedAt,
    int? DocumentsAsSender,
    int? DocumentsAsRecipient)
{
    /// <summary>L'id del contatto.</summary>
    public ContactId Id { get; } = Id;

    /// <summary>Il nome (o la ragione sociale) del contatto.</summary>
    public string Name { get; } = Name;

    /// <summary>Il genere del contatto; <see cref="ContactKind.Unknown"/> per un valore che il client non conosce.</summary>
    public ContactKind Kind { get; } = Kind;

    /// <summary>Il codice della categoria (<see cref="ContactCategory.Id"/>); null se il contatto non ne ha.</summary>
    public string? CategoryId { get; } = CategoryId;

    /// <summary>Un codice del contatto scelto da chi lo gestisce (non e' un id).</summary>
    public string? Code { get; } = Code;

    /// <summary>L'indirizzo.</summary>
    public string? Address { get; } = Address;

    /// <summary>Il CAP.</summary>
    public string? PostalCode { get; } = PostalCode;

    /// <summary>La localita'.</summary>
    public string? City { get; } = City;

    /// <summary>La provincia.</summary>
    public string? Province { get; } = Province;

    /// <summary>La nazione.</summary>
    public string? Country { get; } = Country;

    /// <summary>L'indirizzo email.</summary>
    public string? Email { get; } = Email;

    /// <summary>L'indirizzo di posta elettronica certificata (PEC).</summary>
    public string? Pec { get; } = Pec;

    /// <summary>Il telefono.</summary>
    public string? Phone { get; } = Phone;

    /// <summary>Il fax.</summary>
    public string? Fax { get; } = Fax;

    /// <summary>Il cellulare.</summary>
    public string? Mobile { get; } = Mobile;

    /// <summary>La partita IVA.</summary>
    public string? VatNumber { get; } = VatNumber;

    /// <summary>Il codice fiscale.</summary>
    public string? TaxCode { get; } = TaxCode;

    /// <summary>Il codice IPA (indice delle pubbliche amministrazioni).</summary>
    public string? IpaCode { get; } = IpaCode;

    /// <summary>Il codice dell'ufficio.</summary>
    public string? OfficeCode { get; } = OfficeCode;

    /// <summary>Le note.</summary>
    public string? Notes { get; } = Notes;

    /// <summary>L'id della voce in ARXivar (<c>DM_RUBRICA.SYSTEM_ID</c>) se e' stata importata da li'; null altrimenti.</summary>
    public int? ArxivarId { get; } = ArxivarId;

    /// <summary>Quando la voce e' stata creata (UTC).</summary>
    public DateTimeOffset CreatedAt { get; } = CreatedAt;

    /// <summary>
    /// Quanti documenti hanno il contatto come mittente. Il server lo manda solo negli elenchi: nel dettaglio di un
    /// contatto e' null (non zero).
    /// </summary>
    public int? DocumentsAsSender { get; } = DocumentsAsSender;

    /// <summary>
    /// Quanti documenti hanno il contatto come destinatario. Il server lo manda solo negli elenchi: nel dettaglio di un
    /// contatto e' null (non zero).
    /// </summary>
    public int? DocumentsAsRecipient { get; } = DocumentsAsRecipient;
}
