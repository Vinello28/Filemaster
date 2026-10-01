namespace Filemaster.Application;

/// <summary>
/// Esito di una sonda di salute del server (<c>/healthz</c> o <c>/readyz</c>). Un server non pronto non e' un errore del
/// client: e' l'esito della sonda, quindi il 503 di <c>/readyz</c> (<c>{"status":"unavailable","error":"..."}</c>, che non
/// e' problem+json) diventa un risultato con <see cref="IsHealthy"/> falso, non un'eccezione. Restano eccezioni solo gli
/// errori di trasporto (<see cref="Filemaster.Domain.ConnectionException"/>,
/// <see cref="Filemaster.Domain.FilemasterTimeoutException"/>) e le risposte che la sonda non prevede.
/// </summary>
/// <remarks>
/// Il nome evita <c>HealthStatus</c>, che esiste in <c>Microsoft.Extensions.Diagnostics.HealthChecks</c> e darebbe ambiguita'
/// negli host ASP.NET che usano i controlli di salute.
/// </remarks>
public sealed record HealthProbeResult(bool IsHealthy, string Status, string? Detail)
{
    /// <summary>Vero se il server ha risposto 200 alla sonda; falso se ha risposto 503 (<c>/readyz</c>: il server e' su, ma non pronto).</summary>
    public bool IsHealthy { get; } = IsHealthy;

    /// <summary>
    /// Lo stato come lo scrive il server, testo e non enum: <c>ok</c> (corpo di <c>/healthz</c>), <c>ready</c> o
    /// <c>unavailable</c> (campo <c>status</c> di <c>/readyz</c>). Per decidere si usa <see cref="IsHealthy"/>: un valore
    /// che il server aggiungesse in futuro non romperebbe nessuno.
    /// </summary>
    public string Status { get; } = Status;

    /// <summary>
    /// Il perche' di un esito negativo (il campo <c>error</c> di <c>/readyz</c>, per esempio <c>database non
    /// raggiungibile</c>), per le persone e non per decidere; null se la sonda e' riuscita.
    /// </summary>
    public string? Detail { get; } = Detail;
}
