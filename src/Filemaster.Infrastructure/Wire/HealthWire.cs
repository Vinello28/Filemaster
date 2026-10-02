using System.Text;
using Filemaster.Application;
using Filemaster.Domain;

namespace Filemaster.Infrastructure;

/// <summary>
/// Le sonde di salute anonime. <c>/healthz</c> risponde 200 con il testo <c>ok</c> (non JSON); <c>/readyz</c> risponde 200 con
/// <c>{"status":"ready"}</c> oppure 503 con <c>{"status":"unavailable","error":"database non raggiungibile"}</c>, che NON e' problem+json
/// e non e' un errore: e' l'esito della sonda. <see cref="HealthProbeResult.IsHealthy"/> dipende dallo status HTTP (200), mai dal testo.
/// </summary>
internal static class HealthWire
{
    private const int MaxStatusLength = 64;

    /// <summary>
    /// Legge la risposta di <c>/healthz</c>: il corpo, ripulito dagli spazi ai bordi, e' lo stato (<c>ok</c>). Un BOM UTF-8 iniziale si tollera. Un corpo vuoto, troppo
    /// lungo (oltre 64 caratteri), con caratteri di controllo o UTF-8 non valido non e' la risposta di una sonda (per esempio una pagina d'errore
    /// di un proxy con status 200).
    /// </summary>
    /// <param name="body">I byte del corpo.</param>
    /// <param name="context">Lo status e l'id di correlazione della risposta.</param>
    /// <exception cref="UnexpectedResponseException">Il corpo non e' un testo breve e senza caratteri di controllo.</exception>
    internal static HealthProbeResult ReadLiveness(byte[]? body, WireContext context)
    {
        Guard.NotNull(context);
        var bytes = body ?? Array.Empty<byte>();
        var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes, start, bytes.Length - start).Trim();
        }
        catch (ArgumentException exception)
        {
            // DecoderFallbackException e' un ArgumentException: UTF-8 non valido.
            throw context.Unexpected("Risposta non interpretabile: il corpo di /healthz non e' un testo UTF-8 valido.", exception);
        }

        if (text.Length == 0 || text.Length > MaxStatusLength || HasControlCharacter(text))
        {
            throw context.Unexpected("Risposta non interpretabile: il corpo di /healthz non e' un breve testo di stato.");
        }

        return new HealthProbeResult(true, text, null);
    }

    /// <summary>
    /// Legge la risposta di <c>/readyz</c>. Con lo status 200 la sonda e' riuscita; con 503 il server e' su ma non pronto
    /// (<see cref="HealthProbeResult.IsHealthy"/> falso e il motivo in <see cref="HealthProbeResult.Detail"/>). Qualunque altro status, o un
    /// corpo che non ha <c>status</c> come testo (un 503 di un proxy, per esempio), non e' una risposta che la sonda prevede.
    /// </summary>
    /// <param name="body">I byte del corpo.</param>
    /// <param name="context">Lo status (200 o 503) e l'id di correlazione della risposta.</param>
    /// <exception cref="UnexpectedResponseException">Lo status non e' 200 ne' 503, o il corpo non ha la forma attesa.</exception>
    internal static HealthProbeResult ReadReadiness(byte[]? body, WireContext context)
    {
        Guard.NotNull(context);
        if (context.StatusCode != 200 && context.StatusCode != 503)
        {
            throw context.Unexpected("Risposta non interpretabile: /readyz risponde 200 o 503, non " + context.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
        }

        var healthy = context.StatusCode == 200;
        return WireJson.ReadObject(
            body,
            context,
            "sonda",
            probe => new HealthProbeResult(healthy, probe.RequiredString("status"), probe.OptionalString("error")));
    }

    private static bool HasControlCharacter(string text)
    {
        foreach (var c in text)
        {
            if (c < ' ' || c == '\u007F')
            {
                return true;
            }
        }

        return false;
    }
}
