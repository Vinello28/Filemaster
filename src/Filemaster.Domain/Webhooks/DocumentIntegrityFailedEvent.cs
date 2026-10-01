namespace Filemaster.Domain;

/// <summary>
/// Una verifica d'integrita' di un documento e' fallita (<c>document.integrity_failed</c>): il contenuto che il server ha
/// su disco non corrisponde allo SHA-256 registrato, oppure manca. Scatta a ogni verifica negativa, singola o in lotto.
/// </summary>
public sealed record DocumentIntegrityFailedEvent(string DeliveryId, DateTimeOffset OccurredAt, DocumentId DocumentId, string Sha256, string? Detail)
    : WebhookEvent(DeliveryId, OccurredAt)
{
    /// <summary>Il documento il cui contenuto non e' integro.</summary>
    public DocumentId DocumentId { get; } = DocumentId;

    /// <summary>Lo SHA-256 registrato per il contenuto (quello atteso), in esadecimale minuscolo.</summary>
    public string Sha256 { get; } = Sha256;

    /// <summary>
    /// Il perche' del fallimento, in italiano e per le persone, mai per decidere (per esempio hash diverso o file assente
    /// sul disco). Il server lo valorizza sempre per un fallimento; e' nullable perche' il payload non lo garantisce.
    /// </summary>
    public string? Detail { get; } = Detail;
}
