namespace Filemaster.Infrastructure;

/// <summary>
/// Una scadenza per una chiamata: un <see cref="CancellationToken"/> che scatta quando passa il tempo concesso o quando scatta il
/// token di chi chiama, e un modo per sapere <b>quale dei due</b> e' successo (<see cref="Expired"/>). Il tempo e' misurato da un
/// <see cref="TimeProvider"/> (cosi' i test lo comandano con un orologio finto), non da <c>CancellationTokenSource.CancelAfter</c>,
/// che usa l'orologio vero. Si puo' riarmare (<see cref="Restart"/>): un download usa una scadenza per arrivare alle intestazioni e
/// la riarma con il tempo del trasferimento quando le ha.
/// </summary>
/// <remarks>
/// Chi decide fra "annullamento" e "tempo scaduto" guarda prima il token di chi chiama (<c>IsCancellationRequested</c>) e solo poi
/// <see cref="Expired"/>: se i due scattano insieme vince l'annullamento. Non si distingue mai dal tipo dell'eccezione, che dipende
/// dal gestore HTTP e dal runtime. Lo smaltimento e' idempotente e ferma il timer.
/// </remarks>
internal sealed class Deadline : IDisposable
{
    private readonly CancellationTokenSource _source;
    private readonly ITimer _timer;
    private int _expired;
    private int _disposed;

    /// <summary>Crea la scadenza e la arma subito.</summary>
    /// <param name="timeProvider">L'orologio.</param>
    /// <param name="timeout">Il tempo concesso (gia' validato: positivo e al massimo <c>int.MaxValue</c> millisecondi), oppure <see cref="Timeout.InfiniteTimeSpan"/> per non scadere mai.</param>
    /// <param name="caller">Il token di chi chiama: se scatta, scatta anche <see cref="Token"/>, ma <see cref="Expired"/> resta falso.</param>
    internal Deadline(TimeProvider timeProvider, TimeSpan timeout, CancellationToken caller)
    {
        _source = CancellationTokenSource.CreateLinkedTokenSource(caller);
        _timer = timeProvider.CreateTimer(static state => ((Deadline)state!).Expire(), this, timeout, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Il token che scatta alla scadenza o all'annullamento di chi chiama. Da non leggere dopo lo smaltimento.</summary>
    internal CancellationToken Token => _source.Token;

    /// <summary>Vero se il <b>tempo</b> e' scaduto (non se ha annullato chi chiama).</summary>
    internal bool Expired => Volatile.Read(ref _expired) != 0;

    /// <summary>Vero dopo lo smaltimento.</summary>
    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Riarma il timer con un nuovo tempo, a partire da adesso (il tempo gia' passato non conta). Dopo lo smaltimento non fa nulla.</summary>
    /// <param name="timeout">Il nuovo tempo, o <see cref="Timeout.InfiniteTimeSpan"/> per non scadere piu'.</param>
    internal void Restart(TimeSpan timeout)
    {
        if (!IsDisposed)
        {
            _timer.Change(timeout, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Ferma il timer e rilascia il token. Idempotente.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _timer.Dispose();
            _source.Dispose();
        }
    }

    // Gira sul thread del timer: un'eccezione qui terminerebbe il processo, quindi si inghiottono quelle possibili (lo smaltimento in
    // corso, o un callback di chi si e' registrato sul token che lancia).
    private void Expire()
    {
        Volatile.Write(ref _expired, 1);
        try
        {
            _source.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (AggregateException)
        {
        }
    }
}
