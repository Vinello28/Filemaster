namespace Filemaster.Domain;

/// <summary>
/// Esito di una verifica d'integrita' di un documento: il server ricalcola lo SHA-256 del contenuto che ha su disco e lo
/// confronta con quello registrato, e tiene l'esito nel suo storico. Un contenuto che non torna e' un esito
/// (<see cref="Ok"/> falso), non un'eccezione; un documento senza contenuto non si verifica affatto (il server risponde
/// 409, che e' una <see cref="ContentUnavailableException"/>).
/// </summary>
public sealed record IntegrityCheck(DocumentId DocumentId, string Sha256, bool Ok, string? Detail, DateTimeOffset CheckedAt)
{
    /// <summary>Il documento verificato.</summary>
    public DocumentId DocumentId { get; } = DocumentId;

    /// <summary>Lo SHA-256 registrato per il contenuto, in esadecimale minuscolo.</summary>
    public string Sha256 { get; } = Sha256;

    /// <summary>Vero se lo SHA-256 ricalcolato coincide con quello registrato; falso se il contenuto e' alterato, corrotto o assente dal disco.</summary>
    public bool Ok { get; } = Ok;

    /// <summary>
    /// Il perche' di un esito negativo, in italiano e per le persone, mai per decidere (per esempio hash diverso o file
    /// assente sul disco); null quando <see cref="Ok"/> e' vero (il server omette il campo).
    /// </summary>
    public string? Detail { get; } = Detail;

    /// <summary>Quando e' stata fatta la verifica (UTC).</summary>
    public DateTimeOffset CheckedAt { get; } = CheckedAt;
}
