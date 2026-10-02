namespace Filemaster.Infrastructure;

/// <summary>
/// Come e quanto il client ritenta da solo le <b>letture</b> (<c>GET</c>, comprese le sonde <c>/healthz</c> e <c>/readyz</c>) che
/// falliscono per un motivo transitorio. E' un tipo di input: classe con proprieta' <c>get; set;</c> (nessun <c>init</c>, quindi
/// si usa anche da C# 7.3) e <see cref="Validate"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cosa si ritenta.</b> Un errore di connessione (rete, DNS, connessione interrotta), un 408, un 429 (rispettando
/// <c>Retry-After</c>, in secondi o come data) e un 502, 503 o 504 che <b>non</b> sia problem+json (cioe' un 5xx di un proxy o
/// di un bilanciatore; il 503 <c>storage-not-configured</c> del server non e' transitorio). Il tempo scaduto del client non si
/// ritenta.
/// </para>
/// <para>
/// <b>Cosa non si ritenta mai</b>: le scritture e le verifiche (<c>POST</c>, <c>PATCH</c>, <c>DELETE</c>: dopo un errore di rete
/// l'esito puo' essere ignoto, e ritentare una cancellazione riuscita darebbe un 404 fuorviante), il 500, il 409 e ogni 4xx
/// salvo il 408 e il 429, il 503 di <c>/readyz</c> (e' l'esito della sonda), e qualunque lettura dopo che la risposta e' stata
/// consegnata a chi chiama (uno stream gia' restituito non si ritenta).
/// </para>
/// <para>
/// <b>Attese.</b> Fra un tentativo e il successivo si aspetta un tempo che raddoppia a ogni tentativo a partire da
/// <see cref="InitialDelay"/>, senza superare <see cref="MaxDelay"/>, con un jitter: meta' del tempo fissa piu' una parte casuale
/// fra zero e l'altra meta'. Se il server indica <c>Retry-After</c> si aspetta almeno quel tempo, ma mai piu' di
/// <see cref="MaxDelay"/>. Il tempo di attesa fa parte del tempo totale concesso da <see cref="FilemasterOptions.RequestTimeout"/>.
/// </para>
/// </remarks>
public sealed class FilemasterRetryOptions
{
    /// <summary>Il numero massimo di tentativi che <see cref="MaxAttempts"/> puo' avere (il primo e' compreso).</summary>
    public const int MaxAllowedAttempts = 10;

    /// <summary>
    /// Quanti tentativi al massimo per ogni lettura, il primo compreso: <c>1</c> spegne il ritentativo, <c>3</c> (il default) vuol
    /// dire un tentativo piu' due ritentativi. Da 1 a <see cref="MaxAllowedAttempts"/>.
    /// </summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>
    /// L'attesa prima del primo ritentativo (poi raddoppia a ogni tentativo, vedi le note del tipo); default 500 ms. Non
    /// negativa, al massimo <see cref="MaxDelay"/>. Zero vale ritentativo immediato.
    /// </summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// L'attesa massima fra due tentativi, anche quando il server chiede di piu' con <c>Retry-After</c>; default 10 secondi.
    /// Non minore di <see cref="InitialDelay"/>, al massimo <c>int.MaxValue</c> millisecondi.
    /// </summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Controlla i valori; lancia alla prima violazione, nell'ordine delle proprieta'.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="MaxAttempts"/> e' fuori da 1..<see cref="MaxAllowedAttempts"/>, un'attesa e' negativa o troppo lunga, oppure
    /// <see cref="MaxDelay"/> e' minore di <see cref="InitialDelay"/>. <see cref="ArgumentException.ParamName"/> e' il nome della
    /// proprieta'.
    /// </exception>
    public void Validate()
    {
        if (MaxAttempts < 1 || MaxAttempts > MaxAllowedAttempts)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxAttempts),
                MaxAttempts,
                $"I tentativi devono essere da 1 a {MaxAllowedAttempts} (1 spegne il ritentativo).");
        }

        OptionChecks.Delay(InitialDelay, nameof(InitialDelay));
        OptionChecks.Delay(MaxDelay, nameof(MaxDelay));
        if (MaxDelay < InitialDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxDelay), MaxDelay, "L'attesa massima non puo' essere minore di quella iniziale.");
        }
    }

    /// <summary>Una riga leggibile con i tre valori.</summary>
    /// <returns>Il testo, per esempio <c>FilemasterRetryOptions { MaxAttempts = 3, InitialDelay = 00:00:00.5000000, MaxDelay = 00:00:10 }</c>.</returns>
    public override string ToString() =>
        FormattableString.Invariant($"FilemasterRetryOptions {{ MaxAttempts = {MaxAttempts}, InitialDelay = {InitialDelay}, MaxDelay = {MaxDelay} }}");
}
