using System.Text.Json;
using Filemaster.Domain;

namespace Filemaster.UnitTests.Domain;

/// <summary>
/// <see cref="Document"/>: cio' che non e' ovvio. Il resto delle entita' sono record di dati senza regole proprie; qui
/// ci sono la nullabilita' di <c>Sha256</c>, il default di <c>Contacts</c> e il trabocchetto dell'uguaglianza.
/// </summary>
public sealed class DocumentTests
{
    // Id numerici del server attuale (documento bigint, contatto int); lo sha256 e' quello della fixture 98 del server dev.
    private const string DocumentIdText = "5000000042";
    private const string ContactIdText = "42";
    private const string Sha = "cc1ba284a9fe9cefa40d4bd9dfb8d9e7fb395431aaf79478efca4e04da6c9d7e";
    private const string MetadataText = """{"arxivar":{"docnumber":12345,"categoria":"X"}}""";

    private static readonly DateTimeOffset Created = new(2026, 10, 1, 9, 59, 15, TimeSpan.Zero);

    private static Document NewDocument(JsonElement metadata, IReadOnlyList<DocumentContact>? contacts, string? sha256 = Sha) => new(
        new DocumentId(DocumentIdText),
        new FolderCode("FATTURE"),
        "fattura.pdf",
        "application/pdf",
        sha256,
        590,
        "gabriele",
        "fattura",
        "Acme Srl",
        "Beta Spa",
        metadata,
        Created,
        contacts!);

    private static DocumentContact NewContact() => new(ContactRole.Sender, new ContactId(ContactIdText), "Acme Srl");

    [Fact]
    public void HasContent_is_true_only_when_the_document_has_a_sha256()
    {
        // Un documento importato con i soli metadati ha has_content=false e nessun sha256 (omesso, non null).
        using var metadata = JsonDocument.Parse("{}");

        Assert.True(NewDocument(metadata.RootElement, null, Sha).HasContent);
        Assert.False(NewDocument(metadata.RootElement, null, sha256: null).HasContent);
    }

    [Fact]
    public void Contacts_null_becomes_an_empty_list_so_a_missing_array_is_never_null()
    {
        // Il server mette "contacts" solo nel dettaglio: nell'elenco manca. Chi costruisce il Document puo' passare null.
        using var metadata = JsonDocument.Parse("{}");

        var document = NewDocument(metadata.RootElement, null);

        Assert.NotNull(document.Contacts);
        Assert.Empty(document.Contacts);
    }

    [Fact]
    public void Contacts_given_are_kept_as_they_are()
    {
        using var metadata = JsonDocument.Parse("{}");
        var contacts = new[] { NewContact() };

        var document = NewDocument(metadata.RootElement, contacts);

        Assert.Same(contacts, document.Contacts);
    }

    [Fact]
    public void Two_documents_parsed_from_identical_JSON_are_not_equal_because_Metadata_compares_by_identity()
    {
        // JsonElement non implementa l'uguaglianza per contenuto: due elementi sono uguali solo se vengono dallo stesso
        // JsonDocument e dalla stessa posizione, e nemmeno un Clone() e' uguale all'originale. Un record con un
        // JsonElement eredita questo: "a == b" fra documenti con gli stessi dati e' falso.
        using var first = JsonDocument.Parse(MetadataText);
        using var second = JsonDocument.Parse(MetadataText);

        var a = NewDocument(first.RootElement, Array.Empty<DocumentContact>());
        var b = NewDocument(second.RootElement, Array.Empty<DocumentContact>());
        var cloned = NewDocument(first.RootElement.Clone(), Array.Empty<DocumentContact>());

        Assert.NotEqual(a, b);
        Assert.NotEqual(a, cloned);
        Assert.Equal(a.Metadata.GetRawText(), b.Metadata.GetRawText()); // il contenuto si confronta cosi'
    }

    [Fact]
    public void Two_documents_with_equal_contacts_are_not_equal_because_the_list_compares_by_reference()
    {
        using var metadata = JsonDocument.Parse("{}");

        var a = NewDocument(metadata.RootElement, new[] { NewContact() });
        var b = NewDocument(metadata.RootElement, new[] { NewContact() });

        Assert.Equal(a.Contacts.Single(), b.Contacts.Single()); // DocumentContact e' un record di valori: questo si' uguale
        Assert.NotEqual(a, b); // ma la lista e' un'altra istanza
    }

    [Fact]
    public void A_document_is_equal_to_itself_and_to_a_copy_sharing_the_same_metadata_element_and_list()
    {
        using var metadata = JsonDocument.Parse(MetadataText);
        var contacts = new[] { NewContact() };

        var a = NewDocument(metadata.RootElement, contacts);
        var b = NewDocument(metadata.RootElement, contacts);

        Assert.Equal(a, a);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, NewDocument(metadata.RootElement, contacts, sha256: null)); // un campo scalare diverso resta diverso
    }
}
