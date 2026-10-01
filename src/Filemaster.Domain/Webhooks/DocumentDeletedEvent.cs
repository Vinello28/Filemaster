namespace Filemaster.Domain;

/// <summary>
/// Un documento e' stato cancellato (<c>document.deleted</c>). Il documento non si legge piu': l'evento porta quello che
/// serve a riconoscerlo.
/// </summary>
public sealed record DocumentDeletedEvent(string DeliveryId, DateTimeOffset OccurredAt, DocumentId DocumentId, string? Sha256)
    : WebhookEvent(DeliveryId, OccurredAt)
{
    /// <summary>Il documento cancellato.</summary>
    public DocumentId DocumentId { get; } = DocumentId;

    /// <summary>
    /// Lo SHA-256 del contenuto che aveva, in esadecimale minuscolo; null se era un documento senza contenuto (il server
    /// manda <c>null</c> esplicito nel payload, a differenza delle risposte delle API che i null li omettono).
    /// </summary>
    public string? Sha256 { get; } = Sha256;
}
