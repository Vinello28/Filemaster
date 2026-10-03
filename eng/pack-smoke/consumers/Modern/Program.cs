// Consumatore net8.0/net10.0: lo scenario comune due volte, prima con la registrazione DI (AddFilemaster su una
// ServiceCollection nuda, senza host) e poi con la factory.
using System.Reflection;
using System.Threading.Tasks;
using Filemaster;
using Filemaster.Application;
using Microsoft.Extensions.DependencyInjection;

namespace PackSmoke;

internal static class Program
{
    private static Task<int> Main(string[] args) =>
        Smoke.MainAsync(args, async (version, fixtures) =>
        {
            // Il TFM di questo eseguibile (net8.0 o net10.0) e' quello che devono avere gli assembly dei pacchetti.
            var framework = Smoke.Framework(Assembly.GetEntryAssembly()!);
            Smoke.CheckPackageAssemblies(framework, version);

            using var server = Smoke.StartServer(fixtures);

            var services = new ServiceCollection();
            services.AddFilemaster(options =>
            {
                options.BaseAddress = server.BaseAddress;
                options.ApiKey = Smoke.ApiKey;
                options.Retry.MaxAttempts = 1;
            });
            await using (var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }))
            {
                await Smoke.RunAsync(provider.GetRequiredService<IFilemasterClient>(), server, "DI").ConfigureAwait(false);
            }

            using var client = FilemasterClientFactory.Create(Smoke.Options(server.BaseAddress));
            await Smoke.RunAsync(client, server, "factory").ConfigureAwait(false);
        });
}
