using Filemaster.Domain;

namespace Filemaster.Application;

/// <summary>
/// Le sonde di salute anonime del server: <c>/healthz</c> (il processo risponde) e <c>/readyz</c> (e' pronto: il database
/// e' raggiungibile). Non usano la chiave API, quindi non provano che la chiave sia valida: per quello c'e'
/// <see cref="ITenantInfo"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Un server non pronto e' un risultato, non un'eccezione.</b> Il 503 di <c>/readyz</c> non e' problem+json
/// (<c>{"status":"unavailable","error":"..."}</c>) ed e' l'esito della sonda: arriva come <see cref="HealthProbeResult"/> con
/// <see cref="HealthProbeResult.IsHealthy"/> falso. Per questo il ritentativo automatico delle letture su un 503 non vale
/// per <c>/readyz</c>: quel 503 e' la risposta della sonda e si restituisce subito (se l'adapter dovesse comunque ritentare,
/// a tentativi finiti restituisce l'ultimo esito, mai un'eccezione). Sono eccezioni solo gli errori di trasporto (<see cref="ConnectionException"/>, <see cref="FilemasterTimeoutException"/>:
/// il server non risponde affatto) e una risposta che la sonda non prevede (<see cref="UnexpectedResponseException"/>, o
/// <see cref="ServerErrorException"/> per un 5xx diverso dal 503 di <c>/readyz</c>).
/// </para>
/// <para>Valgono le regole comuni di <see cref="IDocumentStore"/> per l'annullamento.</para>
/// </remarks>
public interface IFilemasterHealth
{
    /// <summary>Interroga <c>GET /healthz</c>: il processo del server e' vivo e risponde (200, corpo <c>ok</c>).</summary>
    /// <param name="cancellationToken">Per annullare la chiamata.</param>
    /// <returns>L'esito della sonda.</returns>
    Task<HealthProbeResult> CheckLivenessAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Interroga <c>GET /readyz</c>: il server e' pronto a servire richieste, cioe' raggiunge il database (200 <c>ready</c>;
    /// 503 <c>unavailable</c> con il motivo in <see cref="HealthProbeResult.Detail"/> se non ci riesce).
    /// </summary>
    /// <param name="cancellationToken">Per annullare la chiamata.</param>
    /// <returns>L'esito della sonda, sano o non pronto.</returns>
    Task<HealthProbeResult> CheckReadinessAsync(CancellationToken cancellationToken = default);
}
