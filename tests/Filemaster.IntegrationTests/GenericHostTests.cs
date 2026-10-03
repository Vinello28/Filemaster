using System.Net;
using System.Text;
using Filemaster.Application;
using Filemaster.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Filemaster.IntegrationTests;

/// <summary>
/// <c>AddFilemaster</c> dentro l'host generico vero (quello di ASP.NET Core e dei Worker Service), con la pipeline vera di
/// <c>IHttpClientFactory</c> (log, gestori, rotazione) e un gestore finto al posto della rete: niente server (il server di loopback e'
/// T6.1).
/// </summary>
public sealed class GenericHostTests
{
    private const string Key = "saf_FakeKeyForTestsOnly_0123456789abcdef";

    private static IHost BuildHost(RecordingHandler handler, Action<FilemasterOptions> configure)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Services.AddFilemaster(configure).ConfigurePrimaryHttpMessageHandler(() => handler);
        return builder.Build();
    }

    [Fact]
    public async Task Invalid_options_stop_the_host_at_startup()
    {
        using var host = BuildHost(new RecordingHandler(), o => o.BaseAddress = new Uri("https://filemaster.example.test/"));

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains(nameof(FilemasterOptions.ApiKey), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_port_resolved_from_the_host_talks_through_the_factory_pipeline()
    {
        var handler = new RecordingHandler();
        using var host = BuildHost(handler, o =>
        {
            o.BaseAddress = new Uri("https://filemaster.example.test/proxy");
            o.ApiKey = Key;
        });
        await host.StartAsync(TestContext.Current.CancellationToken);

        var tenant = await host.Services.GetRequiredService<ITenantInfo>().GetAsync(TestContext.Current.CancellationToken);
        var live = await host.Services.GetRequiredService<IFilemasterClient>().Health.CheckLivenessAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal("acme-test", tenant.Slug);
        Assert.True(live.IsHealthy);
        Assert.Equal(new[] { "https://filemaster.example.test/proxy/tenant", "https://filemaster.example.test/proxy/healthz" }, handler.Uris);
        Assert.Equal(new[] { Key, null }, handler.Keys);
    }

    /// <summary>Risponde all'ente e a <c>/healthz</c>; registra indirizzo e chiave di ogni richiesta.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        internal List<string> Uris { get; } = new();

        internal List<string?> Keys { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uris.Add(request.RequestUri!.AbsoluteUri);
            Keys.Add(request.Headers.TryGetValues("X-API-Key", out var values) ? values.Single() : null);
            var health = request.RequestUri.AbsolutePath.EndsWith("/healthz", StringComparison.Ordinal);
            var body = health
                ? "ok"
                : """{"id":"ten_01M3VCWS5PAKMSC47JNJ1PTFJQ","slug":"acme-test","name":"Acme Test","status":"active","created_at":"2026-10-01T09:59:09.501341Z"}""";
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, health ? "text/plain" : "application/json"),
            };
            return Task.FromResult(response);
        }
    }
}
