using System.Net;
using Filemaster.Application;
using Filemaster.Domain;
using Filemaster.Infrastructure;
using Filemaster.UnitTests.Application;
using Filemaster.UnitTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Filemaster.UnitTests.Composition;

/// <summary>
/// <c>AddFilemaster</c>: cosa registra (client e cinque porte, singleton), quando le opzioni non valide fermano l'avvio, il client HTTP
/// nominato (Timeout infinito, gestore primario senza redirect ne' decompressione, controllato anche se sostituito), le intestazioni
/// segrete oscurate nei log di <see cref="IHttpClientFactory"/>, un client HTTP chiesto alla factory a ogni tentativo.
/// </summary>
public sealed class AddFilemasterTests
{
    private static readonly Type[] PortTypes =
    {
        typeof(IDocumentStore), typeof(IFolderCatalog), typeof(IContactDirectory), typeof(ITenantInfo), typeof(IFilemasterHealth),
    };

    // Un contenitore con il gestore primario sostituito da quello finto (dopo AddFilemaster, come farebbe un utente).
    private static ServiceProvider Build(FakeHandler handler, Action<FilemasterOptions>? configure = null, Action<IServiceCollection, IHttpClientBuilder>? more = null)
    {
        var services = new ServiceCollection();
        var builder = services.AddFilemaster(configure ?? Composed.Configure).ConfigurePrimaryHttpMessageHandler(() => handler);
        more?.Invoke(services, builder);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    [Fact]
    public void Null_arguments_are_rejected_with_their_names()
    {
        Assert.Equal("services", Assert.Throws<ArgumentNullException>(() => FilemasterServiceCollectionExtensions.AddFilemaster(null!, Composed.Configure)).ParamName);
        Assert.Equal("configure", Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddFilemaster(null!)).ParamName);
    }

    [Fact]
    public void The_returned_builder_is_the_named_client()
    {
        var services = new ServiceCollection();

        var builder = services.AddFilemaster(Composed.Configure);

        Assert.Equal("Filemaster", FilemasterServiceCollectionExtensions.HttpClientName);
        Assert.Equal(FilemasterServiceCollectionExtensions.HttpClientName, builder.Name);
    }

    [Fact]
    public void The_client_and_the_five_ports_are_registered_as_singletons()
    {
        var services = new ServiceCollection();
        services.AddFilemaster(Composed.Configure);

        foreach (var type in PortTypes.Prepend(typeof(IFilemasterClient)))
        {
            var descriptor = Assert.Single(services, d => d.ServiceType == type);
            Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        }
    }

    [Fact]
    public void Every_port_resolves_to_the_port_of_the_single_client_also_from_a_scope()
    {
        using var provider = Build(new FakeHandler());

        var client = provider.GetRequiredService<IFilemasterClient>();
        using var scope = provider.CreateScope();

        Assert.Same(client, provider.GetRequiredService<IFilemasterClient>());
        Assert.Same(client, scope.ServiceProvider.GetRequiredService<IFilemasterClient>());
        Assert.Equal(Composed.PortsOf(client), PortTypes.Select(provider.GetRequiredService));
        Assert.Equal(Composed.PortsOf(client), PortTypes.Select(scope.ServiceProvider.GetRequiredService));
        Assert.All(Composed.PortsOf(client), Assert.NotNull);
    }

    [Fact]
    public void A_port_registered_before_AddFilemaster_is_kept()
    {
        var mine = new FakeDocumentStore();
        var services = new ServiceCollection();
        services.AddSingleton<IDocumentStore>(mine);
        services.AddFilemaster(Composed.Configure).ConfigurePrimaryHttpMessageHandler(() => new FakeHandler());
        using var provider = services.BuildServiceProvider();

        Assert.Same(mine, provider.GetRequiredService<IDocumentStore>());
        Assert.NotSame(mine, provider.GetRequiredService<IFilemasterClient>().Documents);
    }

    [Fact]
    public async Task The_resolved_ports_send_the_configured_key_to_the_configured_address()
    {
        var handler = new FakeHandler();
        Composed.ScriptEveryPort(handler);
        using var provider = Build(handler);

        await Composed.CallEveryPortAsync(provider.GetRequiredService<IFilemasterClient>());

        Assert.Equal(6, handler.Requests.Count);
        Assert.All(handler.Requests, sent => Assert.StartsWith(Composed.BaseAddress.AbsoluteUri, sent.Uri.AbsoluteUri, StringComparison.Ordinal));
        Assert.All(handler.Requests.Take(4), sent => Assert.Equal(Composed.Key, sent.Header("X-API-Key")));
    }

