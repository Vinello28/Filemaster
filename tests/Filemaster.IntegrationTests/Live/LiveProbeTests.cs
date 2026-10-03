using Filemaster.Application;
using Filemaster.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using static Filemaster.IntegrationTests.Live.LiveServer;

namespace Filemaster.IntegrationTests.Live;

/// <summary>Sonde di salute e sonda dell'ente contro il server vero, con la factory e con <c>AddFilemaster</c> nell'host generico.</summary>
[Trait("Category", "Live")]
public sealed class LiveProbeTests
{
    [Fact]
    public async Task The_tenant_probe_returns_the_seeded_tenant_with_every_key_scope()
    {
        foreach (var key in new[] { WriteKey, ReadKey, AdminKey })
        {
            using var client = Client(key);

            var tenant = await client.Tenant.GetAsync(TestContext.Current.CancellationToken);

            Assert.Equal(LiveSeed.TenantSlug, tenant.Slug);
            Assert.Equal(LiveSeed.TenantName, tenant.Name);
            Assert.Equal(TenantStatus.Active, tenant.Status);
            Assert.True(TenantId.IsValid(tenant.Id.Value));
            Assert.True(tenant.CreatedAt <= DateTimeOffset.UtcNow.AddMinutes(5));
            Assert.Equal(TimeSpan.Zero, tenant.CreatedAt.Offset);
        }
    }

    [Fact]
    public async Task Liveness_and_readiness_are_healthy()
    {
        using var client = Client();

        var live = await client.Health.CheckLivenessAsync(TestContext.Current.CancellationToken);
        var ready = await client.Health.CheckReadinessAsync(TestContext.Current.CancellationToken);

        Assert.True(live.IsHealthy, live.Status + " " + live.Detail);
        Assert.True(ready.IsHealthy, ready.Status + " " + ready.Detail);
        Assert.Equal("ready", ready.Status);
    }

    [Fact]
    public async Task The_probes_succeed_with_a_key_the_server_does_not_know()
    {
        using var client = Client("saf_ThisKeyDoesNotExistOnTheServer_0123456789");

        var live = await client.Health.CheckLivenessAsync(TestContext.Current.CancellationToken);
        var ready = await client.Health.CheckReadinessAsync(TestContext.Current.CancellationToken);

        Assert.True(live.IsHealthy);
        Assert.True(ready.IsHealthy);
    }

    [Fact]
    public async Task A_client_from_AddFilemaster_in_the_generic_host_reads_the_tenant_and_a_document()
    {
        var url = Url;
        var key = WriteKey;
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Services.AddFilemaster(o =>
        {
            o.BaseAddress = url;
            o.ApiKey = key;
        });
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var client = host.Services.GetRequiredService<IFilemasterClient>();
            var tenant = await host.Services.GetRequiredService<ITenantInfo>().GetAsync(TestContext.Current.CancellationToken);
            Assert.Equal(LiveSeed.TenantSlug, tenant.Slug);

            await LiveCleanup.WithCleanupAsync(client, async cleanup =>
            {
                var bytes = UniqueBytes(3_000);
                var uploaded = await UploadAsync(client, cleanup, bytes, "di.pdf");

                using var content = await client.Documents.OpenContentAsync(uploaded.Document.Id, cancellationToken: TestContext.Current.CancellationToken);
                Assert.Equal(bytes, await ReadAllAsync(content.Content));
            });
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }
}
