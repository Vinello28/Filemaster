using System.Globalization;
using Filemaster.Infrastructure;

namespace Filemaster.UnitTests.Wire;

/// <summary>
/// Le date sul filo. Lettura: RFC 3339 con il fuso scritto, mai l'ora locale (una data senza fuso non vale). Scrittura: sempre un istante UTC con
/// la <c>Z</c>, con la cultura invariante anche se la cultura corrente e' un'altra (ar-SA usa il calendario Um Al Qura: senza la cultura
/// invariante l'anno uscirebbe 1448). Gli istanti attesi sono scritti a mano.
/// </summary>
public sealed class WireDatesTests
{
    public static TheoryData<string, long> ValidInstants() => new()
    {
        // testo, ticks UTC dall'epoca 0001-01-01 calcolati a mano da un DateTimeOffset costruito con i componenti (vedi sotto)
        { "2026-10-01T09:59:12Z", Ticks(2026, 10, 1, 9, 59, 12, 0) },
        { "2026-10-01T09:59:12.9531Z", Ticks(2026, 10, 1, 9, 59, 12, 9531000) },
        { "2026-10-01T09:59:12.5Z", Ticks(2026, 10, 1, 9, 59, 12, 5000000) },
        { "2026-10-01T09:59:12.511018Z", Ticks(2026, 10, 1, 9, 59, 12, 5110180) },
        { "2026-10-01T09:59:12.1234567Z", Ticks(2026, 10, 1, 9, 59, 12, 1234567) },
        { "2026-10-01T09:59:12.0000001Z", Ticks(2026, 10, 1, 9, 59, 12, 1) },
        { "2026-10-01T11:59:12+02:00", Ticks(2026, 10, 1, 9, 59, 12, 0) },
        { "2026-10-01T04:29:12-05:30", Ticks(2026, 10, 1, 9, 59, 12, 0) },
        { "2026-10-01T09:59:12-00:00", Ticks(2026, 10, 1, 9, 59, 12, 0) },
        { "9999-12-31T23:59:59.9999999Z", Ticks(9999, 12, 31, 23, 59, 59, 9999999) },
        { "0001-01-01T00:00:00Z", Ticks(1, 1, 1, 0, 0, 0, 0) },
        { "2024-02-29T12:00:00Z", Ticks(2024, 2, 29, 12, 0, 0, 0) },
    };

