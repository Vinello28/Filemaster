using System.Net;
using Filemaster.Infrastructure;
using Filemaster.UnitTests.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Filemaster.UnitTests.Composition;

/// <summary>
/// <see cref="FilemasterClientFactory.Create"/>: opzioni e gestore controllati prima di creare qualcosa, il gestore della factory
/// (configurazione per asset) smaltito con il client, quello dell'utente mai.
/// </summary>
public sealed class FilemasterClientFactoryTests
{
    [Fact]
    public void Null_options_are_rejected()
    {
        Assert.Equal("options", Assert.Throws<ArgumentNullException>(() => FilemasterClientFactory.Create(null!)).ParamName);
    }

    [Fact]
    public void Invalid_options_are_rejected_with_the_property_name_and_the_handler_is_left_alone()
    {
        var handler = new DisposalTrackingHandler(new FakeHandler());

        var exception = Assert.Throws<ArgumentException>(() => FilemasterClientFactory.Create(Composed.Options(o => o.BaseAddress = null), handler));

        Assert.Equal(nameof(FilemasterOptions.BaseAddress), exception.ParamName);
        Assert.False(handler.Disposed);
    }

    [Fact]
    public async Task A_client_on_the_user_handler_works_and_disposing_it_does_not_dispose_the_handler()
    {
        var fake = new FakeHandler();
        fake.Then(Reply.Json(200, Composed.TenantJson)).Then(Reply.Json(200, "{}"));
        var handler = new DisposalTrackingHandler(fake);

        var client = FilemasterClientFactory.Create(Composed.Options(), handler);
        var tenant = await client.Tenant.GetAsync();
        client.Dispose();
        client.Dispose(); // idempotente

        Assert.Equal("acme-test", tenant.Slug);
        Assert.Equal(Composed.Key, Assert.Single(fake.Requests).Header("X-API-Key"));
        Assert.False(client.OwnsHandler);
        Assert.Same(handler, client.Handler);
        Assert.False(handler.Disposed);
        using var invoker = new HttpMessageInvoker(handler, disposeHandler: false); // ancora utilizzabile
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://other.example.test/"));
        using var response = await invoker.SendAsync(request, CancellationToken.None);
        Assert.Equal(200, (int)response.StatusCode);
    }

