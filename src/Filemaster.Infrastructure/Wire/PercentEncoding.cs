namespace Filemaster.Infrastructure;

/// <summary>
/// La codifica percent dei valori della query string: <see cref="Uri.EscapeDataString(string)"/> (tutto tranne le lettere e le cifre ASCII e
/// <c>-._~</c>, i caratteri non ASCII come UTF-8 (la e accentata diventa <c>%C3%A9</c>), uno spazio <c>%20</c> e mai <c>+</c>). Mai
/// <c>HttpUtility</c> (non esiste su netstandard2.0). Due difese attorno alla chiamata del framework:
/// <list type="bullet">
/// <item><description>Un surrogato isolato non e' testo UTF-16 valido: <c>EscapeDataString</c> lo sostituirebbe in silenzio con U+FFFD (misurato su .NET 10), cioe' il server riceverebbe un valore diverso da quello dell'utente; su .NET Framework lancia invece <c>UriFormatException</c>, senza il nome del parametro (misurato sulla CI net48). Si rifiuta con <see cref="ArgumentException"/>, prima di toccare la rete.</description></item>
/// <item><description>Su .NET Framework una stringa molto lunga (oltre 65.519 caratteri) fa lanciare <c>UriFormatException</c> a <c>EscapeDataString</c> (non verificato in locale: solo net48 su Windows). Il valore si codifica a pezzi di 16.384 caratteri, senza mai spezzare una coppia surrogata, e il risultato e' identico.</description></item>
/// </list>
/// </summary>
internal static class PercentEncoding
{
    private const int ChunkLength = 16 * 1024;

    /// <summary>Codifica un valore per la query string (vedi il tipo).</summary>
    /// <param name="value">Il valore, anche vuoto.</param>
    /// <param name="paramName">Il nome da mettere nell'eccezione se il valore ha un surrogato isolato.</param>
    /// <exception cref="ArgumentException"><paramref name="value"/> contiene un surrogato isolato (non e' UTF-16 valido).</exception>
    internal static string Encode(string value, string paramName)
    {
        Guard.NotNull(value);
        RequireWellFormed(value, paramName);
        if (value.Length <= ChunkLength)
        {
            return Uri.EscapeDataString(value);
        }

        var builder = new System.Text.StringBuilder(value.Length + (value.Length / 2));
        var start = 0;
        while (start < value.Length)
        {
            var end = Math.Min(start + ChunkLength, value.Length);
            if (end < value.Length && char.IsHighSurrogate(value[end - 1]))
            {
                end--; // la coppia surrogata passa intera al pezzo dopo
            }

            // CA1846 suggerirebbe l'overload su span, che esiste solo dai runtime recenti: qui serve anche netstandard2.0.
#pragma warning disable CA1846
            builder.Append(Uri.EscapeDataString(value.Substring(start, end - start)));
#pragma warning restore CA1846
            start = end;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Rifiuta un testo con un surrogato isolato (alto senza basso, o basso senza alto): non si puo' scrivere in UTF-8 senza cambiarlo.
    /// Vale per ogni testo che parte verso il server (query, corpo JSON, campi multipart, nome file).
    /// </summary>
    /// <param name="value">Il testo.</param>
    /// <param name="paramName">Il nome da mettere nell'eccezione.</param>
    /// <exception cref="ArgumentException"><paramref name="value"/> contiene un surrogato isolato.</exception>
    internal static void RequireWellFormed(string value, string paramName)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(c))
            {
                throw new ArgumentException(
                    "Il testo contiene un surrogato isolato (UTF-16 non valido): non si puo' spedire in UTF-8 senza cambiarlo.",
                    paramName);
            }
        }
    }
}
