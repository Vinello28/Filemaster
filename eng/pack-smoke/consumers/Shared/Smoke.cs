// Scenario comune ai consumatori dello smoke: lo stesso codice gira su net8.0, net10.0 e net48. Deve compilare in C# 7.3
// (il consumatore net48 non imposta LangVersion): i tipi di input (UploadDocumentRequest, DocumentQuery, FilemasterOptions,
// FilemasterRetryOptions) si valorizzano con inizializzatori di oggetto, cioe' con i setter. Se una di quelle proprieta'
// diventasse `init` o `required`, il build net48 fallirebbe qui (CS8370): e' il cancello voluto.
#if NETCOREAPP
#nullable disable
#endif

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Filemaster;
using Filemaster.Application;
using Filemaster.Domain;
using Filemaster.Infrastructure;

namespace PackSmoke
{
    internal sealed class SmokeFailure : Exception
    {
        public SmokeFailure(string message)
            : base(message)
        {
        }
    }

    internal static class Smoke
    {
        // La chiave finta delle fixture scrubbate: non e' un segreto.
        public const string ApiKey = "saf_FakeKeyForTestsOnly_0123456789abcdef";

        // Valori delle fixture catturate (tests/Filemaster.UnitTests/Wire/Fixtures/captured, copiate da run.sh).
        private const string TenantFixture = "03-tenant.json";
        private const string UploadFixture = "47-doc-upload.json";
        private const string ListFixture = "71-docs-list-limit1-page1.json";
        private const string ContactValue = "2";

        // Punto d'ingresso comune: argomenti <versione-attesa> <cartella-fixture>; exit 0 se tutto passa, 1 altrimenti.
        public static async Task<int> MainAsync(string[] args, Func<string, string, Task> scenario)
        {
            if (args.Length != 2)
            {
                Console.Error.WriteLine("Uso: <consumatore> <versione-attesa> <cartella-fixture>");
                return 64;
            }

            try
            {
                await scenario(args[0], args[1]).ConfigureAwait(false);
                Console.WriteLine("SMOKE SUPERATO (" + Framework(Assembly.GetEntryAssembly()) + ")");
                return 0;
            }
            catch (SmokeFailure e)
            {
                Console.Error.WriteLine("ERRORE: " + e.Message);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("ERRORE inatteso: " + e);
            }

            return 1;
        }

