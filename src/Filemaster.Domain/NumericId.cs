using System.Globalization;

namespace Filemaster.Domain;

/// <summary>
/// Validazione condivisa degli id tipizzati (<see cref="DocumentId"/>, <see cref="ContactId"/>, <see cref="TenantId"/>):
/// un intero positivo scritto in decimale canonico. Aggiungere un tipo di id e' un nuovo struct che passa a questo
/// helper il proprio valore massimo.
/// </summary>
/// <remarks>
/// La regola e' quella del server (<c>Ids.TryParse</c> di Sharp-a-File: <c>NumberStyles.None</c>, valore maggiore di
/// zero, la stringa riletta dal numero deve coincidere con l'originale): solo cifre ASCII da <c>0</c> a <c>9</c>, nessun
/// segno, nessuno zero iniziale, nessuno spazio ne' a capo, valore da 1 al massimo del tipo (<c>int.MaxValue</c> per
/// contatti ed enti, <c>long.MaxValue</c> per i documenti). Tutto il resto per il server e' un 404, quindi il client lo
/// rifiuta prima. Il controllo e' un ciclo con confronti ASCII espliciti e un'accumulazione con controllo di
/// overflow: mai una regex (il suo <c>$</c> accetta un newline finale), mai <c>char.IsDigit</c> (accetta le cifre
/// Unicode, per esempio arabo-indiche o a larghezza intera) e mai <c>long.TryParse</c> da solo (accetta spazi, segno,
/// zeri iniziali e dipende dalla cultura). Nessuna eccezione per un valore fuori intervallo.
/// </remarks>
internal static class NumericId
{
    /// <summary>
    /// Vero se <paramref name="value"/> e' un id in forma canonica con valore da 1 a <paramref name="max"/>; in tal
    /// caso <paramref name="number"/> e' il valore, altrimenti 0.
    /// </summary>
    internal static bool TryParse(string? value, long max, out long number)
    {
        number = 0;

        // Vuota, oppure zero iniziale (anche "0" da solo: il valore minimo e' 1).
        if (value is null || value.Length == 0 || value[0] == '0')
        {
            return false;
        }

        long accumulated = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c < '0' || c > '9')
            {
                return false;
            }

            var digit = c - '0';

            // accumulated * 10 + digit <= max se e solo se accumulated <= (max - digit) / 10 (divisione intera).
            if (accumulated > (max - digit) / 10)
            {
                return false;
            }

            accumulated = (accumulated * 10) + digit;
        }

        number = accumulated;
        return true;
    }

    /// <summary>
    /// Restituisce il valore di <paramref name="value"/> se valido. Null e' <see cref="ArgumentNullException"/>,
    /// qualunque altra stringa non valida e' <see cref="ArgumentException"/>; entrambe con <paramref name="paramName"/>.
    /// </summary>
    internal static long Require(string? value, long max, string description, string paramName)
    {
        Guard.NotNull(value, paramName);

        if (!TryParse(value, max, out var number))
        {
            throw new ArgumentException(
                $"Non e' {description} valido: serve un numero intero da 1 a {Format(max)}, scritto solo con le cifre " +
                "da 0 a 9, senza segno, senza zeri iniziali, senza spazi ne' a capo.",
                paramName);
        }

        return number;
    }

    /// <summary>
    /// Restituisce <paramref name="number"/> se e' maggiore di zero; altrimenti lancia
    /// <see cref="ArgumentOutOfRangeException"/> con <paramref name="paramName"/>.
    /// </summary>
    internal static long RequirePositive(long number, string description, string paramName)
    {
        if (number <= 0)
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                number,
                $"Non e' {description} valido: il numero deve essere maggiore di zero.");
        }

        return number;
    }

    /// <summary>La forma canonica di <paramref name="number"/> (cultura invariante); stringa vuota per 0, cioe' per <c>default</c>.</summary>
    internal static string Format(long number) =>
        number == 0 ? string.Empty : number.ToString(CultureInfo.InvariantCulture);
}
