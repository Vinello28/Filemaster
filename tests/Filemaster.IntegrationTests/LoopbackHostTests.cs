using Filemaster.Application;
using Filemaster.Domain;
using Filemaster.Infrastructure;
using Filemaster.IntegrationTests.Loopback;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using static Filemaster.IntegrationTests.Loopback.LoopbackSupport;

namespace Filemaster.IntegrationTests;

/// <summary>
/// <c>AddFilemaster</c> nell'host generico vero con il gestore primario della libreria (non sostituito) verso il server di loopback:
/// le regole del primario valgono anche qui, e la rotazione dei gestori di <c>IHttpClientFactory</c> non rompe un download aperto.
/// </summary>
public sealed class LoopbackHostTests
{
    private const int Size = 100_000;

    private static IHost BuildHost(LoopbackServer server, CapturingLogs? logs = null, TimeSpan? handlerLifetime = null)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        if (logs is not null)
        {
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Trace);
            builder.Logging.AddProvider(logs);
        }

        var http = builder.Services.AddFilemaster(o =>
        {
            var defaults = Options(server);
            o.BaseAddress = defaults.BaseAddress;
            o.ApiKey = defaults.ApiKey;
        });
        if (handlerLifetime is { } lifetime)
        {
            http.SetHandlerLifetime(lifetime);
        }

        return builder.Build();
    }

    [Fact]
    public async Task The_DI_client_with_the_library_primary_does_not_follow_redirects_and_streams_uploads_with_Content_Length()
    {
        using var elsewhere = LoopbackServer.Start(exchange => exchange.RespondAsync(200, TenantJson));
        using var server = LoopbackServer.Start(async exchange =>
        {
            if (exchange.Request.Method == "POST")
            {
                await exchange.ReadBodyAsync().ConfigureAwait(false);
                await exchange.RespondAsync(201, UploadJson).ConfigureAwait(false);
            }
            else
            {
                await exchange.RespondAsync(302, string.Empty, "text/plain", "Location: " + elsewhere.BaseAddress + "tenant").ConfigureAwait(false);
            }
        });
        using var host = BuildHost(server);
        var token = TestContext.Current.CancellationToken;
        await host.StartAsync(token);
        var client = host.Services.GetRequiredService<IFilemasterClient>();
        using var source = new GeneratedStream(200_000, seekable: true);

        var redirect = await ThrowsWithin<UnexpectedResponseException>(() => client.Tenant.GetAsync(token));
        await Within(client.Documents.UploadAsync(new UploadDocumentRequest(source, "a.pdf"), token));
        await host.StopAsync(token);

        Assert.Equal(302, redirect.StatusCode);
        Assert.Equal(0, elsewhere.ConnectionCount);
        var upload = server.Requests[1];
        Assert.Equal(upload.BodyLength, upload.ContentLength);
        Assert.False(upload.IsChunked);
        Assert.Equal(0, server.Requests[0].HeaderCount("Accept-Encoding"));
        Assert.False(source.Disposed);
    }

    [Fact]
    public async Task A_download_opened_before_the_factory_rotates_and_disposes_its_handler_is_read_to_the_end()
    {
        // HandlerLifetime di 1 s (il minimo della factory): il download si apre sul gestore A, A scade, la pulizia periodica della
        // factory (ogni 10 s) smaltisce A quando nessuno lo raggiunge piu' (il trasporto non tiene il client), una chiamata nuova crea B.
        // Il download, aperto su A, deve arrivare intero dopo lo smaltimento. Si aspettano i segnali dei log della factory, non intervalli.
        var release = LoopbackServer.NewSignal();
        using var server = LoopbackServer.Start(async exchange =>
        {
            if (exchange.Path.EndsWith("/content", StringComparison.Ordinal))
            {
                var body = Bytes(Size);
                await exchange.WriteHeadAsync(200, "application/pdf", Size).ConfigureAwait(false);
                await exchange.WriteAsync(body.Take(1000).ToArray()).ConfigureAwait(false);
                await exchange.WaitAsync(release.Task).ConfigureAwait(false);
                await exchange.WriteAsync(body.Skip(1000).ToArray()).ConfigureAwait(false);
            }
            else
            {
                await exchange.RespondAsync(200, TenantJson).ConfigureAwait(false);
            }
        });
        var logs = new CapturingLogs();
        using var host = BuildHost(server, logs, TimeSpan.FromSeconds(1));
        var token = TestContext.Current.CancellationToken;
        await host.StartAsync(token);
        var client = host.Services.GetRequiredService<IFilemasterClient>();

        // Finche' il gestore A e' l'unico mai creato, un ciclo di pulizia che ne smaltisce uno ha smaltito proprio A.
        using var content = await Within(client.Documents.OpenContentAsync(DocId, cancellationToken: token));
        await logs.WaitForAsync("HandlerExpired", _ => true, TimeSpan.FromSeconds(20));
        await logs.WaitForAsync("CleanupCycleEnd", state => Convert.ToInt32(state["DisposedCount"], System.Globalization.CultureInfo.InvariantCulture) >= 1, TimeSpan.FromSeconds(40));

        // Una chiamata nuova va sul gestore B (connessione nuova) mentre il download e' ancora aperto su A.
        await Within(client.Tenant.GetAsync(token));
        Assert.Equal(2, server.ConnectionCount);
        release.TrySetResult(true);
        var bytes = await Within(ReadAllAsync(content.Content));
        await host.StopAsync(token);

        Assert.Equal(Bytes(Size), bytes);
    }

    /// <summary>Tiene in memoria i log di <c>DefaultHttpClientFactory</c> (nome dell'evento e stato strutturato).</summary>
    private sealed class CapturingLogs : ILoggerProvider
    {
        private readonly object _gate = new();
        private readonly List<(string Event, IReadOnlyDictionary<string, object?> State)> _entries = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose()
        {
        }

        /// <summary>
        /// Aspetta un evento che soddisfi <paramref name="match"/>, entro <paramref name="limit"/>. Ogni giro fa una raccolta completa: la
        /// factory smaltisce un gestore scaduto solo quando il suo riferimento debole e' morto.
        /// </summary>
        internal async Task WaitForAsync(string eventName, Func<IReadOnlyDictionary<string, object?>, bool> match, TimeSpan limit)
        {
            var deadline = DateTime.UtcNow + limit;
            while (true)
            {
                lock (_gate)
                {
                    if (_entries.Any(e => e.Event == eventName && match(e.State)))
                    {
                        return;
                    }
                }

                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("Nessun evento " + eventName + " della factory entro " + limit.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + " s.");
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Delay(100).ConfigureAwait(false);
            }
        }

        private void Add(string eventName, IReadOnlyDictionary<string, object?> state)
        {
            lock (_gate)
            {
                _entries.Add((eventName, state));
            }
        }

        private sealed class Logger : ILogger
        {
            private readonly CapturingLogs _owner;
            private readonly bool _factory;

            internal Logger(CapturingLogs owner, string category)
            {
                _owner = owner;
                _factory = category.StartsWith("Microsoft.Extensions.Http.DefaultHttpClientFactory", StringComparison.Ordinal);
            }

            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => _factory;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!_factory || eventId.Name is null)
                {
                    return;
                }

                var values = new Dictionary<string, object?>(StringComparer.Ordinal);
                if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
                {
                    foreach (var pair in pairs)
                    {
                        values[pair.Key] = pair.Value;
                    }
                }

                _owner.Add(eventId.Name, values);
            }
        }
    }
}
