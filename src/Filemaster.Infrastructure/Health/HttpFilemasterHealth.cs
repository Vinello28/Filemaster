using Filemaster.Application;
using Filemaster.Domain;

namespace Filemaster.Infrastructure;

/// <summary>
/// L'adapter HTTP di <see cref="IFilemasterHealth"/>: <c>GET healthz</c> e <c>GET readyz</c>, le sonde anonime del server, lette con
/// <see cref="HealthWire"/>. Sono <c>GET</c>: il trasporto le ritenta sugli errori di rete e sui 502/503/504 di un proxy, ma mai sul
/// 503 di <c>/readyz</c>, che e' uno status atteso (l'esito della sonda).
/// </summary>
/// <remarks>
/// <para>
/// <b>Senza chiave API</b> (<see cref="TransportRequest.OmitApiKey"/>). Non e' solo coerenza con la porta ("non usano la chiave API"):
/// il server registra un solo schema di autenticazione, che quindi e' quello di default e gira su OGNI richiesta, anche verso le rotte
/// anonime; se la richiesta porta una chiave, il gestore la cerca nel database (e a volte aggiorna <c>last_used_at</c>). Con il database
/// irraggiungibile quella ricerca fallisce prima di arrivare a <c>/readyz</c>, che risponderebbe 500 (o dopo il timeout di connessione
/// del database) invece del suo 503: la sonda non direbbe piu' "non pronto". Senza chiave il gestore non fa nulla. Le catture 01, 02 e
/// 224 sono state fatte senza chiave.
/// </para>
/// <para>
/// <b><c>/healthz</c></b>: solo il 200 e' l'esito (corpo di testo <c>ok</c>, <c>Accept: text/plain</c>); un altro 2xx o un corpo che non
/// e' un breve testo di stato e' <see cref="UnexpectedResponseException"/>, un 5xx (dopo i tentativi) <see cref="ServerErrorException"/>.
/// </para>
/// <para>
/// <b><c>/readyz</c></b>: 200 e 503 sono esiti (<c>Accept: application/json</c>). Un 200 o un 503 con il corpo della sonda
/// (<c>{"status":"...","error":"..."?}</c>) diventa un <see cref="HealthProbeResult"/> (sano solo con 200), mai un'eccezione. Un 200 con
/// un altro corpo e' una risposta che la sonda non prevede: <see cref="UnexpectedResponseException"/> con status 200. Un 503 con un
/// altro corpo (la pagina di un proxy, un problem+json) non e' "il 503 di <c>/readyz</c>" ma un errore del server o di chi sta in mezzo:
/// si mappa come ogni altra risposta d'errore (<see cref="ProblemMapper"/>, di norma <see cref="ServerErrorException"/>) e, come il 503
/// della sonda, non si ritenta. Ogni altro status (un 500, un 502 o un 504 dopo i tentativi, un altro 2xx) passa da
/// <see cref="ProblemMapper"/> nel trasporto.
/// </para>
/// </remarks>
internal sealed class HttpFilemasterHealth : IFilemasterHealth
{
    private const string TextPlain = "text/plain";
    private const int NotReady = 503;

    private readonly FilemasterTransport _transport;

    /// <summary>Crea l'adapter sul trasporto indicato (condivisibile con gli altri adapter).</summary>
    /// <param name="transport">Il trasporto.</param>
    /// <exception cref="ArgumentNullException"><paramref name="transport"/> e' null.</exception>
    internal HttpFilemasterHealth(FilemasterTransport transport)
    {
        Guard.NotNull(transport);
        _transport = transport;
    }

    /// <inheritdoc />
    public async Task<HealthProbeResult> CheckLivenessAsync(CancellationToken cancellationToken = default)
    {
        var request = new TransportRequest(HttpMethod.Get, Routes.Healthz)
        {
            OmitApiKey = true,
            IsExpectedStatus = IsLive,
            Customize = message => message.Headers.TryAddWithoutValidation("Accept", TextPlain),
        };
        var response = await _transport.SendBufferedAsync(request, cancellationToken).ConfigureAwait(false);
        return HealthWire.ReadLiveness(response.Body, WireContext.Of(response));
    }

    /// <inheritdoc />
    public async Task<HealthProbeResult> CheckReadinessAsync(CancellationToken cancellationToken = default)
    {
        var request = new TransportRequest(HttpMethod.Get, Routes.Readyz)
        {
            OmitApiKey = true,
            IsExpectedStatus = IsProbeOutcome,
            Customize = JsonCalls.AcceptJson,
        };
        var response = await _transport.SendBufferedAsync(request, cancellationToken).ConfigureAwait(false);
        try
        {
            return HealthWire.ReadReadiness(response.Body, WireContext.Of(response));
        }
        catch (UnexpectedResponseException) when (response.StatusCode == NotReady)
        {
            throw ProblemMapper.Map(response.StatusCode, response.Body, response.RequestId);
        }
    }

    private static bool IsLive(int status) => status == 200;

    private static bool IsProbeOutcome(int status) => status == 200 || status == NotReady;
}
