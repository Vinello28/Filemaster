using Filemaster.Application;
using Filemaster.Domain;
using Filemaster.Infrastructure;

namespace Filemaster.UnitTests.Wire;

/// <summary>
/// L'anagrafica. <b>Nessuna cattura contiene un contatto o una categoria</b>: il server di prova aveva l'anagrafica vuota (fixture 150, 151,
/// 152: <c>{"items":[]}</c>), quindi le forme di <see cref="Contact"/> e <see cref="ContactCategory"/> vengono da fixture DERIVATE dal codice del
/// server (<c>ContactDto</c>, <c>ContactCategoryDto</c>, <c>Wire&lt;ContactKind&gt;</c>: snake_case, null omessi) e sono marcate cosi' nel README.
/// Le catture vere vanno a sostituirle (T6.3).
/// </summary>
public sealed class ContactWireTests
{
    private static readonly ContactId Acme = new("con_01M3VEF0K9Z8X7Y6W5V4T3S2R1");
    private static readonly ContactId Mario = new("con_01M3VEF0P1Q2R3S4T5V6W7X8Y9");

    private static Contact Read(byte[] body) => ContactWire.ReadContact(body, WireTest.Context());

    private static Page<Contact> ReadPage(byte[] body) => ContactWire.ReadPage(body, WireTest.Context());

    private static IReadOnlyList<ContactCategory> ReadCategories(byte[] body) => ContactWire.ReadCategories(body, WireTest.Context());

    // ----- catture vere (vuote) -----

    [Fact]
    public void The_captured_empty_directory_is_an_empty_page_without_a_cursor()
    {
        // 150-contacts-list e 152-contacts-list-q (?q=acme): {"items":[]}
        Assert.Empty(ReadPage(WireFixtures.Captured("150-contacts-list")).Items);
        Assert.Null(ReadPage(WireFixtures.Captured("150-contacts-list")).NextCursor);
        Assert.Empty(ReadPage(WireFixtures.Captured("152-contacts-list-q")).Items);
    }

    [Fact]
    public void The_captured_empty_categories_are_an_empty_list()
    {
        // 151-contact-categories-list: {"items":[]}
        Assert.Empty(ReadCategories(WireFixtures.Captured("151-contact-categories-list")));
    }

    // ----- fixture derivate -----

    [Fact]
    public void The_derived_listing_has_a_full_contact_with_every_field_and_the_two_counters()
    {
        var page = ReadPage(WireFixtures.Derived("contacts-page"));
        var acme = page.Items[0];

        Assert.Equal(2, page.Items.Count);
        Assert.Equal("djF8TWFyaW8gUm9zc2l8Y29uXzAxTTNWRUYwUDFRMlIzUzRUNVY2VzdYOFk5", page.NextCursor);
        Assert.Equal(Acme, acme.Id);
        Assert.Equal("Acme Srl", acme.Name);
        Assert.Equal(ContactKind.External, acme.Kind);
        Assert.Equal("CLIENTI", acme.CategoryId);
        Assert.Equal("C-001", acme.Code);
        Assert.Equal("Via Roma 1", acme.Address);
        Assert.Equal("20121", acme.PostalCode);
        Assert.Equal("Milano", acme.City);
        Assert.Equal("MI", acme.Province);
        Assert.Equal("IT", acme.Country);
        Assert.Equal("user@example.test", acme.Email);
        Assert.Equal("pec@example.test", acme.Pec);
        Assert.Equal("+39 02 0000000", acme.Phone);
        Assert.Equal("+39 02 0000001", acme.Fax);
        Assert.Equal("+39 333 0000000", acme.Mobile);
        Assert.Equal("IT00000000000", acme.VatNumber);
        Assert.Equal("00000000000", acme.TaxCode);
        Assert.Equal("UF0000", acme.IpaCode);
        Assert.Equal("0000000", acme.OfficeCode);
        Assert.Equal("Cliente di prova", acme.Notes);
        Assert.Equal(4021, acme.ArxivarId);
        Assert.Equal(WireTest.Utc(2026, 9, 29, 17, 41, 38, 1234560), acme.CreatedAt);
        Assert.Equal(3, acme.DocumentsAsSender);
        Assert.Equal(0, acme.DocumentsAsRecipient);
    }

