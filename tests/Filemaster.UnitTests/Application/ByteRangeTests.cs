using Filemaster.Application;

namespace Filemaster.UnitTests.Application;

/// <summary>
/// <see cref="ByteRange"/>: le tre forme di un intervallo singolo che il server serve con 206 (<c>a-b</c>, <c>a-</c>,
/// <c>-n</c>, fixture 109-111). Un intervallo multiplo ha 200 con il contenuto intero (fixture 113), quindi non si puo'
/// esprimere; <c>b &lt; a</c> e' un intervallo non valido che il server ignora (200 intero), quindi si rifiuta.
/// </summary>
public sealed class ByteRangeTests
{
    [Fact]
    public void Between_writes_the_closed_range_a_dash_b()
    {
        var range = ByteRange.Between(0, 9);

        Assert.Equal("bytes=0-9", range.ToHeaderValue());
        Assert.Equal(0L, range.FirstByte);
        Assert.Equal(9L, range.LastByte);
        Assert.Null(range.SuffixLength);
    }

    [Fact]
    public void Between_accepts_a_single_byte()
    {
        Assert.Equal("bytes=7-7", ByteRange.Between(7, 7).ToHeaderValue());
    }

    [Fact]
    public void From_writes_the_open_range_a_dash()
    {
        var range = ByteRange.From(5);

        Assert.Equal("bytes=5-", range.ToHeaderValue());
        Assert.Equal(5L, range.FirstByte);
        Assert.Null(range.LastByte);
        Assert.Null(range.SuffixLength);
    }

    [Fact]
    public void Suffix_writes_the_range_of_the_last_n_bytes()
    {
        var range = ByteRange.Suffix(10);

        Assert.Equal("bytes=-10", range.ToHeaderValue());
        Assert.Null(range.FirstByte);
        Assert.Null(range.LastByte);
        Assert.Equal(10L, range.SuffixLength);
    }

    [Fact]
    public void Offsets_up_to_long_MaxValue_are_written_in_full()
    {
        Assert.Equal("bytes=0-9223372036854775807", ByteRange.Between(0, long.MaxValue).ToHeaderValue());
        Assert.Equal("bytes=9223372036854775807-", ByteRange.From(long.MaxValue).ToHeaderValue());
        Assert.Equal("bytes=-9223372036854775807", ByteRange.Suffix(long.MaxValue).ToHeaderValue());
    }

    [Theory]
    [InlineData(-1L, 5L, "first")]
    [InlineData(0L, -1L, "last")]
    [InlineData(5L, 4L, "last")]
    [InlineData(long.MaxValue, 0L, "last")]
    public void Between_with_a_negative_offset_or_last_before_first_throws_ArgumentOutOfRangeException(long first, long last, string paramName)
    {
        // "bytes=5-4" e' un intervallo non valido: il server lo ignora e risponde 200 con tutto il contenuto.
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => ByteRange.Between(first, last));

        Assert.Equal(paramName, exception.ParamName);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void From_with_a_negative_offset_throws_ArgumentOutOfRangeException(long first)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => ByteRange.From(first));

        Assert.Equal("first", exception.ParamName);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void Suffix_with_a_length_below_one_throws_ArgumentOutOfRangeException(long length)
    {
        // "bytes=-0" non e' soddisfacibile per definizione: nessun byte.
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => ByteRange.Suffix(length));

        Assert.Equal("length", exception.ParamName);
    }

    [Fact]
    public void Ranges_have_value_equality()
    {
        Assert.Equal(ByteRange.Between(1, 2), ByteRange.Between(1, 2));
        Assert.Equal(ByteRange.From(1), ByteRange.From(1));
        Assert.Equal(ByteRange.Suffix(1), ByteRange.Suffix(1));
        Assert.NotEqual(ByteRange.From(1), ByteRange.Suffix(1));
        Assert.NotEqual(ByteRange.Between(1, 2), ByteRange.From(1));
    }
}
