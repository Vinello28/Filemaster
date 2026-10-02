using Filemaster.Application;

namespace Filemaster.Infrastructure;

/// <summary>
/// L'intestazione <c>Content-Range</c> di una risposta 206: <c>bytes 0-9/590</c> e' il primo byte 0, l'ultimo 9 e la dimensione totale 590. Si
/// legge a mano e in modo rigido, a differenza di <c>ContentRangeHeaderValue</c>, che accetta anche le forme senza intervallo (<c>*/590</c>,
/// quella di un 416) e senza totale (<c>0-9/*</c>) che un <see cref="ContentRange"/> non puo' rappresentare.
/// </summary>
internal static class ContentRangeHeader
{
    private const string Unit = "bytes";

    /// <summary>
    /// Legge <c>bytes primo-ultimo/totale</c>. Falso, senza lanciare, se: l'unita' non e' <c>bytes</c> (senza distinguere maiuscole); manca lo
    /// spazio dopo l'unita'; una parte non e' fatta di sole cifre ASCII (niente segno, spazi o cifre Unicode); un numero non sta in un
    /// <see cref="long"/>; la forma e' <c>*/totale</c> (416) o <c>primo-ultimo/*</c> (totale ignoto); l'ultimo byte precede il primo; oppure l'ultimo
    /// byte non e' prima del totale (<c>bytes 0-590/590</c>). Gli spazi ai bordi dell'intero valore si tollerano.
    /// </summary>
    /// <param name="value">Il valore dell'intestazione.</param>
    /// <param name="range">L'intervallo, o null se il valore non e' valido.</param>
    internal static bool TryParse(string? value, out ContentRange? range)
    {
        range = null;
        if (value is null)
        {
            return false;
        }

        var text = value.Trim();
        if (text.Length <= Unit.Length + 1
            || string.Compare(text, 0, Unit, 0, Unit.Length, StringComparison.OrdinalIgnoreCase) != 0
            || text[Unit.Length] != ' ')
        {
            return false;
        }

        var dash = text.IndexOf('-', Unit.Length + 1);
        var slash = text.IndexOf('/', Unit.Length + 1);
        if (dash < 0 || slash < dash)
        {
            return false;
        }

        if (!TryNumber(text, Unit.Length + 1, dash, out var first)
            || !TryNumber(text, dash + 1, slash, out var last)
            || !TryNumber(text, slash + 1, text.Length, out var total)
            || last < first
            || last >= total)
        {
            return false;
        }

        range = new ContentRange(first, last, total);
        return true;
    }

    // Solo cifre ASCII da start (incluso) a end (escluso), almeno una; falso se non entra in un long.
    private static bool TryNumber(string text, int start, int end, out long number)
    {
        number = 0;
        if (end <= start)
        {
            return false;
        }

        for (var i = start; i < end; i++)
        {
            var c = text[i];
            if (c < '0' || c > '9')
            {
                return false;
            }

            var digit = c - '0';
            if (number > (long.MaxValue - digit) / 10)
            {
                return false;
            }

            number = (number * 10) + digit;
        }

        return true;
    }
}
