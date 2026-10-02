using Filemaster.Domain;
using Filemaster.Infrastructure;

namespace Filemaster.UnitTests.Wire;

/// <summary>
/// Le cartelle lette dalle risposte catturate dal server <c>dev</c> (fixture 21, 22, 24, 26, 28, 44, 215): i valori attesi sono quelli scritti
/// nei file, ricopiati a mano. Poi le varianti: un campo mancante, di tipo sbagliato, un codice non valido, una proprieta' in piu', le date.
/// </summary>
public sealed class FolderWireTests
{
    private static Folder Read(byte[] body) => FolderWire.ReadFolder(body, WireTest.Context());

    private static IReadOnlyList<Folder> ReadList(byte[] body) => FolderWire.ReadFolders(body, WireTest.Context());

    // ----- fixture catturate -----

    [Fact]
    public void A_top_level_folder_is_read_from_the_captured_creation_response()
    {
        // 21-folders-create-parent: {"id":"FATTURE","name":"Fatture","created_at":"2026-10-01T09:59:12.9531Z"}
        var folder = Read(WireFixtures.Captured("21-folders-create-parent"));

        Assert.Equal(new FolderCode("FATTURE"), folder.Id);
        Assert.Null(folder.ParentId);
        Assert.Equal("Fatture", folder.Name);
        Assert.Equal(WireTest.Utc(2026, 10, 1, 9, 59, 12, 9531000), folder.CreatedAt);
        Assert.Equal(TimeSpan.Zero, folder.CreatedAt.Offset);
    }

    [Fact]
    public void A_child_folder_has_its_parent_code()
    {
        // 22-folders-create-child: {"id":"FATTURE.2026","parent_id":"FATTURE","name":"Fatture 2026","created_at":"2026-10-01T09:59:13.109706Z"}
        var folder = Read(WireFixtures.Captured("22-folders-create-child"));

        Assert.Equal(new FolderCode("FATTURE.2026"), folder.Id);
        Assert.Equal(new FolderCode("FATTURE"), folder.ParentId);
        Assert.Equal("Fatture 2026", folder.Name);
        Assert.Equal(WireTest.Utc(2026, 10, 1, 9, 59, 13, 1097060), folder.CreatedAt);
    }

    [Fact]
    public void The_renamed_and_the_recoded_folders_are_read_from_the_captured_update_responses()
    {
        var renamed = Read(WireFixtures.Captured("28-folders-patch-name"));
        var recoded = Read(WireFixtures.Captured("44-folders-patch-code"));

        Assert.Equal(new FolderCode("FATTURE.2026"), renamed.Id);
        Assert.Equal("Fatture 2026 rinominata", renamed.Name);
        Assert.Equal(new FolderCode("FATTURE"), renamed.ParentId);
        Assert.Equal(new FolderCode("FATTURE.2027"), recoded.Id);
        Assert.Equal("Fatture 2027", recoded.Name);
        Assert.Equal(new FolderCode("FATTURE"), recoded.ParentId);
        Assert.Equal(WireTest.Utc(2026, 10, 1, 9, 59, 13, 1097060), recoded.CreatedAt); // la data di creazione non cambia
    }

    [Fact]
    public void The_captured_root_listing_has_two_folders_in_the_server_order()
    {
        // 24-folders-list: CHARSET (09:59:13.192279Z), FATTURE (09:59:12.9531Z): l'ordine e' quello del server (per nome), non per data.
        var folders = ReadList(WireFixtures.Captured("24-folders-list"));

        Assert.Equal(2, folders.Count);
        Assert.Equal(new FolderCode("CHARSET"), folders[0].Id);
        Assert.Equal("Charset", folders[0].Name);
        Assert.Equal(WireTest.Utc(2026, 10, 1, 9, 59, 13, 1922790), folders[0].CreatedAt);
        Assert.Equal(new FolderCode("FATTURE"), folders[1].Id);
        Assert.All(folders, f => Assert.Null(f.ParentId));
    }

    [Fact]
    public void The_captured_children_listing_has_the_parent_on_every_folder()
    {
        // 26-folders-list-children: [FATTURE.2026 con parent FATTURE]
        var folders = ReadList(WireFixtures.Captured("26-folders-list-children"));

        var child = Assert.Single(folders);
        Assert.Equal(new FolderCode("FATTURE.2026"), child.Id);
        Assert.Equal(new FolderCode("FATTURE"), child.ParentId);
    }

    [Fact]
    public void The_captured_empty_listing_is_an_empty_list_and_not_an_error()
    {
        // 215-folders-list-after-delete: {"items":[]}
        var folders = ReadList(WireFixtures.Captured("215-folders-list-after-delete"));

        Assert.Empty(folders);
    }

    // ----- varianti di una fixture vera -----

    private static byte[] Child() => WireFixtures.Captured("22-folders-create-child");