    [Fact]
    public void A_minimal_listed_contact_has_only_the_required_fields_and_counters_of_zero_stay_zero()
    {
        var mario = ReadPage(WireFixtures.Derived("contacts-page")).Items[1];

        Assert.Equal(Mario, mario.Id);
        Assert.Equal("Mario Rossi", mario.Name);
        Assert.Equal(ContactKind.User, mario.Kind);
        Assert.Null(mario.CategoryId);
        Assert.Null(mario.Code);
        Assert.Null(mario.Address);
        Assert.Null(mario.PostalCode);
        Assert.Null(mario.City);
        Assert.Null(mario.Province);
        Assert.Null(mario.Country);
        Assert.Null(mario.Email);
        Assert.Null(mario.Pec);
        Assert.Null(mario.Phone);
        Assert.Null(mario.Fax);
        Assert.Null(mario.Mobile);
        Assert.Null(mario.VatNumber);
        Assert.Null(mario.TaxCode);
        Assert.Null(mario.IpaCode);
        Assert.Null(mario.OfficeCode);
        Assert.Null(mario.Notes);
        Assert.Null(mario.ArxivarId);
        Assert.Equal(WireTest.Utc(2026, 9, 29, 17, 42, 0), mario.CreatedAt);
        Assert.Equal(0, mario.DocumentsAsSender);
        Assert.Equal(12, mario.DocumentsAsRecipient);
    }

    [Fact]
    public void The_derived_detail_has_null_counters_and_not_zero()
    {
        // Il dettaglio non ha i contatori (Contact.DocumentsAsSender: "null, non zero").
        var contact = Read(WireFixtures.Derived("contact-detail"));

        Assert.Equal(Mario, contact.Id);
        Assert.Null(contact.DocumentsAsSender);
        Assert.Null(contact.DocumentsAsRecipient);
    }

    [Fact]
    public void The_derived_categories_are_read_in_order_with_the_optional_ARXivar_id()
    {
        var categories = ReadCategories(WireFixtures.Derived("contact-categories"));

        Assert.Equal(2, categories.Count);
        Assert.Equal(new ContactCategory("ARX-12", "Clienti ARXivar", 12), categories[0]);
        Assert.Equal(new ContactCategory("ASSOCIATI", "Associati", null), categories[1]);
    }

    // ----- varianti -----

    // Il primo contatto della pagina derivata (quello con tutti i campi), come oggetto singolo.
    private static byte[] Full() => PageItem(WireFixtures.Derived("contacts-page"));

