namespace Filemaster.Domain;

/// <summary>
/// Validazione condivisa degli id tipizzati (<see cref="DocumentId"/>, <see cref="ContactId"/>, <see cref="TenantId"/>):
/// un prefisso scelto dal tipo seguito da un ULID in forma canonica. Aggiungere un tipo di id e' un nuovo struct
/// che passa il proprio prefisso a questo helper.
/// </summary>
/// <remarks>
/// La regola e' quella del server (<c>Ids.IsValid</c> di Sharp-a-File: <c>Ulid.TryParse</c> e confronto con la forma
/// canonica), verificata su tutto l'intervallo BMP in ogni posizione: 26 caratteri Crockford base32 maiuscoli
/// (<c>0-9</c> e <c>A-Z</c> senza I, L, O, U) e primo carattere solo da <c>0</c> a <c>7</c>. Un ULID ha 128 bit ma 26
/// cifre base32 ne portano 130: il primo carattere codifica 3 bit soli, quindi <c>8</c> o oltre e' un overflow.
/// La libreria ULID del server (Ulid 1.4.1) da sola e' permissiva: <c>TryParse</c> riesce anche con il minuscolo, con
/// I/L/O/U (decodificati a caso) e con l'overflow (che tronca: <c>8000...</c> diventa <c>0000...</c>); a rifiutarli
/// e' il confronto con <c>ToString()</c>, cioe' la forma canonica, ed e' quello che questo helper riproduce. Il
/// controllo e' un ciclo con confronti ASCII espliciti: mai una regex (il suo <c>$</c> accetta un newline finale) e
/// mai <c>char.IsDigit</c>/<c>char.IsLetter</c> (accettano cifre e lettere Unicode).
/// </remarks>
internal static class PrefixedId
{
    /// <summary>Lunghezza del ULID dopo il prefisso.</summary>
    internal const int UlidLength = 26;

    /// <summary>Vero se <paramref name="value"/> e' <paramref name="prefix"/> seguito da un ULID canonico.</summary>
    internal static bool IsValid(string? value, string prefix)
    {
        if (value is null
            || value.Length != prefix.Length + UlidLength
            || !value.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        // Il primo carattere porta solo 3 bit: oltre '7' il ULID non sta nei 128 bit.
        var first = value[prefix.Length];
        if (first < '0' || first > '7')
        {
            return false;
        }

        for (var i = prefix.Length + 1; i < value.Length; i++)
        {
            if (!IsCrockfordUpper(value[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Restituisce <paramref name="value"/> se valido. Null e' <see cref="ArgumentNullException"/>, qualunque altra
    /// stringa non valida e' <see cref="ArgumentException"/>; entrambe con <paramref name="paramName"/>.
    /// </summary>
    internal static string Require(string? value, string prefix, string description, string paramName)
    {
        Guard.NotNull(value, paramName);

        if (!IsValid(value, prefix))
        {
            throw new ArgumentException(
                $"Non e' {description} valido: serve '{prefix}' seguito da {UlidLength} caratteri maiuscoli " +
                "(cifre e lettere senza I, L, O, U; il primo carattere da 0 a 7), senza spazi ne' a capo.",
                paramName);
        }

        return value;
    }

    private static bool IsCrockfordUpper(char c) =>
        (c >= '0' && c <= '9')
        || (c >= 'A' && c <= 'Z' && c != 'I' && c != 'L' && c != 'O' && c != 'U');
}