    [Theory]
    [InlineData("id")]
    [InlineData("name")]
    [InlineData("created_at")]
    public void A_missing_required_field_is_not_interpretable(string field)
    {
        var exception = WireTest.Unexpected(() => Read(Variants.Without(Child(), field)));

        Assert.Contains(field, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("name")]
    [InlineData("created_at")]
    public void A_null_required_field_is_not_interpretable(string field)
    {
        WireTest.Unexpected(() => Read(Variants.With(Child(), field, "null")));
    }

    [Theory]
    [InlineData("id", "5")]
    [InlineData("id", "true")]
    [InlineData("name", "5")]
    [InlineData("name", "[\"x\"]")]
    [InlineData("created_at", "1790848753")]
    [InlineData("parent_id", "7")]
    public void A_field_of_the_wrong_type_is_not_interpretable(string field, string rawJson)
    {
        WireTest.Unexpected(() => Read(Variants.With(Child(), field, rawJson)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("-iniziale")]
    [InlineData(".iniziale")]
    [InlineData("con spazio")]
    [InlineData("con/slash")]
    [InlineData("accentata")]
    public void An_invalid_folder_code_in_a_response_is_not_interpretable(string code)
    {
        var bad = code == "accentata" ? "perch\U000000E9" : code;

        WireTest.Unexpected(() => Read(Variants.With(Child(), "id", "\"" + bad + "\"")));
        WireTest.Unexpected(() => Read(Variants.With(Child(), "parent_id", "\"" + bad + "\"")));
    }

    [Fact]
    public void A_code_of_51_characters_is_not_valid_and_one_of_50_is()
    {
        WireTest.Unexpected(() => Read(Variants.With(Child(), "id", "\"" + new string('A', 51) + "\"")));

        var folder = Read(Variants.With(Child(), "id", "\"" + new string('A', 50) + "\""));

        Assert.Equal(50, folder.Id.Value.Length);
    }

    [Fact]
    public void A_server_master_folder_id_with_the_fld_prefix_is_a_valid_code()
    {
        var folder = Read(Variants.With(Child(), "id", "\"fld_01M3VEESG5KBYR5PYAJ0TDT4B2\""));

        Assert.Equal("fld_01M3VEESG5KBYR5PYAJ0TDT4B2", folder.Id.Value);
    }

    [Fact]
    public void A_null_or_missing_parent_is_a_top_level_folder()
    {
        Assert.Null(Read(Variants.Without(Child(), "parent_id")).ParentId);
        Assert.Null(Read(Variants.With(Child(), "parent_id", "null")).ParentId);
    }

    [Fact]
    public void An_unknown_extra_property_is_ignored()
    {
        var folder = Read(Variants.With(Child(), "colore", "{\"r\":255}"));

        Assert.Equal(new FolderCode("FATTURE.2026"), folder.Id);
    }

    [Fact]
    public void A_date_without_an_offset_is_not_interpretable()
    {
        WireTest.Unexpected(() => Read(Variants.With(Child(), "created_at", "\"2026-10-01T09:59:13.109706\"")));
        WireTest.Unexpected(() => Read(Variants.With(Child(), "created_at", "\"2026-10-01\"")));
    }

    [Fact]
    public void A_date_in_the_year_9999_and_one_with_an_offset_are_read_exactly()
    {
        var far = Read(Variants.With(Child(), "created_at", "\"9999-12-31T23:59:59.9999999Z\""));
        var offset = Read(Variants.With(Child(), "created_at", "\"2026-10-01T11:59:13+02:00\""));

        Assert.Equal(WireTest.Utc(9999, 12, 31, 23, 59, 59, 9999999), far.CreatedAt);
        Assert.Equal(WireTest.Utc(2026, 10, 1, 9, 59, 13), offset.CreatedAt.ToUniversalTime());
    }

    [Fact]
    public void The_name_can_be_empty_non_ASCII_and_very_long()
    {
        Assert.Equal(string.Empty, Read(Variants.With(Child(), "name", "\"\"")).Name);
        Assert.Equal("Societ\U000000E0 \U0001F4C1", Read(Variants.With(Child(), "name", "\"Societ\U000000E0 \U0001F4C1\"")).Name);
        Assert.Equal(4096, Read(Variants.With(Child(), "name", "\"" + new string('n', 4096) + "\"")).Name.Length);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"x\"")]
    [InlineData("5")]
    public void A_body_that_is_not_an_object_is_not_interpretable(string body)
    {
        WireTest.Unexpected(() => Read(WireTest.Utf8(body)));
        WireTest.Unexpected(() => ReadList(WireTest.Utf8(body)));
    }

    // ----- elenco -----

    [Fact]
    public void A_listing_without_items_or_with_items_of_the_wrong_shape_is_not_interpretable()
    {
        WireTest.Unexpected(() => ReadList(WireTest.Utf8("{}")));
        WireTest.Unexpected(() => ReadList(WireTest.Utf8("{\"items\":null}")));
        WireTest.Unexpected(() => ReadList(WireTest.Utf8("{\"items\":{}}")));
        WireTest.Unexpected(() => ReadList(WireTest.Utf8("{\"items\":[null]}")));
        WireTest.Unexpected(() => ReadList(WireTest.Utf8("{\"items\":[\"FATTURE\"]}")));
    }

    [Fact]
    public void One_bad_folder_in_a_listing_fails_the_whole_listing_and_names_the_element()
    {
        var body = Variants.EditItem(WireFixtures.Captured("24-folders-list"), 1, item => item.Remove("name"));

        var exception = WireTest.Unexpected(() => ReadList(body));

        Assert.Contains("items[1].name", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_next_cursor_in_a_non_paginated_listing_is_ignored()
    {
        var body = Variants.With(WireFixtures.Captured("24-folders-list"), "next_cursor", "\"abc\"");

        Assert.Equal(2, ReadList(body).Count);
    }
}
