using System.Diagnostics.CodeAnalysis;

namespace Filemaster.Domain;

/// <summary>
/// Codice scelto dall'utente che identifica una cartella dentro l'ente: da 1 a <see cref="MaxLength"/> caratteri, il
/// primo alfanumerico ASCII (<c>A-Z</c>, <c>a-z</c>, <c>0-9</c>), poi alfanumerici ASCII, <c>_</c>, <c>.</c> e <c>-</c>.
/// Sicuro in un URL e in un percorso. Il confronto e' esatto: maiuscole e minuscole sono codici diversi.
/// </summary>
/// <remarks>
/// <para>
/// Il client e' piu' stretto del server su un punto, volutamente: nessun trim e nessun a capo. Il server trimma gli
/// input e accetta un newline finale (il <c>$</c> della sua regex combacia prima di un <c>\n</c> in coda); qui uno
/// spazio o un a capo, anche solo in fondo, rende il codice non valido, cosi' un valore sbagliato non viene
/// "aggiustato" in silenzio. Ogni altro input ha lo stesso esito del server (verificato su tutto l'intervallo BMP).
/// </para>
/// <para>
/// Le cartelle restano identificate da un codice scelto dall'utente (a differenza di documenti, contatti ed enti, che
/// hanno id numerici). Un vecchio id di cartella <c>fld_</c> + ULID (30 caratteri) e' un codice valido, senza
/// trattamenti speciali.
/// </para>
/// <para>
/// <c>default(FolderCode)</c> e' il codice vuoto: <see cref="Value"/> e' la stringa vuota (mai null),
/// <see cref="IsEmpty"/> e' vero, <c>ToString()</c>, <c>Equals</c> e <c>GetHashCode</c> non lanciano. Non e' un
/// codice valido: non si puo' ottenere da <c>Parse</c> o dal costruttore. Nessuna conversione implicita da o verso
/// <see cref="string"/>.
/// </para>
/// </remarks>
public readonly record struct FolderCode
{
    /// <summary>Lunghezza massima di un codice, in caratteri.</summary>
    public const int MaxLength = 50;

    private readonly string? _value;

    /// <summary>Crea il codice da una stringa valida.</summary>
    /// <param name="value">Il codice, per esempio <c>FATTURE</c> o <c>2026.09-fatture_v2</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> e' null.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> non e' un codice di cartella valido.</exception>
    public FolderCode(string value)
    {
        Guard.NotNull(value);

        if (!IsValid(value))
        {
            throw new ArgumentException(
                $"Non e' un codice di cartella valido: da 1 a {MaxLength} caratteri fra lettere e cifre ASCII, " +
                "'_', '.' e '-', il primo alfanumerico, senza spazi ne' a capo.",
                nameof(value));
        }

        _value = value;
    }

    /// <summary>Il codice; stringa vuota per <c>default</c>.</summary>
    public string Value => _value ?? string.Empty;

    /// <summary>Vero per <c>default(FolderCode)</c>, il codice vuoto.</summary>
    public bool IsEmpty => _value is null;

    /// <summary>Vero se <paramref name="value"/> e' un codice di cartella valido (nessun trim).</summary>
    /// <param name="value">La stringa da controllare; null e' semplicemente non valido.</param>
    public static bool IsValid([NotNullWhen(true)] string? value)
    {
        if (value is null || value.Length == 0 || value.Length > MaxLength || !IsAsciiAlphanumeric(value[0]))
        {
            return false;
        }

        for (var i = 1; i < value.Length; i++)
        {
            var c = value[i];
            if (!IsAsciiAlphanumeric(c) && c != '_' && c != '.' && c != '-')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Prova a interpretare <paramref name="value"/>; non lancia mai.</summary>
    /// <param name="value">La stringa da interpretare.</param>
    /// <param name="code">Il codice, o <c>default</c> se <paramref name="value"/> non e' valido.</param>
    /// <returns>Vero se <paramref name="value"/> e' un codice di cartella valido.</returns>
    public static bool TryParse(string? value, out FolderCode code)
    {
        if (IsValid(value))
        {
            code = new FolderCode(value);
            return true;
        }

        code = default;
        return false;
    }

    /// <summary>Interpreta <paramref name="value"/> come codice di cartella.</summary>
    /// <param name="value">Il codice, per esempio <c>FATTURE</c>.</param>
    /// <returns>Il codice.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> e' null.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> non e' un codice di cartella valido.</exception>
    public static FolderCode Parse(string value) => new(value);

    /// <summary>Il codice (stringa vuota per <c>default</c>).</summary>
    /// <returns><see cref="Value"/>.</returns>
    public override string ToString() => Value;

    // Solo ASCII, mai char.IsLetterOrDigit: accetterebbe cifre e lettere Unicode.
    private static bool IsAsciiAlphanumeric(char c) =>
        (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
}
