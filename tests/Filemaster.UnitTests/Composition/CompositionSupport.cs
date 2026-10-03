using System.Reflection;
using Filemaster.Application;
using Filemaster.Domain;
using Filemaster.Infrastructure;
using Filemaster.UnitTests.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Filemaster.UnitTests.Composition;

/// <summary>Un fornitore di logger (e una factory) che raccoglie tutto per categoria: per cercare nei log una chiave o un avviso.</summary>
internal sealed class CollectingLoggerProvider : ILoggerProvider, ILoggerFactory
{
    private readonly object _gate = new();
    private readonly Dictionary<string, CollectingLogger> _loggers = new(StringComparer.Ordinal);

    /// <summary>Le categorie viste e i loro record (copie, per leggerle senza corse).</summary>
    internal IReadOnlyList<(string Category, LogRecord Record)> Records
    {
        get
        {
            lock (_gate)
            {
                return _loggers.SelectMany(pair => pair.Value.Records.ToArray().Select(record => (pair.Key, record))).ToArray();
            }
        }
    }

    /// <summary>Tutto il testo scritto, comprese le categorie.</summary>
    internal string Everything => string.Join("\n", Records.Select(r => r.Category + " | " + r.Record.Text + " | " + r.Record.State + " | " + r.Record.ExceptionText));

    public ILogger CreateLogger(string categoryName)
    {
        lock (_gate)
        {
            if (!_loggers.TryGetValue(categoryName, out var logger))
            {
                logger = new CollectingLogger();
                _loggers.Add(categoryName, logger);
            }

            return new LockedLogger(logger, _gate);
        }
    }

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    // CollectingLogger non e' thread-safe: la factory di IHttpClientFactory scrive da piu' thread.
    private sealed class LockedLogger(CollectingLogger inner, object gate) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (gate)
            {
                inner.Log(logLevel, eventId, state, exception, formatter);
            }
        }
    }
}

/// <summary>Un gestore che ricorda di essere stato smaltito; inoltra al gestore interno (di solito un <see cref="FakeHandler"/>).</summary>
internal sealed class DisposalTrackingHandler : DelegatingHandler
{
    internal DisposalTrackingHandler(HttpMessageHandler inner)
        : base(inner)
    {
    }

    internal bool Disposed { get; private set; }

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}

/// <summary>Opzioni, risposte e chiamate condivise dai test della composizione.</summary>
internal static class Composed
{
    internal const string Key = TransportRig.Key;

    internal const string TenantJson = """{"id":"ten_01M3VCWS5PAKMSC47JNJ1PTFJQ","slug":"acme-test","name":"Acme Test","status":"active","created_at":"2026-10-01T09:59:09.501341Z"}""";

    internal const string TransportCategory = "Filemaster.Infrastructure.FilemasterTransport";

    internal static readonly Uri BaseAddress = new("https://filemaster.example.test/base/");

    /// <summary>Vero se le librerie caricate sono l'asset netstandard2.0 (metadato scritto dal csproj dei test).</summary>
    internal static bool UsesNetStandardAsset =>
        typeof(Composed).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(a => a.Key == "ExpectedLibraryAsset").Value!.StartsWith(".NETStandard", StringComparison.Ordinal);

    internal static FilemasterOptions Options(Action<FilemasterOptions>? change = null)
    {
        var options = new FilemasterOptions { BaseAddress = BaseAddress, ApiKey = Key };
        change?.Invoke(options);
        return options;
    }

    internal static void Configure(FilemasterOptions options)
    {
        options.BaseAddress = BaseAddress;
        options.ApiKey = Key;
    }

    /// <summary>Programma sul gestore finto una risposta 404 per ognuna delle chiamate di <see cref="CallEveryPortAsync"/>.</summary>
    internal static void ScriptEveryPort(FakeHandler handler)
    {
        for (var i = 0; i < 6; i++)
        {
            handler.Then(Reply.Problem(404, "not-found"));
        }
    }

    /// <summary>Una chiamata per porta (due per la salute); ogni risposta e' un 404, quindi ogni chiamata lancia un errore del server.</summary>
    internal static async Task CallEveryPortAsync(IFilemasterClient client)
    {
        await Assert.ThrowsAsync<NotFoundException>(() => client.Documents.GetAsync(Application.TestData.DocumentIdOf(1)));
        await Assert.ThrowsAsync<NotFoundException>(() => client.Folders.ListChildrenAsync());
        await Assert.ThrowsAsync<NotFoundException>(() => client.Contacts.ListCategoriesAsync());
        await Assert.ThrowsAsync<NotFoundException>(() => client.Tenant.GetAsync());
        await Assert.ThrowsAnyAsync<FilemasterException>(() => client.Health.CheckLivenessAsync());
        await Assert.ThrowsAnyAsync<FilemasterException>(() => client.Health.CheckReadinessAsync());
    }

    /// <summary>Le porte del client, nell'ordine di <see cref="IFilemasterClient"/>.</summary>
    internal static object[] PortsOf(IFilemasterClient client) =>
        new object[] { client.Documents, client.Folders, client.Contacts, client.Tenant, client.Health };
}