    private static byte[] PageItem(byte[] page)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(page)!.AsObject();
        return WireTest.Utf8(node["items"]!.AsArray()[0]!.ToJsonString());
    }

    [Theory]
    [InlineData("id")]
    [InlineData("name")]
    [InlineData("created_at")]
    public void A_missing_required_field_is_not_interpretable_and_names_the_field(string field)
    {
        var exception = WireTest.Unexpected(() => Read(Variants.Without(Full(), field)));

        Assert.Contains(field, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("id", "5")]
    [InlineData("id", "\"con_abc\"")]
    [InlineData("id", "\"\"")]
    [InlineData("id", "\"doc_01M3VEF0K9Z8X7Y6W5V4T3S2R1\"")]
    [InlineData("name", "5")]
    [InlineData("name", "null")]
    [InlineData("created_at", "\"2026-09-29T17:41:38\"")]
    [InlineData("created_at", "17")]
    [InlineData("kind", "5")]
    [InlineData("kind", "true")]
    [InlineData("category_id", "5")]
    [InlineData("code", "[]")]
    [InlineData("address", "{}")]
    [InlineData("postal_code", "20121")]
    [InlineData("city", "1")]
    [InlineData("province", "1")]
    [InlineData("country", "1")]
    [InlineData("email", "1")]
    [InlineData("pec", "1")]
    [InlineData("phone", "39")]
    [InlineData("fax", "39")]
    [InlineData("mobile", "39")]
    [InlineData("vat_number", "123")]
    [InlineData("tax_code", "123")]
    [InlineData("ipa_code", "123")]
    [InlineData("office_code", "123")]
    [InlineData("notes", "123")]
    public void A_field_of_the_wrong_type_or_shape_is_not_interpretable(string field, string rawJson)
    {
        WireTest.Unexpected(() => Read(Variants.With(Full(), field, rawJson)));
    }

    [Theory]
    [InlineData("id_arxivar")]
    [InlineData("documents_as_sender")]
    [InlineData("documents_as_recipient")]
    public void An_integer_field_that_is_a_string_a_decimal_or_out_of_range_is_not_interpretable(string field)
    {
        // "docnumber letto come stringa": un numero in una stringa ("4021") non e' un intero.
        foreach (var bad in new[] { "\"4021\"", "4021.5", "1e3", "2147483648", "true", "[]" })
        {
            WireTest.Unexpected(() => Read(Variants.With(Full(), field, bad)));
        }
    }

    [Theory]
    [InlineData("documents_as_sender")]
    [InlineData("documents_as_recipient")]
    public void A_negative_counter_is_not_interpretable(string field)
    {
        WireTest.Unexpected(() => Read(Variants.With(Full(), field, "-1")));
    }

    [Fact]
    public void The_ARXivar_id_can_be_zero_or_negative_the_server_does_not_validate_the_sign()
    {
        Assert.Equal(0, Read(Variants.With(Full(), "id_arxivar", "0")).ArxivarId);
        Assert.Equal(-7, Read(Variants.With(Full(), "id_arxivar", "-7")).ArxivarId);
        Assert.Equal(int.MaxValue, Read(Variants.With(Full(), "id_arxivar", "2147483647")).ArxivarId);
    }

    [Theory]
    [InlineData("\"external\"", ContactKind.External)]
    [InlineData("\"user\"", ContactKind.User)]
    [InlineData("\"group\"", ContactKind.Group)]
    [InlineData("\"External\"", ContactKind.Unknown)]
    [InlineData("\"EXTERNAL\"", ContactKind.Unknown)]
    [InlineData("\"User\"", ContactKind.Unknown)]
    [InlineData("\"group \"", ContactKind.Unknown)]
    [InlineData("\" group\"", ContactKind.Unknown)]
    [InlineData("\"team\"", ContactKind.Unknown)]
    [InlineData("\"\"", ContactKind.Unknown)]
    [InlineData("null", ContactKind.Unknown)]
    public void A_contact_kind_is_compared_exactly_and_a_new_one_is_Unknown_never_an_error(string rawKind, ContactKind expected)
    {
        Assert.Equal(expected, Read(Variants.With(Full(), "kind", rawKind)).Kind);
    }

    [Fact]
    public void A_missing_kind_is_Unknown()
    {
        Assert.Equal(ContactKind.Unknown, Read(Variants.Without(Full(), "kind")).Kind);
    }

    [Fact]
    public void Null_and_missing_optional_fields_are_null_and_an_empty_text_stays_empty()
    {
        var nulled = Read(Variants.Edit(Full(), o =>
        {
            o["city"] = null;
            o["notes"] = null;
            o["id_arxivar"] = null;
            o["documents_as_sender"] = null;
        }));
        var empty = Read(Variants.With(Full(), "notes", "\"\""));

        Assert.Null(nulled.City);
        Assert.Null(nulled.Notes);
        Assert.Null(nulled.ArxivarId);
        Assert.Null(nulled.DocumentsAsSender);
        Assert.Equal(string.Empty, empty.Notes);
    }

    [Fact]
    public void Texts_with_unicode_emoji_and_4096_bytes_are_read_intact_and_unknown_properties_are_ignored()
    {
        var contact = Read(Variants.Edit(Full(), o =>
        {
            o["name"] = "Caff\U000000E8 \U0001F600";
            o["notes"] = new string('n', 4096);
            o["campo_nuovo"] = "x";
        }));

        Assert.Equal("Caff\U000000E8 \U0001F600", contact.Name);
        Assert.Equal(4096, contact.Notes!.Length);
    }

    [Fact]
    public void A_contact_with_every_text_field_distinct_is_read_without_swapping_any_of_them()
    {
        // Un valore diverso per ogni campo: un lettore che scambia due proprieta' (citta'/provincia, fax/cellulare) si vede qui.
        var body = WireTest.Utf8(
            "{\"id\":\"" + Acme.Value + "\",\"name\":\"n\",\"kind\":\"group\",\"category_id\":\"v01\",\"code\":\"v02\",\"address\":\"v03\",\"postal_code\":\"v04\"," +
            "\"city\":\"v05\",\"province\":\"v06\",\"country\":\"v07\",\"email\":\"v08\",\"pec\":\"v09\",\"phone\":\"v10\",\"fax\":\"v11\",\"mobile\":\"v12\"," +
            "\"vat_number\":\"v13\",\"tax_code\":\"v14\",\"ipa_code\":\"v15\",\"office_code\":\"v16\",\"notes\":\"v17\",\"id_arxivar\":18," +
            "\"created_at\":\"2026-01-02T03:04:05Z\",\"documents_as_sender\":19,\"documents_as_recipient\":20}");

        var c = Read(body);

        Assert.Equal(
            new object?[] { ContactKind.Group, "v01", "v02", "v03", "v04", "v05", "v06", "v07", "v08", "v09", "v10", "v11", "v12", "v13", "v14", "v15", "v16", "v17", 18, 19, 20 },
            new object?[] { c.Kind, c.CategoryId, c.Code, c.Address, c.PostalCode, c.City, c.Province, c.Country, c.Email, c.Pec, c.Phone, c.Fax, c.Mobile, c.VatNumber, c.TaxCode, c.IpaCode, c.OfficeCode, c.Notes, c.ArxivarId, c.DocumentsAsSender, c.DocumentsAsRecipient });
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"x\"")]
    [InlineData("1")]
    public void A_body_that_is_not_an_object_is_not_interpretable(string body)
    {
        WireTest.Unexpected(() => Read(WireTest.Utf8(body)));
        WireTest.Unexpected(() => ReadPage(WireTest.Utf8(body)));
        WireTest.Unexpected(() => ReadCategories(WireTest.Utf8(body)));
    }

    // ----- pagina e categorie -----

    [Fact]
    public void One_bad_contact_in_a_page_fails_the_page_and_names_the_element()
    {
        var body = Variants.EditItem(WireFixtures.Derived("contacts-page"), 1, item => item.Remove("name"));

        var exception = WireTest.Unexpected(() => ReadPage(body));

        Assert.Contains("items[1].name", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_last_derived_page_has_no_cursor()
    {
        var body = Variants.Without(WireFixtures.Derived("contacts-page"), "next_cursor");

        Assert.Null(ReadPage(body).NextCursor);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("name")]
    public void A_category_without_a_required_field_is_not_interpretable(string field)
    {
        var body = Variants.EditItem(WireFixtures.Derived("contact-categories"), 0, item => item.Remove(field));

        WireTest.Unexpected(() => ReadCategories(body));
    }

    [Theory]
    [InlineData("id", "5")]
    [InlineData("name", "[]")]
    [InlineData("id_arxivar", "\"12\"")]
    [InlineData("id_arxivar", "12.5")]
    [InlineData("id_arxivar", "2147483648")]
    public void A_category_field_of_the_wrong_type_is_not_interpretable(string field, string rawJson)
    {
        var body = Variants.EditItem(WireFixtures.Derived("contact-categories"), 1, item => item[field] = System.Text.Json.Nodes.JsonNode.Parse(rawJson));

        WireTest.Unexpected(() => ReadCategories(body));
    }

    [Fact]
    public void A_category_code_is_kept_as_text_without_validation_and_a_missing_ARXivar_id_is_null()
    {
        var body = WireTest.Utf8("{\"items\":[{\"id\":\"ARX-12\",\"name\":\"\"},{\"id\":\"\",\"name\":\"senza codice\"}]}");

        var categories = ReadCategories(body);

        Assert.Equal(new ContactCategory("ARX-12", string.Empty, null), categories[0]);
        Assert.Equal(string.Empty, categories[1].Id);
    }

    [Fact]
    public void A_category_listing_without_items_is_not_interpretable()
    {
        WireTest.Unexpected(() => ReadCategories(WireTest.Utf8("{}")));
        WireTest.Unexpected(() => ReadCategories(WireTest.Utf8("{\"items\":null}")));
        WireTest.Unexpected(() => ReadCategories(WireTest.Utf8("{\"items\":[1]}")));
    }
}
