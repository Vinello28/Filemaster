using Filemaster.Application;
using Filemaster.Infrastructure;

namespace Filemaster.UnitTests.Wire;

/// <summary>
/// <c>Content-Range</c> di una risposta 206: <c>bytes primo-ultimo/totale</c> con numeri interi non negativi e l'ultimo byte prima del totale.
/// Le forme senza intervallo (<c>bytes *&#47;590</c>, quella di un 416) o senza totale (<c>0-9/*</c>) non sono un <see cref="ContentRange"/>.
/// I valori validi sono quelli delle catture 109, 110 e 111.
/// </summary>
public sealed class ContentRangeHeaderTests
{
    private static ContentRange? Parse(string? value) => ContentRangeHeader.TryParse(value, out var range) ? range : null;

    [Fact]
    public void The_captured_headers_of_the_three_range_forms_are_read()
    {
        // 109 (bytes=0-9), 110 (bytes=-10 su 590 byte), 111 (bytes=5-)
        Assert.Equal(new ContentRange(0, 9, 590), Parse(WireFixtures.Headers("109-doc-content-range-0-9")["Content-Range"]));
        Assert.Equal(new ContentRange(580, 589, 590), Parse(WireFixtures.Headers("110-doc-content-range-suffix")["Content-Range"]));
        Assert.Equal(new ContentRange(5, 589, 590), Parse(WireFixtures.Headers("111-doc-content-range-open")["Content-Range"]));
    }

    [Theory]
    [InlineData("bytes 0-9/590", 0L, 9L, 590L)]
    [InlineData("bytes 0-0/1", 0L, 0L, 1L)]
    [InlineData("bytes 589-589/590", 589L, 589L, 590L)]
    [InlineData("BYTES 0-9/590", 0L, 9L, 590L)]
    [InlineData("Bytes 0-9/590", 0L, 9L, 590L)]
    [InlineData("  bytes 0-9/590  ", 0L, 9L, 590L)]
    [InlineData("bytes 0-4294967296/4294967297", 0L, 4294967296L, 4294967297L)]
    [InlineData("bytes 9223372036854775800-9223372036854775806/9223372036854775807", 9223372036854775800L, 9223372036854775806L, 9223372036854775807L)]
    [InlineData("bytes 007-009/0010", 7L, 9L, 10L)]
    public void A_valid_value_is_read_field_by_field(string value, long first, long last, long total)
    {
        var range = Parse(value);

        Assert.NotNull(range);
        Assert.Equal(first, range!.FirstByte);
        Assert.Equal(last, range.LastByte);
        Assert.Equal(total, range.TotalLength);
    }

    [Theory]
    [InlineData("bytes */590")]
    [InlineData("bytes 0-9/*")]
    [InlineData("bytes */*")]
    [InlineData("bytes 9-0/590")]
    [InlineData("bytes 5-4/590")]
    [InlineData("bytes 0-590/590")]
    [InlineData("bytes 0-591/590")]
    [InlineData("bytes 0-9/0")]
    [InlineData("bytes 590-590/590")]
    [InlineData("bytes 0-9")]
    [InlineData("bytes 0/590")]
    [InlineData("bytes 0-/590")]
    [InlineData("bytes -9/590")]
    [InlineData("bytes /590")]
    [InlineData("bytes 0-9/")]
    [InlineData("bytes -1-5/10")]
    [InlineData("bytes +0-9/590")]
    [InlineData("bytes 0-+9/590")]
    [InlineData("bytes 0 -9/590")]
    [InlineData("bytes 0- 9/590")]
    [InlineData("bytes 0-9 /590")]
    [InlineData("bytes 0-9/ 590")]
    [InlineData("bytes 0-9/5 90")]
    [InlineData("bytes 0.5-9/590")]
    [InlineData("bytes 0x0-9/590")]
    [InlineData("bytes 1e1-9/590")]
    [InlineData("bytes 0-9/590/1")]
    [InlineData("bytes 0-9-12/590")]
    [InlineData("bytes0-9/590")]
    [InlineData("bytes  0-9/590")]
    [InlineData("bytes\t0-9/590")]
    [InlineData("byte 0-9/590")]
    [InlineData("items 0-9/590")]
    [InlineData("0-9/590")]
    [InlineData("bytes")]
    [InlineData("bytes ")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-")]
    [InlineData("/")]
    public void A_value_that_is_not_bytes_first_last_total_is_refused(string value)
    {
        Assert.Null(Parse(value));
    }

    [Fact]
    public void A_null_value_is_refused_without_throwing()
    {
        Assert.False(ContentRangeHeader.TryParse(null, out var range));
        Assert.Null(range);
    }

    [Theory]
    [InlineData("bytes 0-9/9223372036854775808")]
    [InlineData("bytes 0-9223372036854775808/9223372036854775809")]
    [InlineData("bytes 9223372036854775808-9223372036854775809/9223372036854775810")]
    [InlineData("bytes 0-9/99999999999999999999")]
    [InlineData("bytes 0-9/18446744073709551616")]
    public void A_number_that_does_not_fit_in_a_64_bit_integer_is_refused(string value)
    {
        Assert.Null(Parse(value));
    }

    [Fact]
    public void Non_ASCII_digits_are_not_digits()
    {
        // Cifre arabo-indiche e a larghezza piena: char.IsDigit le accetterebbe, un Content-Range no.
        Assert.Null(Parse("bytes \U00000660-\U00000669/\U00000665\U00000669\U00000660"));
        Assert.Null(Parse("bytes \U0000FF10-\U0000FF19/\U0000FF15\U0000FF19\U0000FF10"));
        Assert.Null(Parse("bytes 0-9/59\U00000660"));
    }

    [Fact]
    public void The_416_form_of_the_capture_is_not_a_content_range()
    {
        // 112: Content-Range: bytes */590 con status 416 (range non soddisfacibile)
        Assert.Equal("bytes */590", WireFixtures.Headers("112-doc-content-range-unsatisfiable")["Content-Range"]);
        Assert.Null(Parse(WireFixtures.Headers("112-doc-content-range-unsatisfiable")["Content-Range"]));
    }
}
