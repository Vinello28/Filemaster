using System.Globalization;
using System.Text;
using System.Text.Json;
using Filemaster.Application;
using Filemaster.Domain;

namespace Filemaster.UnitTests.Application;

/// <summary>
/// <see cref="UploadDocumentRequest"/>: i limiti sono quelli esatti del server (Sharp-a-File dev): ogni campo testuale
/// multipart 4096 byte UTF-8 misurati sul valore grezzo, owner e tag 255 caratteri dopo il trim, metadati un oggetto JSON
/// di al massimo 65536 byte, nome file non vuoto dopo aver tolto il percorso. Una violazione e' sempre un'eccezione di
/// argomento (mai <see cref="InvalidRequestException"/>, che e' il 400 del server) e lo stream non si tocca.
/// </summary>
public sealed class UploadDocumentRequestTests
{
    private const int FieldBytes = 4096;
    private const int MetadataBytes = 65536;

    private static UploadDocumentRequest NewRequest(Stream? content = null) =>
        new(content ?? new MemoryStream(new byte[] { 1, 2, 3 }), "fattura.pdf");

    private static JsonElement Json(string text)
    {
        // Mai smaltito: l'elemento vive per la durata del test.
        return JsonDocument.Parse(text).RootElement;
    }

    // {"k":"<padding ASCII>"} lungo esattamente totalBytes byte UTF-8.
    private static JsonElement MetadataOfBytes(int totalBytes)
    {
        const string Frame = "{\"k\":\"\"}";
        return Json("{\"k\":\"" + new string('a', totalBytes - Frame.Length) + "\"}");
    }

