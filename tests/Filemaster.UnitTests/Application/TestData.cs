using System.Globalization;
using System.Text.Json;
using Filemaster.Domain;

namespace Filemaster.UnitTests.Application;

/// <summary>Documenti e contatti di prova, riconoscibili dal numero (gli id sono numeri validi; quelli dei documenti superano <c>int.MaxValue</c>).</summary>
internal static class TestData
{
    internal static DocumentId DocumentIdOf(int n) => DocumentId.From(3_000_000_000L + n);

    internal static ContactId ContactIdOf(int n) => ContactId.From(7_000 + n);

    internal static Document Doc(int n, string? sha256 = null, long sizeBytes = 0, string? metadataJson = "{}", string mimeType = "application/pdf")
    {
        // Mai smaltito: l'elemento vive per la durata del test. Con metadataJson null i metadati sono default(JsonElement).
        var metadata = metadataJson is null ? default : JsonDocument.Parse(metadataJson).RootElement;
        return new Document(
            DocumentIdOf(n),
            FolderId: null,
            OriginalFilename: "documento-" + n.ToString(CultureInfo.InvariantCulture) + ".pdf",
            MimeType: mimeType,
            Sha256: sha256,
            SizeBytes: sizeBytes,
            Owner: null,
            Tag: null,
            Sender: null,
            Recipient: null,
            Metadata: metadata,
            CreatedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Contacts: Array.Empty<DocumentContact>());
    }

    internal static Contact ContactOf(int n) =>
        new(
            ContactIdOf(n),
            Name: "Contatto " + n.ToString(CultureInfo.InvariantCulture),
            Kind: ContactKind.External,
            CategoryId: null,
            Code: null,
            Address: null,
            PostalCode: null,
            City: null,
            Province: null,
            Country: null,
            Email: null,
            Pec: null,
            Phone: null,
            Fax: null,
            Mobile: null,
            VatNumber: null,
            TaxCode: null,
            IpaCode: null,
            OfficeCode: null,
            Notes: null,
            ArxivarId: null,
            CreatedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            DocumentsAsSender: null,
            DocumentsAsRecipient: null);
}
