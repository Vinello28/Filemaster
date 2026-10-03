// Consumatore net48 in C# 7.3: la factory con le opzioni valorizzate dai setter, poi lo scenario comune. Gira solo su
// Windows (su macOS/Linux run.sh lo compila soltanto).
using System;
using System.Text.Json;
using System.Threading.Tasks;
using Filemaster;

namespace PackSmoke
{
    internal static class Program
    {
        private static Task<int> Main(string[] args)
        {
            return Smoke.MainAsync(args, RunAsync);
        }

        private static async Task RunAsync(string version, string fixtures)
        {
            // Su .NET Framework si carica l'asset netstandard2.0 dei pacchetti.
            Smoke.CheckPackageAssemblies(".NETStandard,Version=v2.0", version);

            // Le dipendenze con floor 8.0.x (le versioni risolte le controlla run.sh su project.assets.json).
            Console.WriteLine("Dipendenze caricate:");
            Smoke.PrintDependency("System.Text.Json", typeof(JsonElement));
            Smoke.PrintDependency("Microsoft.Bcl.AsyncInterfaces", typeof(IAsyncDisposable));
            Smoke.PrintDependency("Microsoft.Bcl.TimeProvider", typeof(TimeProvider));

            using (var server = Smoke.StartServer(fixtures))
            using (var client = FilemasterClientFactory.Create(Smoke.Options(server.BaseAddress)))
            {
                await Smoke.RunAsync(client, server, "factory").ConfigureAwait(false);
            }
        }
    }
}