    private static void SetField(UploadDocumentRequest request, string field, string? value)
    {
        switch (field)
        {
            case nameof(UploadDocumentRequest.Owner):
                request.Owner = value;
                break;
            case nameof(UploadDocumentRequest.Tag):
                request.Tag = value;
                break;
            case nameof(UploadDocumentRequest.Sender):
                request.Sender = value;
                break;
            case nameof(UploadDocumentRequest.Recipient):
                request.Recipient = value;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(field), field, null);
        }
    }

    private static void AssertRejects(UploadDocumentRequest request, string paramName)
    {
        var exception = Assert.Throws<ArgumentException>(request.Validate);

        Assert.Equal(paramName, exception.ParamName);
    }

    [Fact]
    public void Constructor_with_null_content_throws_ArgumentNullException_for_content()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => new UploadDocumentRequest(null!, "a.pdf"));

        Assert.Equal("content", exception.ParamName);
    }

    [Fact]
    public void Constructor_with_null_fileName_throws_ArgumentNullException_for_fileName()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => new UploadDocumentRequest(new MemoryStream(), null!));

        Assert.Equal("fileName", exception.ParamName);
    }

    [Fact]
    public void Constructor_keeps_the_arguments_and_leaves_every_optional_property_unset()
    {
        using var content = new MemoryStream();

        var request = new UploadDocumentRequest(content, "fattura.pdf");

        Assert.Same(content, request.Content);
        Assert.Equal("fattura.pdf", request.FileName);
        Assert.Null(request.ContentType);
        Assert.Null(request.FolderId);
        Assert.Null(request.Owner);
        Assert.Null(request.Tag);
        Assert.Null(request.Sender);
        Assert.Null(request.Recipient);
        Assert.Null(request.Metadata);
    }

    [Fact]
    public void Validate_accepts_a_request_with_only_the_required_arguments()
    {
        NewRequest().Validate();
    }

    [Fact]
    public void Validate_accepts_a_request_with_every_optional_property_set()
    {
        var request = NewRequest();
        request.ContentType = "application/pdf";
        request.FolderId = new FolderCode("FATTURE");
        request.Owner = "gabriele";
        request.Tag = "fattura";
        request.Sender = "Acme Srl";
        request.Recipient = "Beta Spa";
        request.Metadata = Json("""{"arxivar":{"docnumber":12345,"categoria":"X"}}""");

        request.Validate();
    }

    // --- Stream: Validate non lo legge e non lo tocca ---

    [Fact]
    public void Validate_does_not_read_or_touch_the_stream_beyond_CanRead()
    {
        var content = new UntouchableStream();
        var request = new UploadDocumentRequest(content, "fattura.pdf");

        request.Validate();

        Assert.Equal(0, content.OtherCalls); // CanRead si puo' leggere, nient'altro (ne' Length, ne' Position, ne' Read)
    }

    [Fact]
    public void Validate_with_a_stream_that_cannot_be_read_throws_ArgumentException_for_Content()
    {
        var content = new MemoryStream(new byte[] { 1 });
        content.Dispose(); // uno stream smaltito ha CanRead falso
        var request = new UploadDocumentRequest(content, "fattura.pdf");

        AssertRejects(request, nameof(UploadDocumentRequest.Content));
    }

    // --- Nome file ---

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void Validate_with_an_empty_or_blank_fileName_throws_ArgumentException_for_FileName(string fileName)
    {
        AssertRejects(new UploadDocumentRequest(new MemoryStream(), fileName), nameof(UploadDocumentRequest.FileName));
    }

    [Theory]
    [InlineData("cartella/")]
    [InlineData("C:\\cartella\\")]
    [InlineData("cartella/   ")]
    public void Validate_with_a_fileName_that_is_empty_once_the_path_is_removed_throws_ArgumentException_for_FileName(string fileName)
    {
        // Il server tiene solo la parte dopo l'ultimo '/' o '\': "cartella/" diventa il nome vuoto, cioe' un 400.
        AssertRejects(new UploadDocumentRequest(new MemoryStream(), fileName), nameof(UploadDocumentRequest.FileName));
    }

    [Theory]
    [InlineData("fattura.pdf")]
    [InlineData("C:\\cartella\\fattura.pdf")]
    [InlineData("cartella/fattura.pdf")]
    [InlineData("perche' e'.pdf")]
    [InlineData(" spazi iniziali.pdf")]
    [InlineData("senza-estensione")]
    [InlineData(".nascosto")]
    public void Validate_accepts_a_fileName_with_a_non_empty_base_name(string fileName)
    {
        new UploadDocumentRequest(new MemoryStream(), fileName).Validate();
    }

    // --- Content-Type ---

    [Theory]
    [InlineData("application/pdf")]
    [InlineData("application/octet-stream")]
    [InlineData("text/plain; charset=utf-8")]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("image/svg+xml")]
    public void Validate_accepts_a_well_formed_content_type(string contentType)
    {
        var request = NewRequest();
        request.ContentType = contentType;

        request.Validate();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_treats_an_empty_content_type_as_absent_like_the_server_does(string contentType)
    {
        var request = NewRequest();
        request.ContentType = contentType;

        request.Validate();
    }

    [Theory]
    [InlineData("pdf")]
    [InlineData("application/")]
    [InlineData("/pdf")]
    [InlineData("application/pdf/extra")]
    [InlineData("appli cation/pdf")]
    [InlineData("application/pdf\r\nX-Evil: 1")]
    [InlineData("application/pdf; a=b\nX-Evil: 1")]
    [InlineData("application/p\U000000E4df")]
    public void Validate_with_a_malformed_content_type_throws_ArgumentException_for_ContentType(string contentType)
    {
        var request = NewRequest();
        request.ContentType = contentType;

        AssertRejects(request, nameof(UploadDocumentRequest.ContentType));
    }

    // --- Cartella ---

    [Fact]
    public void Validate_with_the_empty_default_folder_code_throws_ArgumentException_for_FolderId()
    {
        // default(FolderCode) e' il codice vuoto: costruirebbe una richiesta con folder_id vuoto, che il server tratta
        // come "nessuna cartella" in silenzio.
        var request = NewRequest();
        request.FolderId = default(FolderCode);

        AssertRejects(request, nameof(UploadDocumentRequest.FolderId));
    }

    // --- Owner e Tag: 255 caratteri dopo il trim, 4096 byte sul campo grezzo ---

    [Theory]
    [InlineData("Owner")]
    [InlineData("Tag")]
    public void Validate_accepts_owner_and_tag_of_exactly_255_characters(string field)
    {
        var request = NewRequest();
        SetField(request, field, new string('a', 255));

        request.Validate();
    }

    [Theory]
    [InlineData("Owner")]
    [InlineData("Tag")]
    public void Validate_with_owner_or_tag_of_256_characters_throws_ArgumentException(string field)
    {
        var request = NewRequest();
        SetField(request, field, new string('a', 256));

        AssertRejects(request, field);
    }

    [Theory]
    [InlineData("Owner")]
    [InlineData("Tag")]
    public void Validate_measures_owner_and_tag_after_trimming_the_surrounding_whitespace(string field)
    {
        // Il server trimma prima di misurare: 255 caratteri piu' spazi ai bordi sono accettati.
        var request = NewRequest();
        SetField(request, field, "  " + new string('a', 255) + "\t ");

        request.Validate();
    }

    [Theory]
    [InlineData("Owner")]
    [InlineData("Tag")]
    public void Validate_measures_owner_and_tag_in_characters_not_in_bytes(string field)
    {
        // 255 volte il simbolo dell'euro: 255 caratteri, 765 byte. Il limite del server e' sui caratteri.
        var request = NewRequest();
        SetField(request, field, new string('\U000020AC', 255));

        request.Validate();
    }

    [Theory]
    [InlineData("Owner")]
    [InlineData("Tag")]
    public void Validate_with_owner_or_tag_whose_raw_field_exceeds_4096_bytes_throws_even_if_it_trims_below_255(string field)
    {
        // Il server misura i byte del campo multipart PRIMA del trim: 255 caratteri e 4000 spazi sono 4255 byte.
        var request = NewRequest();
        SetField(request, field, new string('a', 255) + new string(' ', 4000));

        AssertRejects(request, field);
    }

    [Theory]
    [InlineData("Owner")]
    [InlineData("Tag")]
    [InlineData("Sender")]
    [InlineData("Recipient")]
    public void Validate_treats_a_blank_text_field_as_absent(string field)
    {
        var request = NewRequest();
        SetField(request, field, "   ");

        request.Validate();
    }

    // --- Sender e Recipient: solo i 4096 byte del campo grezzo ---

    [Theory]
    [InlineData("Sender")]
    [InlineData("Recipient")]
    public void Validate_accepts_sender_and_recipient_of_exactly_4096_bytes(string field)
    {
        var request = NewRequest();
        SetField(request, field, new string('a', FieldBytes));

        request.Validate();
    }

    [Theory]
    [InlineData("Sender")]
    [InlineData("Recipient")]
    public void Validate_with_sender_or_recipient_of_4097_bytes_throws_ArgumentException(string field)
    {
        var request = NewRequest();
        SetField(request, field, new string('a', FieldBytes + 1));

        AssertRejects(request, field);
    }

    [Theory]
    [InlineData("Sender")]
    [InlineData("Recipient")]
    public void Validate_measures_sender_and_recipient_in_UTF8_bytes_not_in_characters(string field)
    {
        var fits = NewRequest();
        SetField(fits, field, new string('\U000020AC', 1365)); // 4095 byte
        fits.Validate();

        var tooBig = NewRequest();
        SetField(tooBig, field, new string('\U000020AC', 1366)); // 4098 byte, ma solo 1366 caratteri
        AssertRejects(tooBig, field);
    }

    [Theory]
    [InlineData("Sender")]
    [InlineData("Recipient")]
    public void Validate_counts_a_surrogate_pair_as_four_bytes(string field)
    {
        var fits = NewRequest();
        SetField(fits, field, string.Concat(Enumerable.Repeat("\U0001F600", 1024))); // 1024 x 4 = 4096 byte
        fits.Validate();

        var tooBig = NewRequest();
        SetField(tooBig, field, string.Concat(Enumerable.Repeat("\U0001F600", 1025))); // 4100 byte
        AssertRejects(tooBig, field);
    }

    [Theory]
    [InlineData("Sender")]
    [InlineData("Recipient")]
    public void Validate_does_not_trim_before_measuring_sender_and_recipient(string field)
    {
        // Il server misura il campo grezzo: gli spazi ai bordi contano nei 4096 byte.
        var request = NewRequest();
        SetField(request, field, new string('a', FieldBytes) + " ");

        AssertRejects(request, field);
    }

    // --- Metadati: un oggetto JSON, al massimo 65536 byte UTF-8 ---

    [Fact]
    public void Validate_accepts_an_empty_metadata_object()
    {
        var request = NewRequest();
        request.Metadata = Json("{}");

        request.Validate();
    }

    [Fact]
    public void Validate_accepts_metadata_of_exactly_65536_bytes()
    {
        var request = NewRequest();
        request.Metadata = MetadataOfBytes(MetadataBytes);
        Assert.Equal(MetadataBytes, Encoding.UTF8.GetByteCount(request.Metadata.Value.GetRawText()));

        request.Validate();
    }

    [Fact]
    public void Validate_with_metadata_of_65537_bytes_throws_ArgumentException_for_Metadata()
    {
        var request = NewRequest();
        request.Metadata = MetadataOfBytes(MetadataBytes + 1);

        AssertRejects(request, nameof(UploadDocumentRequest.Metadata));
    }

    [Fact]
    public void Validate_measures_metadata_in_UTF8_bytes_not_in_characters()
    {
        // 30000 simboli dell'euro sono 30000 caratteri ma 90000 byte.
        var request = NewRequest();
        request.Metadata = Json("{\"k\":\"" + new string('\U000020AC', 30000) + "\"}");

        AssertRejects(request, nameof(UploadDocumentRequest.Metadata));
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("\"testo\"")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("null")]
    public void Validate_with_metadata_that_is_not_a_JSON_object_throws_ArgumentException_for_Metadata(string json)
    {
        // Anche "null": il server lo accetta come "nessun metadato", ma chi non ne ha non imposta la proprieta'.
        var request = NewRequest();
        request.Metadata = Json(json);

        AssertRejects(request, nameof(UploadDocumentRequest.Metadata));
    }

    [Fact]
    public void Validate_with_default_JsonElement_metadata_throws_ArgumentException_not_InvalidOperationException()
    {
        // default(JsonElement) ha ValueKind Undefined e GetRawText() lancia InvalidOperationException: va controllato prima.
        var request = NewRequest();
        request.Metadata = default(JsonElement);

        AssertRejects(request, nameof(UploadDocumentRequest.Metadata));
    }

    // --- Ordine dei controlli e valori di riferimento ---

    [Fact]
    public void Public_limits_are_the_ones_of_the_server()
    {
        Assert.Equal(255, UploadDocumentRequest.MaxIndexedTextLength);
        Assert.Equal(FieldBytes, UploadDocumentRequest.MaxFieldBytes);
        Assert.Equal(MetadataBytes, UploadDocumentRequest.MaxMetadataBytes);
    }

    [Fact]
    public void Validate_reports_the_first_violation_by_declaration_order_of_the_properties()
    {
        var request = new UploadDocumentRequest(new MemoryStream(), " ");
        request.Owner = new string('a', 256);

        AssertRejects(request, nameof(UploadDocumentRequest.FileName));
    }

    // Uno stream che registra ogni uso oltre alla lettura di CanRead: Validate non deve leggerlo ne' riposizionarlo.
    private sealed class UntouchableStream : Stream
    {
        public int OtherCalls { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => Touched(false);

        public override bool CanWrite => Touched(false);

        public override long Length => Touched(0L);

        public override long Position
        {
            get => Touched(0L);
            set => Touched(0);
        }

        public override void Flush() => Touched(0);

        public override int Read(byte[] buffer, int offset, int count) => Touched(0);

        public override long Seek(long offset, SeekOrigin origin) => Touched(0L);

        public override void SetLength(long value) => Touched(0);

        public override void Write(byte[] buffer, int offset, int count) => Touched(0);

        private T Touched<T>(T value)
        {
            OtherCalls++;
            return value;
        }
    }
}
