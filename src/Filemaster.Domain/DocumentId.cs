using System.Diagnostics.CodeAnalysis;

namespace Filemaster.Domain;

/// <summary>
/// Identificatore di un documento: <c>doc_</c> seguito da un ULID canonico, cioe' 26 caratteri Crockford base32
/// maiuscoli (cifre e lettere senza I, L, O, U; il primo carattere da 0 a 7). Il confronto e' esatto. Un id non
/// canonico (minuscolo, spazi, a capo) non esiste: il server risponderebbe 404, quindi il client lo rifiuta prima.
/// </summary>
/// <remarks>
/// <c>default(DocumentId)</c> e' l'id vuoto: <see cref="Value"/> e' la stringa vuota (mai null), <see cref="IsEmpty"/>
/// e' vero, <c>ToString()</c>, <c>Equals</c> e <c>GetHashCode</c> non lanciano. Non e' un id valido: non si puo'
/// ottenere da <c>Parse</c> o dal costruttore, e chi riceve un id da usare deve rifiutare <see cref="IsEmpty"/>.
/// Nessuna conversione implicita da o verso <see cref="string"/>.
/// </remarks>
public readonly record struct DocumentId
{
    private const string Prefix = "doc_";

    private readonly string? _value;

    /// <summary>Crea l'id da una stringa in forma canonica.</summary>
    /// <param name="value">Il valore, per esempio <c>doc_01ARZ3NDEKTSV4RRFFQ69G5FAV</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> e' null.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> non e' un id di documento canonico.</exception>
    public DocumentId(string value) => _value = PrefixedId.Require(value, Prefix, "un id di documento", nameof(value));

    /// <summary>Il valore canonico; stringa vuota per <c>default</c>.</summary>
    public string Value => _value ?? string.Empty;

    /// <summary>Vero per <c>default(DocumentId)</c>, l'id vuoto.</summary>
    public bool IsEmpty => _value is null;

    /// <summary>Vero se <paramref name="value"/> e' un id di documento in forma canonica.</summary>
    /// <param name="value">La stringa da controllare; null e' semplicemente non valido.</param>
    public static bool IsValid([NotNullWhen(true)] string? value) => PrefixedId.IsValid(value, Prefix);

    /// <summary>Prova a interpretare <paramref name="value"/>; non lancia mai.</summary>
    /// <param name="value">La stringa da interpretare.</param>
    /// <param name="id">L'id, o <c>default</c> se <paramref name="value"/> non e' valido.</param>
    /// <returns>Vero se <paramref name="value"/> e' un id di documento in forma canonica.</returns>
    public static bool TryParse(string? value, out DocumentId id)
    {
        if (IsValid(value))
        {
            id = new DocumentId(value);
            return true;
        }

        id = default;
        return false;
    }

    /// <summary>Interpreta <paramref name="value"/> come id di documento.</summary>
    /// <param name="value">Il valore, per esempio <c>doc_01ARZ3NDEKTSV4RRFFQ69G5FAV</c>.</param>
    /// <returns>L'id.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> e' null.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> non e' un id di documento canonico.</exception>
    public static DocumentId Parse(string value) => new(value);

    /// <summary>Il valore canonico (stringa vuota per <c>default</c>).</summary>
    /// <returns><see cref="Value"/>.</returns>
    public override string ToString() => Value;
}
