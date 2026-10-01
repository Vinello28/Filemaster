namespace Filemaster.Domain;

/// <summary>
/// Categoria dell'anagrafica (per esempio "Associati"). La identifica un codice scelto da chi la crea, unico
/// nell'ente. Il codice ha la forma di un <see cref="FolderCode"/> (il server applica la stessa regola) ma non e' una
/// cartella: per non confondere i due namespace e' una <see cref="string"/> e non un <see cref="FolderCode"/>.
/// </summary>
public sealed record ContactCategory(string Id, string Name, int? ArxivarId)
{
    /// <summary>Il codice della categoria, per esempio <c>ASSOCIATI</c> o <c>ARX-12</c>.</summary>
    public string Id { get; } = Id;

    /// <summary>Il nome della categoria, per le persone.</summary>
    public string Name { get; } = Name;

    /// <summary>L'id della categoria in ARXivar (<c>DM_CATRUBRICHE.ID</c>) se e' stata importata da li'; null altrimenti.</summary>
    public int? ArxivarId { get; } = ArxivarId;
}
