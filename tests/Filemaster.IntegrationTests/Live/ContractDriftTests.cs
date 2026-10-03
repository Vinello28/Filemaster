using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Filemaster.Application;
using Filemaster.Domain;
using static Filemaster.IntegrationTests.Live.LiveServer;

namespace Filemaster.IntegrationTests.Live;

/// <summary>
/// La forma GREZZA delle risposte del server, letta con un <see cref="HttpClient"/> nudo (senza il client Filemaster): se il
/// server aggiunge, toglie o rinomina un campo, cambia il formato delle date o degli errori, questi test lo dicono prima che il
/// livello wire (scritto sulle catture @8aec8bb) cominci a sbagliare in silenzio. Le liste di campi sono quelle che il client
/// legge (Infrastructure/Wire) per un oggetto con tutti i campi valorizzati: i null il server li omette.
/// </summary>
[Trait("Category", "Live")]
public sealed class ContractDriftTests
{
    // UTC con 'Z' e da 0 a 6 decimali con gli zeri finali tagliati (lezioni T0.3).
    private static readonly Regex Timestamp = new(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,6})?Z$", RegexOptions.CultureInvariant);

    private static readonly string[] TenantFields = { "created_at", "id", "name", "slug", "status" };

    private static readonly string[] DocumentFields =
    {
        "contacts", "created_at", "folder_id", "has_content", "id", "metadata", "mime_type", "original_filename", "owner", "recipient",
        "sender", "sha256", "size_bytes", "tag",
    };

    // Il contatto esterno di seed.sh: tutti i campi del ContactDto @8aec8bb tranne fax, mobile, ipa_code e office_code (non seminati).
    private static readonly string[] ContactListFields =
    {
        "address", "category_id", "city", "code", "country", "created_at", "documents_as_recipient", "documents_as_sender", "email",
        "id", "id_arxivar", "kind", "name", "notes", "pec", "phone", "postal_code", "province", "tax_code", "vat_number",
    };

    private static readonly string[] ProblemFields = { "detail", "request_id", "status", "title", "type" };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GET_tenant_has_exactly_the_fields_the_client_reads()
    {
        using var http = Http(ReadKey);

        using var response = await http.GetAsync(new Uri("tenant", UriKind.Relative), Ct);
        using var json = await JsonBody(response, HttpStatusCode.OK);

        Assert.Equal(TenantFields, Names(json.RootElement));
        Assert.Equal("active", json.RootElement.GetProperty("status").GetString());
        Assert.Matches(Timestamp, json.RootElement.GetProperty("created_at").GetString()!);
    }

    [Fact]
    public async Task A_document_has_exactly_the_fields_the_client_reads_and_upload_adds_deduplicated()
    {
        using var client = Client();
        using var http = Http(WriteKey);
        await LiveCleanup.WithCleanupAsync(client, async cleanup =>
        {
            var folder = UniqueFolder("DRIFT");
            await client.Folders.CreateAsync(new CreateFolderRequest(folder, "Deriva " + RunId), Ct);
            cleanup.Folder(folder);
            var uploaded = await UploadAsync(client, cleanup, UniqueBytes(700), "deriva.pdf", r =>
            {
                r.FolderId = folder;
                r.Owner = "o";
                r.Tag = "t";
                r.Sender = "s";
                r.Recipient = "r";
            });

            using var response = await http.GetAsync(new Uri("documents/" + uploaded.Document.Id.Value, UriKind.Relative), Ct);
            using var json = await JsonBody(response, HttpStatusCode.OK);

            Assert.Equal(DocumentFields, Names(json.RootElement));
            Assert.Equal(JsonValueKind.Array, json.RootElement.GetProperty("contacts").ValueKind);
            Assert.Equal(JsonValueKind.Object, json.RootElement.GetProperty("metadata").ValueKind);
            Assert.Matches(Timestamp, json.RootElement.GetProperty("created_at").GetString()!);
            Assert.Matches("^[0-9a-f]{64}$", json.RootElement.GetProperty("sha256").GetString()!);

            // L'elenco omette "contacts"; un documento senza cartella omette "folder_id" (null omessi).
            using var list = await http.GetAsync(new Uri("documents?owner=o&tag=t&limit=200", UriKind.Relative), Ct);
            using var page = await JsonBody(list, HttpStatusCode.OK);
            var item = Assert.Single(page.RootElement.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetString() == uploaded.Document.Id.Value);
            Assert.Equal(DocumentFields.Where(f => f != "contacts"), Names(item));

            // Upload grezzo: 201, senza Location, "deduplicated" al posto di "contacts".
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(UniqueBytes(300));
            file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            form.Add(file, "file", "grezzo.pdf");
            using var created = await http.PostAsync(new Uri("documents", UriKind.Relative), form, Ct);
            using var body = await JsonBody(created, HttpStatusCode.Created);
            cleanup.Document(new DocumentId(body.RootElement.GetProperty("id").GetString()!));
            Assert.Null(created.Headers.Location);
            Assert.Equal(
                new[] { "created_at", "deduplicated", "has_content", "id", "metadata", "mime_type", "original_filename", "sha256", "size_bytes" },
                Names(body.RootElement));
        });
    }

    [Fact]
    public async Task A_listed_contact_has_exactly_the_fields_the_client_reads()
    {
        using var http = Http(ReadKey);

        using var response = await http.GetAsync(new Uri("contacts?q=" + LiveSeed.ExternalCode, UriKind.Relative), Ct);
        using var json = await JsonBody(response, HttpStatusCode.OK);

        var contact = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(ContactListFields, Names(contact));
        Assert.Equal("external", contact.GetProperty("kind").GetString());
        Assert.StartsWith("con_", contact.GetProperty("id").GetString(), StringComparison.Ordinal);

        using var categories = await http.GetAsync(new Uri("contact-categories", UriKind.Relative), Ct);
        using var list = await JsonBody(categories, HttpStatusCode.OK);
        Assert.Equal(new[] { "items" }, Names(list.RootElement));
        var category = Assert.Single(list.RootElement.GetProperty("items").EnumerateArray(), c => c.GetProperty("id").GetString() == LiveSeed.CategoryId);
        Assert.Equal(new[] { "id", "id_arxivar", "name" }, Names(category));
    }

    [Fact]
    public async Task An_error_is_problem_json_with_the_request_id_in_the_body_and_not_in_the_headers()
    {
        using var http = Http(ReadKey);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("documents/doc_00000000000000000000000000", UriKind.Relative));
        request.Headers.Add("X-Request-ID", "e2edrift0123456789abcdef01234567");

        using var response = await http.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.False(response.Headers.Contains("X-Request-ID"));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(ProblemFields, Names(json.RootElement));
        Assert.Equal("/problems/not-found", json.RootElement.GetProperty("type").GetString());
        Assert.Equal(404, json.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("e2edrift0123456789abcdef01234567", json.RootElement.GetProperty("request_id").GetString());
    }

    [Fact]
    public async Task DELETE_and_POST_verify_with_an_empty_body_and_Content_Length_0_are_accepted()
    {
        using var client = Client();
        using var http = Http(WriteKey);
        await LiveCleanup.WithCleanupAsync(client, async cleanup =>
        {
            var uploaded = await UploadAsync(client, cleanup, UniqueBytes(400), "vuoto.pdf");
            var path = "documents/" + uploaded.Document.Id.Value;

            using var verify = new HttpRequestMessage(HttpMethod.Post, new Uri(path + "/verify", UriKind.Relative)) { Content = new ByteArrayContent(Array.Empty<byte>()) };
            Assert.Equal(0, verify.Content.Headers.ContentLength);
            using var verified = await http.SendAsync(verify, Ct);
            using var check = await JsonBody(verified, HttpStatusCode.OK);
            Assert.True(check.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(new[] { "checked_at", "document_id", "ok", "sha256" }, Names(check.RootElement));

            using var delete = new HttpRequestMessage(HttpMethod.Delete, new Uri(path, UriKind.Relative)) { Content = new ByteArrayContent(Array.Empty<byte>()) };
            Assert.Equal(0, delete.Content.Headers.ContentLength);
            using var deleted = await http.SendAsync(delete, Ct);
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        });
    }

    [Fact]
    public async Task A_partial_download_has_the_headers_the_client_reads()
    {
        using var client = Client();
        using var http = Http(ReadKey);
        await LiveCleanup.WithCleanupAsync(client, async cleanup =>
        {
            var bytes = UniqueBytes(1_000);
            var uploaded = await UploadAsync(client, cleanup, bytes, "intestazioni.pdf");

            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("documents/" + uploaded.Document.Id.Value + "/content", UriKind.Relative));
            request.Headers.Range = new RangeHeaderValue(0, 9);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);

            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            Assert.Contains("bytes", response.Headers.AcceptRanges);
            Assert.Equal(10, response.Content.Headers.ContentLength);
            Assert.Equal(0, response.Content.Headers.ContentRange?.From);
            Assert.Equal(9, response.Content.Headers.ContentRange?.To);
            Assert.Equal(bytes.Length, response.Content.Headers.ContentRange?.Length);
            Assert.NotNull(response.Content.Headers.LastModified);
            Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
            Assert.Equal("intestazioni.pdf", response.Content.Headers.ContentDisposition?.FileNameStar);
            Assert.Null(response.Headers.ETag);
        });
    }

    /// <summary>Un <see cref="HttpClient"/> nudo verso il server: niente redirect (un 3xx sarebbe una deriva), chiave nell'intestazione.</summary>
    private static HttpClient Http(string apiKey)
    {
#pragma warning disable CA2000 // Il gestore lo smaltisce l'HttpClient (disposeHandler: true).
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }, disposeHandler: true)
        {
            BaseAddress = Url,
            Timeout = TimeSpan.FromSeconds(30),
        };
#pragma warning restore CA2000
        http.DefaultRequestHeaders.Add("X-API-Key", apiKey);
        return http;
    }

    private static async Task<JsonDocument> JsonBody(HttpResponseMessage response, HttpStatusCode expected)
    {
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == expected, "HTTP " + (int)response.StatusCode + " invece di " + (int)expected + ": " + text);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        return JsonDocument.Parse(text);
    }

    private static string[] Names(JsonElement element) =>
        element.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
}
