using Filemaster.Domain;
using Filemaster.Infrastructure;

namespace Filemaster.UnitTests.Wire;

/// <summary>
/// I percorsi del server: relativi all'indirizzo base e senza barra iniziale (<c>TransportRequest</c> li rifiuta altrimenti), con gli id
/// gia' validati. Un id o un codice vuoto (<c>default</c>) costruirebbe <c>documents/</c> e viene rifiutato. I testi attesi sono scritti a mano
/// e confrontati con i percorsi delle richieste catturate.
/// </summary>
public sealed class RoutesTests
{
    private const string Doc = "doc_01M3VEESG5KBYR5PYAJ0TDT4B2";

    [Fact]
    public void The_fixed_routes_have_the_exact_text_and_no_leading_slash()
    {
        Assert.Equal("documents", Routes.Documents);
        Assert.Equal("documents/bulk/move", Routes.DocumentsBulkMove);
        Assert.Equal("documents/bulk/verify", Routes.DocumentsBulkVerify);
        Assert.Equal("folders", Routes.Folders);
        Assert.Equal("contacts", Routes.Contacts);
        Assert.Equal("contact-categories", Routes.ContactCategories);
        Assert.Equal("tenant", Routes.Tenant);
        Assert.Equal("healthz", Routes.Healthz);
        Assert.Equal("readyz", Routes.Readyz);
    }

    [Fact]
    public void The_document_routes_have_the_exact_text()
    {
        var id = new DocumentId(Doc);

        Assert.Equal("documents/" + Doc, Routes.Document(id));
        Assert.Equal("documents/" + Doc + "/content", Routes.DocumentContent(id));
        Assert.Equal("documents/" + Doc + "/preview", Routes.DocumentPreview(id));
        Assert.Equal("documents/" + Doc + "/verify", Routes.DocumentVerify(id));
        Assert.Equal("documents/" + Doc + "/folder", Routes.DocumentFolder(id));
    }

    [Fact]
    public void The_routes_match_the_paths_of_the_captured_requests()
    {
        var id = new DocumentId(Doc);

        Assert.Equal(WireFixtures.RequestPath("98-doc-get"), "/" + Routes.Document(id));
        Assert.Equal(WireFixtures.RequestPath("108-doc-content"), "/" + Routes.DocumentContent(id));
        Assert.Equal(WireFixtures.RequestPath("121-doc-preview-pdf"), "/" + Routes.DocumentPreview(id));
        Assert.Equal(WireFixtures.RequestPath("126-doc-verify"), "/" + Routes.DocumentVerify(id));
        Assert.Equal(WireFixtures.RequestPath("136-doc-move-to-folder"), "/" + Routes.DocumentFolder(id));
        Assert.Equal(WireFixtures.RequestPath("28-folders-patch-name"), "/" + Routes.Folder(new FolderCode("FATTURE.2026")));
        Assert.Equal("/" + Routes.DocumentsBulkVerify, WireFixtures.RequestPath("129-docs-bulk-verify"));
        Assert.Equal("/" + Routes.DocumentsBulkMove, WireFixtures.RequestPath("133-docs-bulk-move"));
        Assert.Equal("/" + Routes.Tenant, WireFixtures.RequestPath("03-tenant"));
        Assert.Equal("/" + Routes.Healthz, WireFixtures.RequestPath("01-healthz"));
        Assert.Equal("/" + Routes.Readyz, WireFixtures.RequestPath("02-readyz"));
        Assert.Equal("/" + Routes.ContactCategories, WireFixtures.RequestPath("151-contact-categories-list"));
    }

    [Fact]
    public void The_folder_and_contact_routes_have_the_exact_text()
    {
        Assert.Equal("folders/FATTURE", Routes.Folder(new FolderCode("FATTURE")));
        Assert.Equal("contacts/con_01M3VEF0K9Z8X7Y6W5V4T3S2R1", Routes.Contact(new ContactId("con_01M3VEF0K9Z8X7Y6W5V4T3S2R1")));
    }

    [Theory]
    [InlineData("A")]
    [InlineData("a.b-c_d")]
    [InlineData("2026.09-fatture_v2")]
    [InlineData("fld_01M3VEESG5KBYR5PYAJ0TDT4B2")]
    public void A_valid_folder_code_goes_into_the_path_unchanged_because_every_character_is_path_safe(string code)
    {
        Assert.Equal("folders/" + code, Routes.Folder(new FolderCode(code)));
    }

    [Fact]
    public void An_empty_id_or_code_is_refused_with_the_name_of_the_argument()
    {
        var document = Assert.Throws<ArgumentException>(() => Routes.Document(default));
        var content = Assert.Throws<ArgumentException>(() => Routes.DocumentContent(default));
        var preview = Assert.Throws<ArgumentException>(() => Routes.DocumentPreview(default));
        var verify = Assert.Throws<ArgumentException>(() => Routes.DocumentVerify(default));
        var folder = Assert.Throws<ArgumentException>(() => Routes.DocumentFolder(default));
        var code = Assert.Throws<ArgumentException>(() => Routes.Folder(default));
        var contact = Assert.Throws<ArgumentException>(() => Routes.Contact(default));

        Assert.All(new[] { document, content, preview, verify, folder, contact }, e => Assert.Equal("id", e.ParamName));
        Assert.Equal("code", code.ParamName);
    }

    [Fact]
    public void Every_route_is_accepted_by_the_transport_as_a_relative_path()
    {
        var paths = new[]
        {
            Routes.Documents, Routes.DocumentsBulkMove, Routes.DocumentsBulkVerify, Routes.Folders, Routes.Contacts, Routes.ContactCategories, Routes.Tenant,
            Routes.Healthz, Routes.Readyz, Routes.Document(new DocumentId(Doc)), Routes.DocumentContent(new DocumentId(Doc)), Routes.Folder(new FolderCode("A.b")),
            Routes.Contact(new ContactId("con_01M3VEF0K9Z8X7Y6W5V4T3S2R1")),
        };

        foreach (var path in paths)
        {
            var request = new TransportRequest(HttpMethod.Get, path);
            Assert.Equal(path, request.RelativeUri);
        }
    }

    [Fact]
    public void A_route_keeps_the_path_prefix_of_the_base_address()
    {
        // La barra iniziale scarterebbe /api/: i percorsi non l'hanno.
        var combined = new Uri(new Uri("https://files.example.test/api/"), Routes.Document(new DocumentId(Doc)));

        Assert.Equal("/api/documents/" + Doc, combined.AbsolutePath);
    }
}
