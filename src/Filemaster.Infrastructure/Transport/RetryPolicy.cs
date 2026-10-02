using System.Net.Http.Headers;

namespace Filemaster.Infrastructure;

/// <summary>
/// Le regole di ritentativo, come funzioni pure (si provano senza rete): quali risposte sono transitorie, quanto si aspetta fra due
/// tentativi e come si legge <c>Retry-After</c>. Chi le applica (il trasporto) decide a monte che si ritenta solo un <c>GET</c>, solo
/// prima di consegnare la risposta a chi chiama, e mai uno status dichiarato atteso dalla richiesta.
/// </summary>
internal static class RetryPolicy
{
    private const string ProblemJsonMediaType = "application/problem+json";

    /// <summary>
    /// Vero per gli status transitori: 408 e 429 (sempre) e 502, 503 e 504 <b>che non sono problem+json</b>. Un 502/503/504 di un
    /// proxy o di un bilanciatore e' transitorio; un 503 problem+json e' il server stesso (<c>storage-not-configured</c>: lo stato di
    /// configurazione non cambia da solo). Il 500, il 409 e ogni altro 4xx non si ritentano mai.
    /// </summary>
    /// <param name="statusCode">Lo status della risposta.</param>
    /// <param name="mediaType">Il tipo di contenuto della risposta, senza parametri; null se manca.</param>
    internal static bool IsRetryableStatus(int statusCode, string? mediaType) =>
        statusCode == 408
        || statusCode == 429
        || ((statusCode == 502 || statusCode == 503 || statusCode == 504) && !IsProblemJson(mediaType));

    /// <summary>Vero se il tipo di contenuto e' <c>application/problem+json</c> (senza distinzione fra maiuscole e minuscole).</summary>
    internal static bool IsProblemJson(string? mediaType) =>
        string.Equals(mediaType, ProblemJsonMediaType, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Quanto aspettare prima del prossimo tentativo. Il backoff e' esponenziale: <c>InitialDelay * 2^(n-1)</c> dopo l'n-esimo
    /// tentativo fallito, senza superare <paramref name="maxDelay"/>; poi un jitter "equal": meta' fissa piu' una parte casuale fra
    /// zero e l'altra meta' (<c>base/2 + base/2 * jitter</c>). Se il server ha chiesto un'attesa (<paramref name="retryAfter"/>) si
    /// aspetta almeno quella, ma mai piu' di <paramref name="maxDelay"/>: il risultato e' il maggiore fra il backoff con jitter e il
    /// <c>Retry-After</c> limitato.
    /// </summary>
    /// <param name="initialDelay">L'attesa dopo il primo tentativo.</param>
    /// <param name="maxDelay">Il tetto, anche per <c>Retry-After</c>.</param>
    /// <param name="failedAttempt">Quale tentativo e' appena fallito (1 per il primo).</param>
    /// <param name="jitter">Un numero casuale in [0, 1); fuori da quell'intervallo si porta ai suoi estremi.</param>
    /// <param name="retryAfter">L'attesa chiesta dal server, se c'e'.</param>
    internal static TimeSpan ComputeDelay(TimeSpan initialDelay, TimeSpan maxDelay, int failedAttempt, double jitter, TimeSpan? retryAfter)
    {
        // Calcolo in tick con double e con tetto prima della conversione: TimeSpan * double non esiste su netstandard2.0 e
        // 2^9 volte un'attesa lunga trabocca un long.
        var doubled = initialDelay.Ticks * Math.Pow(2, Math.Max(0, failedAttempt - 1));
        var baseTicks = Math.Min((double)maxDelay.Ticks, doubled);
        var clamped = Math.Min(1.0, Math.Max(0.0, double.IsNaN(jitter) ? 0.0 : jitter));
        var delay = TimeSpan.FromTicks((long)((baseTicks / 2) + ((baseTicks / 2) * clamped)));

        if (retryAfter is { } requested)
        {
            var capped = requested > maxDelay ? maxDelay : requested;
            if (capped > delay)
            {
                delay = capped;
            }
        }

        return delay;
    }

    /// <summary>
    /// L'attesa chiesta da <c>Retry-After</c>, in secondi (<c>Delta</c>) o come data HTTP (<c>Date</c>, relativa a
    /// <paramref name="now"/>); mai negativa (una data passata e' zero). Null se l'intestazione manca o non e' interpretabile
    /// (<c>HttpClient</c> la lascia nulla).
    /// </summary>
    /// <param name="retryAfter">L'intestazione gia' interpretata da <c>HttpClient</c>.</param>
    /// <param name="now">L'ora corrente, dal <see cref="TimeProvider"/>.</param>
    internal static TimeSpan? ReadRetryAfter(RetryConditionHeaderValue? retryAfter, DateTimeOffset now)
    {
        if (retryAfter is null)
        {
            return null;
        }

        if (retryAfter.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (retryAfter.Date is { } date)
        {
            var wait = date - now;
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        return null;
    }
}