    // ----- opzioni -----

    [Fact]
    public void Invalid_options_fail_the_startup_validation_without_revealing_the_key()
    {
        const string BadKey = "saf_secret with space";
        using var provider = Build(new FakeHandler(), o =>
        {
            Composed.Configure(o);
            o.ApiKey = BadKey;
        });

        // IStartupValidator e' cio' che l'host generico chiama in StartAsync (ValidateOnStart).
        var exception = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Equal(FilemasterServiceCollectionExtensions.HttpClientName, exception.OptionsName);
        Assert.Contains(nameof(FilemasterOptions.ApiKey), exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(BadKey, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_options_also_fail_the_first_resolution_of_the_client_and_of_every_port()
    {
        using var provider = Build(new FakeHandler(), o => o.ApiKey = Composed.Key); // manca l'indirizzo

        var exception = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IFilemasterClient>());
        Assert.Contains(nameof(FilemasterOptions.BaseAddress), exception.Message, StringComparison.Ordinal);
        foreach (var type in PortTypes)
        {
            Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService(type));
        }
    }

    [Fact]
    public void Retry_options_out_of_range_fail_the_startup_validation()
    {
        using var provider = Build(new FakeHandler(), o =>
        {
            Composed.Configure(o);
            o.Retry.MaxAttempts = 0;
        });

        var exception = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains(nameof(FilemasterRetryOptions.MaxAttempts), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Valid_options_pass_the_startup_validation()
    {
        using var provider = Build(new FakeHandler());

        provider.GetRequiredService<IStartupValidator>().Validate();

        Assert.NotNull(provider.GetRequiredService<IFilemasterClient>());
    }

    [Fact]
    public void The_validator_ignores_options_with_another_name()
    {
        using var provider = Build(new FakeHandler());

        var other = provider.GetRequiredService<IOptionsMonitor<FilemasterOptions>>().Get("altro");

        Assert.Null(other.BaseAddress); // non valide, ma non sono le nostre: nessuna eccezione
    }

    // ----- client HTTP e gestore primario -----

    [Fact]
    public void The_named_HttpClient_has_an_infinite_timeout()
    {
        using var provider = Build(new FakeHandler());

        using var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient(FilemasterServiceCollectionExtensions.HttpClientName);

        Assert.Equal(Timeout.InfiniteTimeSpan, http.Timeout);
    }

    [Fact]
    public void The_primary_handler_follows_no_redirect_decompresses_nothing_and_matches_the_asset()
    {
        HttpMessageHandler? primary = null;
        var services = new ServiceCollection();
        services.AddFilemaster(Composed.Configure);
        services.Configure<HttpClientFactoryOptions>(
            FilemasterServiceCollectionExtensions.HttpClientName,
            o => o.HttpMessageHandlerBuilderActions.Add(b => primary = b.PrimaryHandler));
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IFilemasterClient>();

        HandlerAssertions.IsTheLibraryPrimary(primary);
    }

    [Fact]
    public void A_replaced_primary_handler_that_follows_redirects_is_rejected_at_resolution()
    {
        using var provider = Build(new FakeHandler(), more: (_, builder) =>
            builder.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None }));

        var exception = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IFilemasterClient>());

        Assert.Contains("AllowAutoRedirect", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_replaced_primary_handler_that_decompresses_is_rejected_at_resolution()
    {
        using var provider = Build(new FakeHandler(), more: (_, builder) =>
            builder.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.GZip }));

        var exception = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IFilemasterClient>());

        Assert.Contains("AutomaticDecompression", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_replaced_primary_handler_with_the_rules_is_accepted()
    {
        using var provider = Build(new FakeHandler(), more: (_, builder) =>
            builder.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None }));

