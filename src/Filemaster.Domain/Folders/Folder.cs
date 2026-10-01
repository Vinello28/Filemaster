namespace Filemaster.Domain;

/// <summary>
/// Cartella logica dentro un ente. La identifica il codice scelto da chi la crea (<see cref="FolderCode"/>), unico
/// nell'ente. Il server non sposta le cartelle: <see cref="ParentId"/> si fissa alla creazione, quindi un albero non ha
/// cicli. Il codice si puo' cambiare con la rinomina, e sottocartelle e documenti lo seguono.
/// </summary>
public sealed record Folder(FolderCode Id, FolderCode? ParentId, string Name, DateTimeOffset CreatedAt)
{
    /// <summary>Il codice della cartella.</summary>
    public FolderCode Id { get; } = Id;

    /// <summary>Il codice della cartella madre; null per una cartella di primo livello (radice).</summary>
    public FolderCode? ParentId { get; } = ParentId;

    /// <summary>Il nome della cartella, per le persone.</summary>
    public string Name { get; } = Name;

    /// <summary>Quando la cartella e' stata creata (UTC).</summary>
    public DateTimeOffset CreatedAt { get; } = CreatedAt;
}
