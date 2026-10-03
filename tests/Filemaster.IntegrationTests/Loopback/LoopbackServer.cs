using System.Net;
using System.Net.Sockets;

namespace Filemaster.IntegrationTests.Loopback;

/// <summary>Cosa fa il server con una richiesta (riga e intestazioni gia' lette): legge il corpo, risponde, tronca, resetta.</summary>
/// <param name="exchange">La richiesta e la connessione su cui rispondere.</param>
/// <returns>Il task finito quando la risposta e' data (o la connessione chiusa).</returns>
internal delegate Task LoopbackHandler(LoopbackExchange exchange);

/// <summary>
/// Un server HTTP/1.1 minimale su <see cref="TcpListener"/> (<c>127.0.0.1</c>, porta scelta dal sistema), scritto a mano perche' i test
/// provino il gestore HTTP VERO del client su una connessione TCP vera, compreso cio' che un server serio non fa (troncare, resettare,
/// non rispondere). Gira uguale su .NET e .NET Framework. Registra ogni richiesta appena ne ha letto le intestazioni
/// (<see cref="Requests"/>); il corpo lo legge il gestore della prova con <see cref="LoopbackExchange.ReadBodyAsync"/>. Le
/// connessioni restano aperte fra una richiesta e l'altra (keep-alive) solo se il corpo e' stato letto e la risposta non chiude.
/// </summary>
/// <remarks>
/// Nessuna attesa e' senza limite (lezione 34): <see cref="WaitForRequestsAsync"/> e le attese di <see cref="LoopbackExchange"/>
/// finiscono dopo <see cref="Limit"/>, e <see cref="Dispose"/> chiude il listener e tutte le connessioni, cosi' ogni lettura o
/// scrittura in corso fallisce subito.
/// </remarks>
internal sealed class LoopbackServer : IDisposable
{
    /// <summary>Il tetto di ogni attesa del server e degli helper: oltre, la prova fallisce invece di restare appesa.</summary>
    internal static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    private readonly TcpListener _listener;
    private readonly LoopbackHandler _handler;
    private readonly int _keepBodyBytes;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly List<RecordedRequest> _requests = new();
    private readonly List<TcpClient> _clients = new();
    private readonly List<Task> _connections = new();
    private readonly List<Exception> _handlerErrors = new();
    private readonly Task _acceptLoop;
    private TaskCompletionSource<bool> _changed = NewSignal();
    private int _connectionCount;

    private LoopbackServer(LoopbackHandler handler, int keepBodyBytes)
    {
        _handler = handler;
        _keepBodyBytes = keepBodyBytes;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>La porta su cui ascolta.</summary>
    internal int Port { get; }

    /// <summary>L'indirizzo base, con la barra finale: <c>http://127.0.0.1:porta/</c>.</summary>
    internal Uri BaseAddress => new("http://127.0.0.1:" + Port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/");

    /// <summary>Le richieste ricevute finora (una copia), nell'ordine di arrivo delle intestazioni.</summary>
    internal IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToArray();
            }
        }
    }

    /// <summary>Quante connessioni TCP sono state accettate.</summary>
    internal int ConnectionCount => Volatile.Read(ref _connectionCount);

    /// <summary>Le eccezioni uscite dai gestori della prova (di norma vuota; utile per capire un fallimento).</summary>
    internal IReadOnlyList<Exception> HandlerErrors
    {
        get
        {
            lock (_gate)
            {
                return _handlerErrors.ToArray();
            }
        }
    }

    /// <summary>Avvia un server che passa ogni richiesta a <paramref name="handler"/>.</summary>
    /// <param name="handler">Cosa fare con ogni richiesta.</param>
    /// <param name="keepBodyBytes">Fino a quanti byte di corpo tenere in memoria (oltre si tengono solo conteggio e SHA-256).</param>
    /// <returns>Il server avviato; va smaltito.</returns>
    internal static LoopbackServer Start(LoopbackHandler handler, int keepBodyBytes = 4 * 1024 * 1024) => new(handler, keepBodyBytes);

    /// <summary>Aspetta che siano arrivate almeno <paramref name="count"/> richieste (al massimo <see cref="Limit"/>, poi <see cref="TimeoutException"/>).</summary>
    /// <param name="count">Quante richieste.</param>
    /// <returns>Le richieste ricevute.</returns>
    internal async Task<IReadOnlyList<RecordedRequest>> WaitForRequestsAsync(int count)
    {
        var limit = Task.Delay(Limit);
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (_requests.Count >= count)
                {
                    return _requests.ToArray();
                }

                changed = _changed.Task;
            }

            if (await Task.WhenAny(changed, limit).ConfigureAwait(false) == limit)
            {
                throw new TimeoutException("Il server di loopback non ha ricevuto " + count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " richieste entro il limite.");
            }
        }
    }

    /// <summary>Ferma il server: chiude il listener e ogni connessione, poi aspetta (con un limite) che i cicli finiscano.</summary>
    public void Dispose()
    {
        Task[] pending;
        lock (_gate)
        {
            if (_stop.IsCancellationRequested)
            {
                return;
            }

            _stop.Cancel();
            foreach (var client in _clients)
            {
                client.Close();
            }

            pending = _connections.Concat(new[] { _acceptLoop }).ToArray();
        }

        _listener.Stop();
        Task.WaitAny(Task.WhenAll(pending), Task.Delay(TimeSpan.FromSeconds(5)));
        _stop.Dispose();
    }

    internal static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal CancellationToken Stopping => _stop.Token;

    internal void Record(RecordedRequest request)
    {
        TaskCompletionSource<bool> changed;
        lock (_gate)
        {
            _requests.Add(request);
            changed = _changed;
            _changed = NewSignal();
        }

        changed.TrySetResult(true);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            var id = Interlocked.Increment(ref _connectionCount);
            lock (_gate)
            {
                if (_stop.IsCancellationRequested)
                {
                    client.Close();
                    return;
                }

                _clients.Add(client);
                _connections.Add(Task.Run(() => ServeAsync(client, id)));
            }
        }
    }

    // Una connessione: richieste in sequenza finche' il client chiude, il gestore chiude o il corpo resta non letto.
    private async Task ServeAsync(TcpClient client, int connectionId)
    {
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            var reader = new HttpWireReader(stream);
            while (!_stop.IsCancellationRequested)
            {
                var request = await reader.ReadHeadAsync(connectionId, _keepBodyBytes).ConfigureAwait(false);
                if (request is null)
                {
                    return;
                }

                Record(request);
                var exchange = new LoopbackExchange(this, client, stream, reader, request);
                try
                {
                    await _handler(exchange).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // Un errore del gestore (spesso la connessione chiusa dal client) chiude la connessione: lo si registra.
                catch (Exception exception)
#pragma warning restore CA1031
                {
                    lock (_gate)
                    {
                        _handlerErrors.Add(exception);
                    }

                    return;
                }

                if (exchange.Closed || !request.BodyComplete || !exchange.Responded)
                {
                    return;
                }
            }
        }
#pragma warning disable CA1031 // La connessione chiusa dall'altra parte (o dallo smaltimento) e' la fine normale del ciclo.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
        finally
        {
            client.Close();
        }
    }
}
