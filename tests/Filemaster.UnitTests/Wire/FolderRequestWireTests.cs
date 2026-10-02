using System.Text;
using System.Text.Json;
using Filemaster.Application;
using Filemaster.Domain;
using Filemaster.Infrastructure;

namespace Filemaster.UnitTests.Wire;

/// <summary>
/// Le richieste che riguardano le cartelle: i corpi di creazione (<c>POST /folders</c>) e di modifica (<c>PATCH /folders/{id}</c>) e il percorso
/// dell'elenco dei figli. Il testo atteso e' quello dei corpi che il server ha accettato nelle catture 21, 22, 28 e 44; il server rifiuta le
/// proprieta' sconosciute, quindi ogni test guarda anche i NOMI delle proprieta'. Il nuovo codice sul filo si chiama <c>id</c>.
/// </summary>
public sealed class FolderRequestWireTests
{
    private static string Text(byte[] body) => new UTF8Encoding(false, true).GetString(body);

    private static string[] Names(byte[] body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
    }

    // ----- creazione -----

    [Fact]
    public void The_create_bodies_are_exactly_the_ones_the_server_accepted_in_the_captures()
    {
        // 21: {"id":"FATTURE","name":"Fatture"}; 22: {"id":"FATTURE.2026","parent_id":"FATTURE","name":"Fatture 2026"}
        var top = new CreateFolderRequest(new FolderCode("FATTURE"), "Fatture");
        var child = new CreateFolderRequest(new FolderCode("FATTURE.2026"), "Fatture 2026") { ParentId = new FolderCode("FATTURE") };

        Assert.Equal(WireFixtures.RequestBody("21-folders-create-parent"), Text(FolderWire.CreateBody(top)));
        Assert.Equal(WireFixtures.RequestBody("22-folders-create-child"), Text(FolderWire.CreateBody(child)));
        Assert.Equal("{\"id\":\"FATTURE\",\"name\":\"Fatture\"}", Text(FolderWire.CreateBody(top)));
        Assert.Equal("{\"id\":\"FATTURE.2026\",\"parent_id\":\"FATTURE\",\"name\":\"Fatture 2026\"}", Text(FolderWire.CreateBody(child)));
    }

    [Fact]
    public void The_create_body_has_only_the_properties_the_server_knows_and_no_parent_when_there_is_none()
    {
        var top = new CreateFolderRequest(new FolderCode("A"), "n");
        var child = new CreateFolderRequest(new FolderCode("B"), "n") { ParentId = new FolderCode("A") };

        Assert.Equal(new[] { "id", "name" }, Names(FolderWire.CreateBody(top)));
        Assert.Equal(new[] { "id", "parent_id", "name" }, Names(FolderWire.CreateBody(child)));
    }