        Assert.NotNull(provider.GetRequiredService<IFilemasterClient>());
    }

    [Fact]
    public void The_handler_check_leaves_the_other_named_clients_alone()
    {
        using var provider = Build(new FakeHandler(), more: (services, _) => services.AddHttpClient("altro"));

        using var other = provider.GetRequiredService<IHttpClientFactory>().CreateClient("altro"); // gestore di default: segue i redirect

        Assert.NotNull(other);
    }

    [Fact]
    public void A_finite_timeout_set_after_AddFilemaster_is_rejected_at_resolution()
    {
        using var provider = Build(new FakeHandler(), more: (_, builder) => builder.ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(100)));

        var exception = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IFilemasterClient>());

        Assert.Contains("Timeout", exception.Message, StringComparison.Ordinal);
    }

    // ----- log -----

    [Fact]
    public async Task The_key_and_the_authorization_header_never_appear_in_the_HttpClientFactory_logs()
    {
        const string Bearer = "Bearer token-che-non-deve-uscire";
        var handler = new FakeHandler();
        handler.Then(Reply.Json(200, Composed.TenantJson));
        using var logs = new CollectingLoggerProvider();
        using var provider = Build(handler, more: (services, builder) =>
        {
            services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
            builder.AddHttpMessageHandler(() => new AddHeader("Authorization", Bearer));
        });

        await provider.GetRequiredService<ITenantInfo>().GetAsync();

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(Composed.Key, sent.Header("X-API-Key")); // la chiave e' partita davvero...
        Assert.Equal(Bearer, sent.Header("Authorization"));
        var factoryLogs = string.Join("\n", logs.Records.Where(r => r.Category.StartsWith("System.Net.Http.HttpClient.Filemaster", StringComparison.Ordinal)).Select(r => r.Record.Text));
        Assert.Contains("X-API-Key: *", factoryLogs, StringComparison.Ordinal); // ...e le intestazioni sono state scritte, oscurate
        Assert.Contains("Authorization: *", factoryLogs, StringComparison.Ordinal);
        Assert.Contains("X-Request-ID: ", factoryLogs, StringComparison.Ordinal); // le altre restano leggibili
        Assert.DoesNotContain(Composed.Key, logs.Everything, StringComparison.Ordinal);
        Assert.DoesNotContain("token-che-non-deve-uscire", logs.Everything, StringComparison.Ordinal);
    }

    [Fact]
    public void The_client_logs_through_the_container_logger_factory()
    {
        using var logs = new CollectingLoggerProvider();
        using var provider = Build(
            new FakeHandler(),
            o =>
            {
                Composed.Configure(o);
                o.BaseAddress = new Uri("http://filemaster.example.test/");
            },
            (services, _) => services.AddLogging(b => b.AddProvider(logs)));

        provider.GetRequiredService<IFilemasterClient>();

        var warning = Assert.Single(logs.Records, r => r.Category == Composed.TransportCategory);
        Assert.Equal(LogLevel.Warning, warning.Record.Level);
    }

    // ----- durata e factory -----

    [Fact]
    public async Task The_singleton_client_asks_the_factory_for_an_HttpClient_on_every_attempt()
    {
        var handler = new FakeHandler();
        handler.Then(Reply.Json(200, Composed.TenantJson))
            .Then(Reply.Text(503))
            .Then(Reply.Json(200, Composed.TenantJson));
        CountingFactory? counting = null;
        using var provider = Build(handler, o =>
        {
            Composed.Configure(o);
            o.Retry.InitialDelay = TimeSpan.Zero;
            o.Retry.MaxDelay = TimeSpan.Zero;
        }, (services, _) =>
        {
            var original = services.Last(d => d.ServiceType == typeof(IHttpClientFactory));
            services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(sp =>
                counting = new CountingFactory((IHttpClientFactory)original.ImplementationFactory!(sp))));
        });
        var tenant = provider.GetRequiredService<ITenantInfo>();
        var atCreation = counting!.Created;

        await tenant.GetAsync(); // un tentativo
        await tenant.GetAsync(); // due tentativi (503 di un proxy, ritentato)

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(3, counting.Created - atCreation);
        Assert.All(counting.Names, name => Assert.Equal(FilemasterServiceCollectionExtensions.HttpClientName, name));
    }

    [Fact]
    public async Task The_client_uses_the_TimeProvider_of_the_container()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero));
        var entered = Waiting.NewSignal();
        var handler = new FakeHandler();
        handler.Then(Waiting.Hang(entered));
        using var provider = Build(handler, more: (services, _) => services.AddSingleton<TimeProvider>(time));

        var call = provider.GetRequiredService<ITenantInfo>().GetAsync();
        await Waiting.Within(entered.Task);
        time.Advance(TimeSpan.FromSeconds(31)); // RequestTimeout di default: 30 secondi

        await Assert.ThrowsAsync<FilemasterTimeoutException>(() => Waiting.Within(call));
    }

    private sealed class AddHeader(string name, string value) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.TryAddWithoutValidation(name, value);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class CountingFactory(IHttpClientFactory inner) : IHttpClientFactory
    {
        private int _created;

        internal int Created => Volatile.Read(ref _created);

        internal System.Collections.Concurrent.ConcurrentQueue<string> Names { get; } = new();

        public HttpClient CreateClient(string name)
        {
            Interlocked.Increment(ref _created);
            Names.Enqueue(name);
            return inner.CreateClient(name);
        }
    }
}
