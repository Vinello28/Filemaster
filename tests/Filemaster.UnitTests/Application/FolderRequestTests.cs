using Filemaster.Application;
using Filemaster.Domain;

namespace Filemaster.UnitTests.Application;

/// <summary>
/// <see cref="CreateFolderRequest"/> e <see cref="UpdateFolderRequest"/>: il nome di una cartella e' obbligatorio, trimmato
/// e di al massimo 255 caratteri (non byte) come per il server; un codice o un padre vuoti (<c>default</c>) si rifiutano.
/// </summary>
public sealed class FolderRequestTests
{
    private static readonly FolderCode Code = new("FATTURE");

    private static void AssertRejects(Action validate, string? paramName)
    {
        var exception = Assert.Throws<ArgumentException>(validate);

        Assert.Equal(paramName, exception.ParamName);
    }

    // --- CreateFolderRequest ---

    [Fact]
    public void Create_constructor_with_null_name_throws_ArgumentNullException_for_name()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => new CreateFolderRequest(Code, null!));

        Assert.Equal("name", exception.ParamName);
    }

    [Fact]
    public void Create_constructor_keeps_the_arguments_and_leaves_the_parent_unset()
    {
        var request = new CreateFolderRequest(Code, "Fatture");

        Assert.Equal(Code, request.Code);
        Assert.Equal("Fatture", request.Name);
        Assert.Null(request.ParentId);
    }

    [Fact]
    public void Create_Validate_accepts_a_request_with_a_parent()
    {
        var request = new CreateFolderRequest(new FolderCode("FATTURE.2026"), "Fatture 2026");
        request.ParentId = Code;

        request.Validate();
    }

    [Fact]
    public void Create_Validate_with_the_empty_default_code_throws_ArgumentException_for_Code()
    {
        var request = new CreateFolderRequest(default, "Fatture");

        AssertRejects(request.Validate, nameof(CreateFolderRequest.Code));
    }

    [Fact]
    public void Create_Validate_with_the_empty_default_parent_throws_ArgumentException_for_ParentId()
    {
        // default(FolderCode) e' il codice vuoto, non "nessun padre": per la radice si lascia null.
        var request = new CreateFolderRequest(Code, "Fatture");
        request.ParentId = default(FolderCode);

        AssertRejects(request.Validate, nameof(CreateFolderRequest.ParentId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void Create_Validate_with_an_empty_or_blank_name_throws_ArgumentException_for_Name(string name)
    {
        AssertRejects(new CreateFolderRequest(Code, name).Validate, nameof(CreateFolderRequest.Name));
    }

    [Fact]
    public void Create_Validate_accepts_a_name_of_exactly_255_characters_and_rejects_256()
    {
        new CreateFolderRequest(Code, new string('a', 255)).Validate();

        AssertRejects(new CreateFolderRequest(Code, new string('a', 256)).Validate, nameof(CreateFolderRequest.Name));
    }

    [Fact]
    public void Create_Validate_measures_the_name_after_trimming()
    {
        // Il server trimma il nome prima di misurarlo e di salvarlo.
        new CreateFolderRequest(Code, "  " + new string('a', 255) + " \t").Validate();
    }

    [Fact]
    public void Create_Validate_measures_the_name_in_characters_not_in_bytes()
    {
        // 255 volte il simbolo dell'euro: 255 caratteri, 765 byte.
        new CreateFolderRequest(Code, new string('\U000020AC', 255)).Validate();
    }

    [Fact]
    public void Create_MaxNameLength_is_the_server_limit()
    {
        Assert.Equal(255, CreateFolderRequest.MaxNameLength);
    }

    // --- UpdateFolderRequest ---

    [Fact]
    public void Update_constructor_leaves_both_changes_unset()
    {
        var request = new UpdateFolderRequest();

        Assert.Null(request.NewCode);
        Assert.Null(request.Name);
    }

    [Fact]
    public void Update_Validate_with_no_change_throws_ArgumentException_without_a_single_parameter_to_blame()
    {
        // Il server risponde 400 "indica il nuovo codice o il nuovo nome": la colpa e' della combinazione.
        AssertRejects(new UpdateFolderRequest().Validate, null);
    }

    [Fact]
    public void Update_Validate_accepts_only_a_new_name()
    {
        var request = new UpdateFolderRequest();
        request.Name = "Fatture 2026 rinominata";

        request.Validate();
    }

    [Fact]
    public void Update_Validate_accepts_only_a_new_code()
    {
        var request = new UpdateFolderRequest();
        request.NewCode = new FolderCode("FATTURE.2027");

        request.Validate();
    }

    [Fact]
    public void Update_Validate_accepts_a_new_code_and_a_new_name_together()
    {
        var request = new UpdateFolderRequest();
        request.NewCode = new FolderCode("FATTURE.2027");
        request.Name = "Fatture 2027";

        request.Validate();
    }

    [Fact]
    public void Update_Validate_with_the_empty_default_new_code_throws_ArgumentException_for_NewCode()
    {
        var request = new UpdateFolderRequest();
        request.NewCode = default(FolderCode);
        request.Name = "Fatture";

        AssertRejects(request.Validate, nameof(UpdateFolderRequest.NewCode));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Update_Validate_with_an_empty_or_blank_name_throws_ArgumentException_for_Name(string name)
    {
        // Un nome vuoto non e' "lascia il nome com'e'" (quello e' null): il server lo rifiuterebbe.
        var request = new UpdateFolderRequest();
        request.NewCode = new FolderCode("FATTURE.2027");
        request.Name = name;

        AssertRejects(request.Validate, nameof(UpdateFolderRequest.Name));
    }

    [Fact]
    public void Update_Validate_accepts_a_name_of_exactly_255_characters_and_rejects_256()
    {
        var fits = new UpdateFolderRequest();
        fits.Name = new string('a', 255);
        fits.Validate();

        var tooLong = new UpdateFolderRequest();
        tooLong.Name = new string('a', 256);
        AssertRejects(tooLong.Validate, nameof(UpdateFolderRequest.Name));
    }
}