    [Fact]
    public async Task After_disposal_every_call_fails_with_ObjectDisposedException()
    {
        var client = FilemasterClientFactory.Create(Composed.Options(), new FakeHandler());

        client.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.Tenant.GetAsync());
        Assert.NotNull(client.Documents); // le porte restano leggibili
    }

    [Fact]
    public void Without_a_handler_the_factory_creates_and_owns_the_library_primary()
    {
        using var client = FilemasterClientFactory.Create(Composed.Options());

        Assert.True(client.OwnsHandler);
        HandlerAssertions.IsTheLibraryPrimary(client.Handler);
    }

    [Fact]
    public async Task The_factory_handler_is_disposed_with_the_client()
    {
        var client = FilemasterClientFactory.Create(Composed.Options());
        var handler = client.Handler;

        client.Dispose();

        // Un gestore smaltito rifiuta l'invio prima di toccare la rete; uno vivo proverebbe a connettersi (porta 1: rifiutata).
        using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("http://127.0.0.1:1/"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => invoker.SendAsync(request, CancellationToken.None));
    }

    [Fact]
    public void The_client_is_the_facade_and_the_ports_are_stable()
    {
        using var client = FilemasterClientFactory.Create(Composed.Options(), new FakeHandler());

        Assert.All(Composed.PortsOf(client), Assert.NotNull);
        Assert.Equal(Composed.PortsOf(client), Composed.PortsOf(client));
        Assert.IsAssignableFrom<IDisposable>(client);
    }

    [Fact]
    public async Task Every_port_speaks_through_the_handler_with_the_same_headers()
    {
        var fake = new FakeHandler();
        Composed.ScriptEveryPort(fake);
        using var client = FilemasterClientFactory.Create(Composed.Options(), fake);

        await Composed.CallEveryPortAsync(client);

        Assert.Equal(6, fake.Requests.Count);
        Assert.Single(fake.Requests.Select(sent => sent.Header("User-Agent")).Distinct());
        Assert.All(fake.Requests.Take(4), sent => Assert.Equal(Composed.Key, sent.Header("X-API-Key")));
    }

    [Fact]
    public void The_logger_factory_is_used()
    {
        using var logs = new CollectingLoggerProvider();

        using var client = FilemasterClientFactory.Create(Composed.Options(o => o.BaseAddress = new Uri("http://filemaster.example.test/")), new FakeHandler(), logs);

        var record = Assert.Single(logs.Records);
        Assert.Equal(Composed.TransportCategory, record.Category);
        Assert.Equal(LogLevel.Warning, record.Record.Level);
    }

    // ----- gestore dell'utente che viola le regole -----

    [Fact]
    public void A_user_HttpClientHandler_that_follows_redirects_is_rejected_and_not_disposed()
    {
        using var handler = new HttpClientHandler(); // AllowAutoRedirect = true di default

        var exception = Assert.Throws<ArgumentException>(() => FilemasterClientFactory.Create(Composed.Options(), handler));

        Assert.Equal("handler", exception.ParamName);
        Assert.Contains("AllowAutoRedirect", exception.Message, StringComparison.Ordinal);
        Assert.True(handler.AllowAutoRedirect); // il gestore non e' stato toccato
    }

    [Fact]
    public void A_user_HttpClientHandler_that_decompresses_is_rejected()
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.Deflate };

        var exception = Assert.Throws<ArgumentException>(() => FilemasterClientFactory.Create(Composed.Options(), handler));

        Assert.Equal("handler", exception.ParamName);
        Assert.Contains("AutomaticDecompression", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unsafe_primary_under_a_chain_of_delegating_handlers_is_rejected()
    {
        using var primary = new HttpClientHandler();
        using var chain = new DisposalTrackingHandler(new DisposalTrackingHandler(primary));

        var exception = Assert.Throws<ArgumentException>(() => FilemasterClientFactory.Create(Composed.Options(), chain));

        Assert.Equal("handler", exception.ParamName);
        Assert.False(chain.Disposed);
    }

    [Fact]
    public void A_user_HttpClientHandler_with_the_rules_is_accepted_and_kept()
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None };

        using var client = FilemasterClientFactory.Create(Composed.Options(), handler);

        Assert.Same(handler, client.Handler);
        Assert.False(client.OwnsHandler);
    }

#if NET
    [Fact]
    public void A_user_SocketsHttpHandler_that_follows_redirects_is_rejected_where_the_type_is_known()
    {
        using var handler = new SocketsHttpHandler();

        if (Composed.UsesNetStandardAsset)
        {
            // L'asset netstandard2.0 non conosce SocketsHttpHandler: non lo puo' ispezionare e lo accetta (documentato).
            using var accepted = FilemasterClientFactory.Create(Composed.Options(), handler);
            Assert.Same(handler, accepted.Handler);
            return;
        }

        var exception = Assert.Throws<ArgumentException>(() => FilemasterClientFactory.Create(Composed.Options(), handler));
        Assert.Equal("handler", exception.ParamName);
    }

    [Fact]
    public void A_user_SocketsHttpHandler_that_decompresses_is_rejected_where_the_type_is_known()
    {
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.Brotli };

        if (Composed.UsesNetStandardAsset)
        {
            using var accepted = FilemasterClientFactory.Create(Composed.Options(), handler);
            return;
        }

        var exception = Assert.Throws<ArgumentException>(() => FilemasterClientFactory.Create(Composed.Options(), handler));
        Assert.Contains("AutomaticDecompression", exception.Message, StringComparison.Ordinal);
    }
#endif
}