    [Fact]
    public void The_name_is_sent_as_it_is_without_trimming_and_survives_the_round_trip()
    {
        const string Name = "  Fatture \"2026\" \\ / <>&'+ perch\U000000E9 \U0001F600  ";

        var body = FolderWire.CreateBody(new CreateFolderRequest(new FolderCode("A"), Name));

        using var document = JsonDocument.Parse(body);
        Assert.Equal(Name, document.RootElement.GetProperty("name").GetString());
        Assert.Equal("A", document.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public void The_body_is_compact_UTF8_without_a_byte_order_mark_and_does_not_escape_what_it_need_not()
    {
        var body = FolderWire.CreateBody(new CreateFolderRequest(new FolderCode("A"), "caff\U000000E8 + ' <b>"));

        Assert.NotEqual(0xEF, body[0]);
        Assert.Equal((byte)'{', body[0]);
        Assert.Equal("{\"id\":\"A\",\"name\":\"caff\U000000E8 + ' <b>\"}", Text(body));
    }

    [Fact]
    public void A_name_of_255_characters_is_sent_whole_and_an_invalid_request_is_refused_before_writing()
    {
        var long255 = new string('n', 255);
        Assert.Equal(255, JsonDocument.Parse(FolderWire.CreateBody(new CreateFolderRequest(new FolderCode("A"), long255))).RootElement.GetProperty("name").GetString()!.Length);

        Assert.Equal("Name", Assert.Throws<ArgumentException>(() => FolderWire.CreateBody(new CreateFolderRequest(new FolderCode("A"), new string('n', 256)))).ParamName);
        Assert.Equal("Name", Assert.Throws<ArgumentException>(() => FolderWire.CreateBody(new CreateFolderRequest(new FolderCode("A"), "   "))).ParamName);
        Assert.Equal("Code", Assert.Throws<ArgumentException>(() => FolderWire.CreateBody(new CreateFolderRequest(default, "n"))).ParamName);
        Assert.Equal("ParentId", Assert.Throws<ArgumentException>(() => FolderWire.CreateBody(new CreateFolderRequest(new FolderCode("A"), "n") { ParentId = default(FolderCode) })).ParamName);
        Assert.Throws<ArgumentNullException>(() => FolderWire.CreateBody(null!));
    }

    [Fact]
    public void A_lone_surrogate_in_a_folder_name_is_refused_with_the_property_name()
    {
        var bad = "a" + new string((char)0xD800, 1);

        Assert.Equal("Name", Assert.Throws<ArgumentException>(() => FolderWire.CreateBody(new CreateFolderRequest(new FolderCode("A"), bad))).ParamName);
        Assert.Equal("Name", Assert.Throws<ArgumentException>(() => FolderWire.UpdateBody(new UpdateFolderRequest { Name = bad })).ParamName);
    }

    // ----- modifica -----

    [Fact]
    public void The_update_bodies_are_exactly_the_ones_the_server_accepted_in_the_captures()
    {
        // 28: {"name":"Fatture 2026 rinominata"}; 44: {"id":"FATTURE.2027","name":"Fatture 2027"}
        var rename = new UpdateFolderRequest { Name = "Fatture 2026 rinominata" };
        var recode = new UpdateFolderRequest { NewCode = new FolderCode("FATTURE.2027"), Name = "Fatture 2027" };

        Assert.Equal(WireFixtures.RequestBody("28-folders-patch-name"), Text(FolderWire.UpdateBody(rename)));
        Assert.Equal(WireFixtures.RequestBody("44-folders-patch-code"), Text(FolderWire.UpdateBody(recode)));
        Assert.Equal("{\"name\":\"Fatture 2026 rinominata\"}", Text(FolderWire.UpdateBody(rename)));
        Assert.Equal("{\"id\":\"FATTURE.2027\",\"name\":\"Fatture 2027\"}", Text(FolderWire.UpdateBody(recode)));
    }

    [Fact]
    public void The_new_code_is_called_id_on_the_wire_and_only_the_set_fields_are_written()
    {
        var codeOnly = FolderWire.UpdateBody(new UpdateFolderRequest { NewCode = new FolderCode("NUOVO") });
        var nameOnly = FolderWire.UpdateBody(new UpdateFolderRequest { Name = "Nuovo" });

        Assert.Equal("{\"id\":\"NUOVO\"}", Text(codeOnly));
        Assert.Equal("{\"name\":\"Nuovo\"}", Text(nameOnly));
        Assert.Equal(new[] { "id" }, Names(codeOnly));
        Assert.Equal(new[] { "name" }, Names(nameOnly));
        Assert.DoesNotContain("new_code", Text(codeOnly), StringComparison.Ordinal);
        Assert.DoesNotContain("null", Text(codeOnly), StringComparison.Ordinal);
    }

    [Fact]
    public void An_invalid_update_is_refused_before_writing()
    {
        var none = Assert.Throws<ArgumentException>(() => FolderWire.UpdateBody(new UpdateFolderRequest()));
        Assert.Null(none.ParamName);
        Assert.Equal("Name", Assert.Throws<ArgumentException>(() => FolderWire.UpdateBody(new UpdateFolderRequest { Name = string.Empty })).ParamName);
        Assert.Equal("Name", Assert.Throws<ArgumentException>(() => FolderWire.UpdateBody(new UpdateFolderRequest { Name = new string('n', 256) })).ParamName);
        Assert.Equal("NewCode", Assert.Throws<ArgumentException>(() => FolderWire.UpdateBody(new UpdateFolderRequest { NewCode = default(FolderCode) })).ParamName);
        Assert.Throws<ArgumentNullException>(() => FolderWire.UpdateBody(null!));
    }

    [Fact]
    public void The_update_name_is_sent_as_it_is()
    {
        var body = FolderWire.UpdateBody(new UpdateFolderRequest { Name = "  Nome \U0001F600 " });

        using var document = JsonDocument.Parse(body);
        Assert.Equal("  Nome \U0001F600 ", document.RootElement.GetProperty("name").GetString());
    }

    // ----- elenco dei figli -----

    [Fact]
    public void The_listing_path_is_folders_for_the_top_level_and_has_parent_id_for_the_children()
    {
        // 24: GET /folders; 26: GET /folders?parent_id=FATTURE
        Assert.Equal("folders", FolderWire.ListPath(null));
        Assert.Equal("folders?parent_id=FATTURE", FolderWire.ListPath(new FolderCode("FATTURE")));
        Assert.Equal(WireFixtures.RequestPath("24-folders-list").TrimStart('/'), FolderWire.ListPath(null));
        Assert.Equal(WireFixtures.RequestPath("26-folders-list-children").TrimStart('/'), FolderWire.ListPath(new FolderCode("FATTURE")));
        Assert.Equal("folders?parent_id=FATTURE.2026", FolderWire.ListPath(new FolderCode("FATTURE.2026")));
    }

    [Fact]
    public void An_empty_parent_is_refused_and_the_top_level_is_null()
    {
        var exception = Assert.Throws<ArgumentException>(() => FolderWire.ListPath(default(FolderCode)));

        Assert.Equal("parent", exception.ParamName);
    }

    [Fact]
    public void The_listing_has_no_page_parameters_because_it_is_not_paginated()
    {
        Assert.DoesNotContain("limit", FolderWire.ListPath(new FolderCode("A")), StringComparison.Ordinal);
        Assert.DoesNotContain("cursor", FolderWire.ListPath(new FolderCode("A")), StringComparison.Ordinal);
    }
}
