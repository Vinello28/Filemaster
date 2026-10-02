namespace Filemaster.Infrastructure;

/// <summary>
/// Controlli condivisi dai <c>Validate()</c> delle opzioni. Ognuno riceve il nome della proprieta' controllata e lo mette in
/// <see cref="ArgumentException.ParamName"/>: in un <c>Validate()</c> senza parametri un <c>new ArgumentException(messaggio,
/// nameof(Proprieta))</c> scritto a mano farebbe scattare CA2208. I messaggi non riportano mai il valore controllato (la chiave
/// API non deve finire in un'eccezione).
/// </summary>
internal static class OptionChecks
{
    /// <summary>
    /// La durata massima accettata per un timeout o un ritardo: <c>int.MaxValue</c> millisecondi (circa 24 giorni e 20 ore).
    /// Oltre, <c>Task.Delay</c> e i timer lanciano al momento della chiamata invece che alla validazione.
    /// </summary>
    internal static readonly TimeSpan MaxDuration = TimeSpan.FromMilliseconds(int.MaxValue);

    /// <summary>Rifiuta un valore obbligatorio mancante.</summary>
    internal static void Required(bool present, string paramName, string what)
    {
        if (!present)
        {
            throw new ArgumentException($"{what} e' obbligatorio.", paramName);
        }
    }

    /// <summary>Rifiuta una condizione non soddisfatta, con un messaggio fisso (mai il valore).</summary>
    internal static void Holds(bool condition, string paramName, string message)
    {
        if (!condition)
        {
            throw new ArgumentException(message, paramName);
        }
    }

    /// <summary>
    /// Rifiuta un timeout che non e' positivo o supera <see cref="MaxDuration"/>; <see cref="Timeout.InfiniteTimeSpan"/> passa
    /// solo se <paramref name="allowInfinite"/> e' vero.
    /// </summary>
    internal static void TimeoutValue(TimeSpan value, bool allowInfinite, string paramName)
    {
        if (value == Timeout.InfiniteTimeSpan)
        {
            if (!allowInfinite)
            {
                throw new ArgumentOutOfRangeException(paramName, value, "Il valore non puo' essere infinito: serve una durata positiva.");
            }

            return;
        }

        if (value <= TimeSpan.Zero || value > MaxDuration)
        {
            throw new ArgumentOutOfRangeException(paramName, value, "La durata deve essere positiva e al massimo int.MaxValue millisecondi.");
        }
    }

    /// <summary>Rifiuta un ritardo negativo, infinito o oltre <see cref="MaxDuration"/> (zero e' ammesso: ritentativo immediato).</summary>
    internal static void Delay(TimeSpan value, string paramName)
    {
        if (value < TimeSpan.Zero || value > MaxDuration)
        {
            throw new ArgumentOutOfRangeException(paramName, value, "Il ritardo deve essere compreso fra zero e int.MaxValue millisecondi.");
        }
    }
}
