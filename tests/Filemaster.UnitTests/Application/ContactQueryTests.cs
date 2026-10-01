using Filemaster.Application;
using Filemaster.Domain;

namespace Filemaster.UnitTests.Application;

/// <summary>
/// <see cref="ContactQuery"/>: i filtri di <c>GET /contacts</c> del server dev. <c>category_id</c> ha la forma di un codice di
/// cartella (un valore malformato e' 404 sul server) e <c>kind</c> accetta solo <c>external</c>, <c>user</c> e <c>group</c>
/// (qualunque altro e' 400): <see cref="ContactKind.Unknown"/> non e' un genere che si possa cercare.
/// </summary>
public sealed class ContactQueryTests
{
    private static void AssertRejects(ContactQuery query, string paramName)
    {
        var exception = Assert.Throws<ArgumentException>(query.Validate);

        Assert.Equal(paramName, exception.ParamName);
    }

    [Fact]
    public void A_new_query_has_no_filter_and_is_valid()
    {
        var query = new ContactQuery();

        Assert.Null(query.Text);
        Assert.Null(query.CategoryId);
        Assert.Null(query.Kind);
        query.Validate();
    }

    [Fact]
    public void Validate_accepts_every_filter_set_together()
    {
        var query = new ContactQuery();
        query.Text = "acme";
        query.CategoryId = "ASSOCIATI";
        query.Kind = ContactKind.External;

        query.Validate();
    }

    [Theory]
    [InlineData(ContactKind.External)]
    [InlineData(ContactKind.User)]
    [InlineData(ContactKind.Group)]
    public void Validate_accepts_the_three_kinds_the_server_knows(ContactKind kind)
    {
        var query = new ContactQuery();
        query.Kind = kind;

        query.Validate();
    }

    [Theory]
    [InlineData(ContactKind.Unknown)]
    [InlineData((ContactKind)99)]
    [InlineData((ContactKind)(-1))]
    public void Validate_with_a_kind_the_server_does_not_have_throws_ArgumentException_for_Kind(ContactKind kind)
    {
        var query = new ContactQuery();
        query.Kind = kind;

        AssertRejects(query, nameof(ContactQuery.Kind));
    }

    [Theory]
    [InlineData("ASSOCIATI")]
    [InlineData("ARX-12")]
    [InlineData("a")]
    [InlineData("2026.09-fatture_v2")]
    public void Validate_accepts_a_category_code_with_the_shape_of_a_folder_code(string categoryId)
    {
        var query = new ContactQuery();
        query.CategoryId = categoryId;

        query.Validate();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ASSOCIATI")]
    [InlineData("ASSOCIATI ")]
    [InlineData("ASSOCIATI\n")]
    [InlineData("-ASSOCIATI")]
    [InlineData("ASSO/CIATI")]
    [InlineData("ASSOCIATI\U000000E0")]
    public void Validate_with_a_malformed_category_code_throws_ArgumentException_for_CategoryId(string categoryId)
    {
        // Il server risponde 404 a un category_id malformato (non e' "non trovato" per un filtro: e' un errore di forma).
        var query = new ContactQuery();
        query.CategoryId = categoryId;

        AssertRejects(query, nameof(ContactQuery.CategoryId));
    }

    [Fact]
    public void Validate_accepts_a_category_code_of_50_characters_and_rejects_51()
    {
        var fits = new ContactQuery();
        fits.CategoryId = new string('A', FolderCode.MaxLength);
        fits.Validate();

        var tooLong = new ContactQuery();
        tooLong.CategoryId = new string('A', FolderCode.MaxLength + 1);
        AssertRejects(tooLong, nameof(ContactQuery.CategoryId));
    }

    [Fact]
    public void Validate_accepts_any_text_for_the_search_filter()
    {
        var query = new ContactQuery();
        query.Text = string.Empty;
        query.Validate();

        query.Text = "100%_[x]\\ perche' e'";
        query.Validate();
    }
}
