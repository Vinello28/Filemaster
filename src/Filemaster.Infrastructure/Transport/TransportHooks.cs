namespace Filemaster.Infrastructure;

/// <summary>
/// I punti dove i test sostituiscono il mondo reale: l'attesa fra due tentativi, il generatore del jitter e il tetto di byte per le
/// risposte lette in memoria. Tutto e' opzionale: senza, valgono <c>Task.Delay</c>, un <see cref="Random"/> condiviso (con un lock:
/// non e' thread-safe e il trasporto e' condiviso) e 32 MiB.
/// </summary>
internal sealed class TransportHooks
{
    /// <summary>Come si aspetta fra due tentativi: riceve l'attesa e il token della scadenza della chiamata.</summary>
    internal Func<TimeSpan, CancellationToken, Task>? Delay { get; set; }

    /// <summary>Il numero casuale del jitter, in [0, 1).</summary>
    internal Func<double>? NextJitter { get; set; }

    /// <summary>Quanti byte al massimo di una risposta si leggono in memoria; oltre e' <c>UnexpectedResponseException</c>.</summary>
    internal int? MaxBufferedBytes { get; set; }
}
