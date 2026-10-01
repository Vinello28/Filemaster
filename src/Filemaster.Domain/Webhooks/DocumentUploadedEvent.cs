namespace Filemaster.Domain;

/// <summary>
/// Un documento e' stato caricato (<c>document.uploaded</c>). Il payload ha solo l'essenziale: per il resto si legge il
/// documento dal suo id.
/// </summary>
public sealed record DocumentUploadedEvent(
    string DeliveryId,
    DateTimeOffset OccurredAt,
    DocumentId DocumentId,
    string OriginalFilename,
    string Sha256,
    bool Deduplicated)
    : WebhookEvent(DeliveryId, OccurredAt)
{
    /// <summary>Il documento caricato.</summary>
    public DocumentId DocumentId { get; } = DocumentId;

    /// <summary>Il nome del file com'era al caricamento.</summary>
    public string OriginalFilename { get; } = OriginalFilename;

    /// <summary>Lo SHA-256 del contenuto, in esadecimale minuscolo.</summary>
    public string Sha256 { get; } = Sha256;

    /// <summary>Vero se lo stesso contenuto (stessi byte, stesso ente) c'era gia': il documento e' comunque nuovo.</summary>
    public bool Deduplicated { get; } = Deduplicated;
}
