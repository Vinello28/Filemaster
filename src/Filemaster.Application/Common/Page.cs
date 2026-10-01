namespace Filemaster.Application;

/// <summary>
/// Una pagina di un elenco e il cursore per chiedere la successiva. Si legge da qualunque versione di C#; per scorrere
/// tutte le pagine si passa <see cref="NextCursor"/> a un nuovo <see cref="PageRequest"/> finche' non e' null.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="NextCursor"/> e' null sull'ultima pagina, anche quando e' piena: il server legge un elemento in piu' del
/// limite per sapere se ne restano. Quindi una pagina con meno elementi del limite e' sempre l'ultima, ma una pagina piena
/// non e' detto che lo sia: l'unico segnale e' il cursore.
/// </para>
/// <para>
/// Come per ogni record del Domain con una lista, l'uguaglianza confronta <see cref="Items"/> per riferimento: due pagine
/// con gli stessi elementi, lette da due risposte, non sono uguali.
/// </para>
/// </remarks>
/// <typeparam name="T">Il tipo degli elementi.</typeparam>
public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor)
{
    /// <summary>Gli elementi della pagina, nell'ordine del server. Mai null: un valore null passato al costruttore diventa una lista vuota.</summary>
    public IReadOnlyList<T> Items { get; } = Items ?? Array.Empty<T>();

    /// <summary>
    /// Il cursore opaco della pagina successiva, da passare cosi' com'e' a <see cref="PageRequest"/> insieme agli stessi filtri;
    /// null se questa e' l'ultima pagina.
    /// </summary>
    public string? NextCursor { get; } = NextCursor;
}
