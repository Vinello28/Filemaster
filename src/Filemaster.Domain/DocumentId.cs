using System.Diagnostics.CodeAnalysis;

namespace Filemaster.Domain;

/// <summary>
/// Identificatore di un documento: un intero positivo (<c>bigint</c> sul server, da 1 a <see cref="long.MaxValue"/>)
/// scritto in decimale canonico, cioe' solo cifre da 0 a 9, senza segno, senza zeri iniziali, senza spazi. Il confronto
/// e' esatto. Un id non canonico (<c>042</c>, <c>+42</c>, spazi, a capo, il vecchio <c>doc_...</c>) non esiste: il
/// server risponderebbe 404, quindi il client lo rifiuta prima.
/// </summary>
/// <remarks>
/// <c>default(DocumentId)</c> e' l'id vuoto: <see cref="Number"/> e' 0, <see cref="Value"/> e' la stringa vuota (mai
/// null), <see cref="IsEmpty"/> e' vero, <c>ToString()</c>, <c>Equals</c> e <c>GetHashCode</c> non lanciano. Non e' un
/// id valido: non si puo' ottenere da <c>Parse</c>, da <see cref="From(long)"/> o dal costruttore, e chi riceve un id da
/// usare deve rifiutare <see cref="IsEmpty"/>. Nessuna conversione implicita da o verso <see cref="string"/>.
/// </remarks>
public readonly record struct DocumentId
{
    private readonly long _number;

    /// <summary>Crea l'id da una stringa in forma canonica.</summary>
    /// <param name="value">Il valore, per esempio <c>42</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> e' null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> non e' un id di documento canonico: vuota, con segno, con zeri iniziali, con spazi o
    /// caratteri che non sono cifre da 0 a 9, oppure fuori dall'intervallo da 1 a <see cref="long.MaxValue"/>.
    /// </exception>
    public DocumentId(string value) => _number = NumericId.Require(value, long.MaxValue, "un id di documento", nameof(value));

    /// <summary>Il numero dell'id; 0 per <c>default</c>, altrimenti da 1 a <see cref="long.MaxValue"/>.</summary>
    public long Number => _number;

    /// <summary>Il valore canonico (decimale, cultura invariante); stringa vuota per <c>default</c>.</summary>
    public string Value => NumericId.Format(_number);

    /// <summary>Vero per <c>default(DocumentId)</c>, l'id vuoto.</summary>
    public bool IsEmpty => _number == 0;

    /// <summary>Crea l'id da un numero.</summary>
    /// <param name="number">Il numero, da 1 a <see cref="long.MaxValue"/>.</param>
    /// <returns>L'id.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="number"/> e' zero o negativo.</exception>
    public static DocumentId From(long number) =>
        new(NumericId.Format(NumericId.RequirePositive(number, "un id di documento", nameof(number))));

    /// <summary>Vero se <paramref name="value"/> e' un id di documento in forma canonica.</summary>
    /// <param name="value">La stringa da controllare; null e' semplicemente non valido.</param>
    public static bool IsValid([NotNullWhen(true)] string? value) => NumericId.TryParse(value, long.MaxValue, out _);

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
    /// <param name="value">Il valore, per esempio <c>42</c>.</param>
    /// <returns>L'id.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> e' null.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> non e' un id di documento canonico.</exception>
    public static DocumentId Parse(string value) => new(value);

    /// <summary>Il valore canonico (stringa vuota per <c>default</c>).</summary>
    /// <returns><see cref="Value"/>.</returns>
    public override string ToString() => Value;
}