    private static long Ticks(int year, int month, int day, int hour, int minute, int second, long fraction) =>
        new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero).UtcTicks + fraction;

    [Theory]
    [MemberData(nameof(ValidInstants))]
    public void A_date_with_an_offset_is_read_as_the_exact_instant(string text, long utcTicks)
    {
        Assert.True(WireDates.TryParse(text, out var value), text);

        Assert.Equal(utcTicks, value.UtcTicks);
    }

    [Fact]
    public void A_Z_date_has_a_zero_offset_and_a_date_with_an_offset_keeps_it()
    {
        Assert.True(WireDates.TryParse("2026-10-01T09:59:12Z", out var z));
        Assert.True(WireDates.TryParse("2026-10-01T11:59:12+02:00", out var plus));

        Assert.Equal(TimeSpan.Zero, z.Offset);
        Assert.Equal(TimeSpan.FromHours(2), plus.Offset);
    }

    [Theory]
    [InlineData("2026-10-01T09:59:12")]
    [InlineData("2026-10-01T09:59:12.5")]
    [InlineData("2026-10-01")]
    [InlineData("2026-10-01 09:59:12Z")]
    [InlineData("2026-10-01T09:59:12z")]
    [InlineData("2026-10-01t09:59:12Z")]
    [InlineData(" 2026-10-01T09:59:12Z")]
    [InlineData("2026-10-01T09:59:12Z ")]
    [InlineData("2026-10-01T09:59:12+02")]
    [InlineData("2026-10-01T09:59:12+02:00:00")]
    [InlineData("2026-10-01T09:59:12.12345678Z")]
    [InlineData("2026-02-30T09:59:12Z")]
    [InlineData("2026-13-01T09:59:12Z")]
    [InlineData("2026-10-01T24:00:00Z")]
    [InlineData("2026-10-01T09:60:12Z")]
    [InlineData("2026-10-01T09:59:60Z")]
    [InlineData("2026-10-01T09:59:12+15:00")]
    [InlineData("26-10-01T09:59:12Z")]
    [InlineData("")]
    [InlineData("ieri")]
    [InlineData("1790848752")]
    public void A_date_without_an_offset_or_with_another_shape_is_refused(string text)
    {
        Assert.False(WireDates.TryParse(text, out _), text);
    }

    [Fact]
    public void A_null_text_is_refused_without_throwing()
    {
        Assert.False(WireDates.TryParse(null, out var value));
        Assert.Equal(default, value);
    }

    [Theory]
    [InlineData("9999-12-31T23:59:59-01:00")]
    [InlineData("0001-01-01T00:00:00+01:00")]
    public void An_instant_whose_UTC_value_is_out_of_range_is_refused_without_throwing(string text)
    {
        Assert.False(WireDates.TryParse(text, out _));
    }

    [Theory]
    [InlineData("2026-10-01T09:59:12+0200")]
    [InlineData("2026-10-01T09:59:12.Z")]
    public void The_two_oddities_that_the_server_own_parser_tolerates_are_tolerated_too(string text)
    {
        // Pinned: gli stessi due formati di QueryParsing.Bound del server accettano un fuso senza due punti e un punto senza decimali.
        Assert.True(WireDates.TryParse(text, out var value), text);

        Assert.Equal(0, value.Ticks % TimeSpan.TicksPerSecond);
    }

    [Fact]
    public void A_date_is_never_read_as_local_time()
    {
        // Con un fuso diverso da quello della macchina il risultato resta quello scritto nel testo.
        Assert.True(WireDates.TryParse("2026-01-15T12:00:00Z", out var z));
        Assert.True(WireDates.TryParse("2026-07-15T12:00:00Z", out var summer));

        Assert.Equal(TimeSpan.Zero, z.Offset);
        Assert.Equal(TimeSpan.Zero, summer.Offset);
    }

    public static TheoryData<string, string> Formatted() => new()
    {
        { "2026-10-01T10:00:00Z", "2026-10-01T10:00:00Z" },
        { "2026-10-01T12:00:00+02:00", "2026-10-01T10:00:00Z" },
        { "2026-10-01T01:00:00+02:00", "2026-09-30T23:00:00Z" },
        { "2026-10-01T10:00:00-05:30", "2026-10-01T15:30:00Z" },
        { "2026-10-01T10:00:00.5Z", "2026-10-01T10:00:00.5Z" },
        { "2026-10-01T10:00:00.1234567Z", "2026-10-01T10:00:00.1234567Z" },
        { "2026-10-01T10:00:00.0000001Z", "2026-10-01T10:00:00.0000001Z" },
        { "0001-01-01T00:00:00Z", "0001-01-01T00:00:00Z" },
        { "9999-12-31T23:59:59.9999999Z", "9999-12-31T23:59:59.9999999Z" },
    };

    [Theory]
    [MemberData(nameof(Formatted))]
    public void A_date_is_written_as_a_UTC_instant_with_a_Z_and_no_trailing_zero_decimals(string input, string expected)
    {
        var value = DateTimeOffset.ParseExact(input, "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", CultureInfo.InvariantCulture, DateTimeStyles.None);

        Assert.Equal(expected, WireDates.Format(value));
    }

    [Theory]
    [InlineData("ar-SA")]
    [InlineData("it-IT")]
    [InlineData("th-TH")]
    [InlineData("fa-IR")]
    public void Writing_does_not_depend_on_the_current_culture(string culture)
    {
        var value = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.FromHours(2));

        WireTest.WithCulture(culture, () =>
        {
            Assert.Equal("2026-10-01T10:00:00Z", WireDates.Format(value));
        });
    }

    [Theory]
    [InlineData("ar-SA")]
    [InlineData("it-IT")]
    [InlineData("th-TH")]
    public void Reading_does_not_depend_on_the_current_culture(string culture)
    {
        WireTest.WithCulture(culture, () =>
        {
            Assert.True(WireDates.TryParse("2026-10-01T09:59:12.5Z", out var value));
            Assert.Equal(Ticks(2026, 10, 1, 9, 59, 12, 5000000), value.UtcTicks);
        });
    }

    [Fact]
    public void The_cultures_used_by_these_tests_really_differ_from_the_invariant_one()
    {
        // Autoverifica: se ar-SA non cambiasse l'anno, i due test sopra passerebbero a vuoto.
        WireTest.WithCulture("ar-SA", () =>
        {
            var value = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

            Assert.NotEqual("2026-10-01T12:00:00Z", value.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.CurrentCulture));
        });
    }
}
