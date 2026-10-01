namespace Filemaster.Application;

/// <summary>
/// Quale pagina di un elenco chiedere: il cursore restituito dalla pagina precedente e, a scelta, quanti elementi al
/// massimo. Senza argomenti e' la prima pagina con il limite di default del server (50). E' immutabile e si valida alla
/// costruzione: un valore fuori dai limiti non esiste, quindi non ha un <c>Validate()</c>.
/// </summary>
/// <remarks>
/// Si usa con gli elenchi paginati (documenti, contatti): <c>new PageRequest(page.NextCursor)</c> chiede la pagina dopo
/// <c>page</c>, con gli stessi filtri. Gli altri elenchi del server (cartelle, categorie dei contatti) non sono paginati.
/// </remarks>
public sealed record PageRequest
{
    /// <summary>Il limite massimo che il server accetta per pagina (oltre risponde 400).</summary>
    public const int MaxLimit = 200;

    /// <summary>Crea la richiesta di una pagina.</summary>
    /// <param name="cursor">
    /// Il <see cref="Page{T}.NextCursor"/> della pagina precedente, da passare cosi' com'e'; null per la prima pagina. Non puo'
    /// essere vuoto: per il server un cursore vuoto vale "prima pagina", e chi scorre le pagine con un cursore vuoto per
    /// errore ripartirebbe da capo senza mai finire.
    /// </param>
    /// <param name="limit">Quanti elementi al massimo, da 1 a <see cref="MaxLimit"/>; null per il default del server (50).</param>
    /// <exception cref="ArgumentException"><paramref name="cursor"/> e' vuoto o di soli spazi.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> e' minore di 1 o maggiore di <see cref="MaxLimit"/>.</exception>
    public PageRequest(string? cursor = null, int? limit = null)
    {
        if (cursor is not null && string.IsNullOrWhiteSpace(cursor))
        {
            throw new ArgumentException(
                "Il cursore non puo' essere vuoto: usare null per la prima pagina, oppure il NextCursor della pagina precedente.",
                nameof(cursor));
        }

        if (limit is < 1 or > MaxLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, $"Il limite deve essere compreso fra 1 e {MaxLimit}.");
        }

        Cursor = cursor;
        Limit = limit;
    }

    /// <summary>Il cursore opaco della pagina da leggere; null per la prima.</summary>
    public string? Cursor { get; }

    /// <summary>Il numero massimo di elementi per pagina; null per il default del server.</summary>
    public int? Limit { get; }
}