        public static void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new SmokeFailure(message);
            }
        }

        public static string Framework(Assembly assembly)
        {
            var attribute = assembly.GetCustomAttribute<TargetFrameworkAttribute>();
            return attribute == null ? "(nessun TargetFrameworkAttribute)" : attribute.FrameworkName;
        }

        // I 4 assembly dei pacchetti devono venire dalla cartella lib/ giusta (TargetFrameworkAttribute) e avere la versione
        // del pacchetto installato (InformationalVersion, con l'eventuale "+commit" di SourceLink).
        public static void CheckPackageAssemblies(string expectedFramework, string expectedVersion)
        {
            var assemblies = new[]
            {
                typeof(FilemasterClientFactory).Assembly,
                typeof(Tenant).Assembly,
                typeof(IFilemasterClient).Assembly,
                typeof(FilemasterOptions).Assembly,
            };
            var names = new[] { "Filemaster", "Filemaster.Domain", "Filemaster.Application", "Filemaster.Infrastructure" };
            Console.WriteLine("Assembly di Filemaster caricati (atteso " + expectedFramework + ", versione " + expectedVersion + "):");
            for (var i = 0; i < assemblies.Length; i++)
            {
                var assembly = assemblies[i];
                var name = assembly.GetName().Name;
                var framework = Framework(assembly);
                var info = InformationalVersion(assembly);
                Console.WriteLine("  " + name + " " + info + " [" + framework + "] " + assembly.Location);
                Check(name == names[i], "assembly inatteso: " + name + " al posto di " + names[i]);
                Check(framework == expectedFramework, name + " viene da " + framework + ", atteso " + expectedFramework + " (cartella lib/ sbagliata)");
                Check(
                    info == expectedVersion || info.StartsWith(expectedVersion + "+", StringComparison.Ordinal),
                    name + " ha versione " + info + ", attesa " + expectedVersion + " (pacchetto sbagliato o preso da un'altra sorgente)");
            }
        }

        // Stampa la versione dell'assembly che contiene il tipo (per le dipendenze con floor 8.0.x su net48).
        public static void PrintDependency(string label, Type type)
        {
            var assembly = type.Assembly;
            Console.WriteLine("  " + label + ": " + assembly.GetName().Name + " " + assembly.GetName().Version + " (" + InformationalVersion(assembly) + ") " + assembly.Location);
        }

        // Il server finto con i corpi catturati dal server vero, byte per byte.
        public static FakeServer StartServer(string fixtures)
        {
            var server = new FakeServer();
            server.Respond("GET", "/tenant", 200, File.ReadAllBytes(Path.Combine(fixtures, TenantFixture)));
            server.Respond("POST", "/documents", 201, File.ReadAllBytes(Path.Combine(fixtures, UploadFixture)));
            server.Respond("GET", "/documents", 200, File.ReadAllBytes(Path.Combine(fixtures, ListFixture)));
            return server;
        }

        // Opzioni con i setter (C# 7.3): nessun ritentativo, cosi' un errore si vede alla prima richiesta.
        public static FilemasterOptions Options(Uri baseAddress)
        {
            return new FilemasterOptions
            {
                BaseAddress = baseAddress,
                ApiKey = ApiKey,
                RequestTimeout = TimeSpan.FromSeconds(20),
                TransferTimeout = TimeSpan.FromMinutes(1),
                Retry = new FilemasterRetryOptions
                {
                    MaxAttempts = 1,
                    InitialDelay = TimeSpan.FromMilliseconds(100),
                    MaxDelay = TimeSpan.FromSeconds(1),
                },
            };
        }

        // Ente, caricamento con tutti i campi opzionali, elenco con tutti i filtri: risposte lette e richieste controllate sul filo.
        public static async Task RunAsync(IFilemasterClient client, FakeServer server, string label)
        {
            var first = server.Requests.Length;
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
            using (var metadata = JsonDocument.Parse("{\"arxivar\":{\"docnumber\":12345,\"categoria\":\"X\"}}"))
            using (var filter = JsonDocument.Parse("{\"arxivar\":{\"docnumber\":12345}}"))
            {
                var tenant = await client.Tenant.GetAsync(cancellation.Token).ConfigureAwait(false);
                Check(
                    tenant.Id.Value == "1" && tenant.Id.Number == 1 && tenant.Slug == "acme-test" && tenant.Name == "Acme Test" && tenant.Status == TenantStatus.Active,
                    "[" + label + "] GET /tenant letto male: " + tenant);

                var fileBytes = Encoding.ASCII.GetBytes("%PDF-1.4 pack smoke");
                UploadResult upload;
                using (var content = new MemoryStream(fileBytes))
                {
                    var request = new UploadDocumentRequest(content, "fattura.pdf")
                    {
                        ContentType = "application/pdf",
                        FolderId = new FolderCode("FATTURE"),
                        Owner = "maria",
                        Tag = "fattura",
                        Sender = "Acme Srl",
                        Recipient = "Beta Spa",
                        Metadata = metadata.RootElement,
                    };
                    upload = await client.Documents.UploadAsync(request, cancellation.Token).ConfigureAwait(false);
                }

                Check(
                    upload.Document.Id.Value == "30017" && upload.Document.Id.Number == 30017L && upload.Deduplicated && upload.Document.SizeBytes == 590
                        && upload.Document.OriginalFilename == "fattura.pdf" && upload.Document.HasContent,
                    "[" + label + "] risposta di POST /documents letta male: " + upload.Document.Id + " " + upload.Document.SizeBytes);

                var query = new DocumentQuery
                {
                    FolderId = new FolderCode("FATTURE"),
                    Owner = "maria",
                    Tag = "fattura",
                    FileName = "fattura",
                    Sender = "Acme Srl",
                    Recipient = "Beta Spa",
                    SenderId = new ContactId(ContactValue),
                    RecipientId = new ContactId(ContactValue),
                    Text = "fattura",
                    MetadataText = "X",
                    Metadata = filter.RootElement,
                    CreatedFrom = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
                    CreatedBefore = new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero),
                };
                var page = await client.Documents.ListAsync(query, new PageRequest(limit: 1), cancellation.Token).ConfigureAwait(false);
                Check(
                    page.Items.Count == 1 && page.Items[0].Id.Value == "30028" && page.NextCursor != null,
                    "[" + label + "] risposta di GET /documents letta male");
            }

            var errors = server.Errors;
            Check(errors.Length == 0, "[" + label + "] il server finto ha avuto errori: " + string.Join(" | ", errors));
            var requests = server.Requests.Skip(first).ToArray();
            Check(requests.Length == 3, "[" + label + "] attese 3 richieste al server, arrivate " + requests.Length);
            foreach (var request in requests)
            {
                Check(
                    request.Header("X-API-Key") == ApiKey,
                    "[" + label + "] " + request.Method + " " + request.Path + " senza l'intestazione X-API-Key giusta (ricevuta: " + (request.Header("X-API-Key") ?? "nessuna") + ")");
            }

            Check(requests[0].Method == "GET" && requests[0].Path == "/tenant", "[" + label + "] prima richiesta inattesa: " + requests[0].Method + " " + requests[0].Path);

            var upload1 = requests[1];
            var contentType = upload1.Header("Content-Type") ?? string.Empty;
            var body = Encoding.UTF8.GetString(upload1.Body);
            Check(upload1.Method == "POST" && upload1.Path == "/documents", "[" + label + "] seconda richiesta inattesa: " + upload1.Method + " " + upload1.Path);
            Check(contentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase), "[" + label + "] upload con Content-Type " + contentType);
            Check(
                body.Contains("name=\"file\"; filename=\"fattura.pdf\"") && body.Contains("%PDF-1.4 pack smoke") && body.Contains("application/pdf"),
                "[" + label + "] l'upload non contiene la parte file attesa");
            Check(body.Contains("name=\"owner\"") && body.Contains("maria") && body.Contains("name=\"metadata\""), "[" + label + "] l'upload non contiene i campi opzionali");

            var list = requests[2];
            Check(list.Method == "GET" && list.Path == "/documents", "[" + label + "] terza richiesta inattesa: " + list.Method + " " + list.Path);
            foreach (var expected in new[] { "folder_id=FATTURE", "owner=maria", "sender_id=" + ContactValue, "created_from=", "created_to=", "limit=1" })
            {
                Check(list.Query.Contains(expected), "[" + label + "] la query di GET /documents non contiene " + expected + ": " + list.Query);
            }

            Console.WriteLine("OK [" + label + "] GET /tenant, POST /documents (multipart, X-API-Key), GET /documents con filtri");
        }

        private static string InformationalVersion(Assembly assembly)
        {
            var attribute = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            return attribute == null ? "(nessuna InformationalVersion)" : attribute.InformationalVersion;
        }
    }
}
