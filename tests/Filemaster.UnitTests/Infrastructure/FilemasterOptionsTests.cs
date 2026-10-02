using Filemaster.Infrastructure;

namespace Filemaster.UnitTests.Infrastructure;

/// <summary>
/// <see cref="FilemasterOptions"/>: i default, ogni regola di <see cref="FilemasterOptions.Validate"/> (valori validi, bordi,
/// null, schema, query e frammento, chiave con spazi o a capo o fuori ASCII, tempi), la barra finale dell'indirizzo base e il fatto
/// che la chiave API non compaia mai in <c>ToString()</c> ne' nei messaggi delle eccezioni di validazione.
/// </summary>
public sealed class FilemasterOptionsTests
{
    // Una chiave inventata e distintiva: se compare in un messaggio o in un ToString si trova con una ricerca.
    private const string FakeKey = "saf_FakeKeyForTestsOnly_0123456789abcdef";

    private static FilemasterOptions Valid() =>
        new() { BaseAddress = new Uri("https://filemaster.example.test/"), ApiKey = FakeKey };

    private static ArgumentException Rejected(FilemasterOptions options, string paramName)
    {
        var exception = Assert.Throws<ArgumentException>(options.Validate);
        Assert.Equal(paramName, exception.ParamName);
        Assert.DoesNotContain(FakeKey, exception.Message, StringComparison.Ordinal);
        return exception;
    }

    private static ArgumentOutOfRangeException RejectedRange(FilemasterOptions options, string paramName)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
        Assert.Equal(paramName, exception.ParamName);
        Assert.DoesNotContain(FakeKey, exception.Message, StringComparison.Ordinal);
        return exception;
    }

    [Fact]
    public void Defaults_are_30_seconds_30_minutes_and_a_non_null_retry_with_3_attempts()
    {
        var options = new FilemasterOptions();

        Assert.Null(options.BaseAddress);
        Assert.Null(options.ApiKey);
        Assert.Equal(TimeSpan.FromSeconds(30), options.RequestTimeout);
        Assert.Equal(TimeSpan.FromMinutes(30), options.TransferTimeout);
        Assert.NotNull(options.Retry);
        Assert.Equal(3, options.Retry.MaxAttempts);
        Assert.Equal(TimeSpan.FromMilliseconds(500), options.Retry.InitialDelay);
        Assert.Equal(TimeSpan.FromSeconds(10), options.Retry.MaxDelay);
    }

    [Fact]
    public void Valid_options_pass_and_the_default_retry_is_valid_too()
    {
        Valid().Validate();
    }

    [Fact]
    public void Setting_Retry_to_null_throws_and_leaves_the_previous_value()
    {
        var options = Valid();
        var before = options.Retry;

        var exception = Assert.Throws<ArgumentNullException>(() => options.Retry = null!);

        Assert.Equal("value", exception.ParamName);
        Assert.Same(before, options.Retry);
    }

    [Fact]
    public void A_replaced_Retry_is_the_one_that_gets_validated()
    {
        var options = Valid();
        options.Retry = new FilemasterRetryOptions { MaxAttempts = 0 };

        var exception = RejectedRange(options, nameof(FilemasterRetryOptions.MaxAttempts));

        Assert.Equal(0, exception.ActualValue);
    }

    // ----- BaseAddress -----

    [Fact]
    public void A_missing_BaseAddress_is_rejected()
    {
        var options = Valid();
        options.BaseAddress = null;

        Rejected(options, nameof(FilemasterOptions.BaseAddress));
    }

    [Fact]
    public void A_relative_BaseAddress_is_rejected()
    {
        var options = Valid();
        options.BaseAddress = new Uri("/storage/", UriKind.Relative);

        Rejected(options, nameof(FilemasterOptions.BaseAddress));
    }

    [Theory]
    [InlineData("ftp://filemaster.example.test/")]
    [InlineData("file:///tmp/filemaster/")]
    [InlineData("ws://filemaster.example.test/")]
    [InlineData("mailto:someone@example.test")]
    public void A_BaseAddress_that_is_not_http_or_https_is_rejected(string address)
    {
        var options = Valid();
        options.BaseAddress = new Uri(address);

        Rejected(options, nameof(FilemasterOptions.BaseAddress));
    }

    [Theory]
    [InlineData("http://filemaster.example.test/")]
    [InlineData("HTTPS://filemaster.example.test")]
    [InlineData("https://filemaster.example.test:8443/")]
    [InlineData("https://filemaster.example.test/storage")]
    [InlineData("https://filemaster.example.test/storage/")]
    [InlineData("https://filemaster.example.test/a/b/c")]
    [InlineData("http://127.0.0.1:5000")]
    [InlineData("http://[::1]:5000/")]
    public void Http_and_https_addresses_are_accepted_with_or_without_a_path_prefix(string address)
    {
        var options = Valid();
        options.BaseAddress = new Uri(address);

        options.Validate();
    }

    [Theory]
    [InlineData("https://filemaster.example.test/?x=1")]
    [InlineData("https://filemaster.example.test/storage?x=1")]
    [InlineData("https://filemaster.example.test/storage/#section")]
    [InlineData("https://filemaster.example.test/#")]
    [InlineData("https://filemaster.example.test/?")]
    public void A_BaseAddress_with_a_query_or_a_fragment_is_rejected_even_when_empty(string address)
    {
        var options = Valid();
        options.BaseAddress = new Uri(address);

        Rejected(options, nameof(FilemasterOptions.BaseAddress));
    }

    [Theory]
    [InlineData("https://filemaster.example.test", "https://filemaster.example.test/")]
    [InlineData("https://filemaster.example.test/", "https://filemaster.example.test/")]
    [InlineData("https://filemaster.example.test/proxy", "https://filemaster.example.test/proxy/")]
    [InlineData("https://filemaster.example.test/proxy/", "https://filemaster.example.test/proxy/")]
    [InlineData("http://filemaster.example.test:8080/a/b", "http://filemaster.example.test:8080/a/b/")]
    public void The_normalized_address_always_ends_with_a_slash(string address, string expected)
    {
        var options = Valid();
        options.BaseAddress = new Uri(address);
        options.Validate();

        var normalized = options.GetNormalizedBaseAddress();

        Assert.Equal(expected, normalized.AbsoluteUri);
        Assert.Equal(new Uri(address), options.BaseAddress); // Validate e la normalizzazione non cambiano la proprieta'
    }

    [Theory]
    [InlineData("https://filemaster.example.test", "documents", "https://filemaster.example.test/documents")]
    [InlineData("https://filemaster.example.test/proxy", "documents", "https://filemaster.example.test/proxy/documents")]
    [InlineData("https://filemaster.example.test/proxy/", "documents/doc_X/content", "https://filemaster.example.test/proxy/documents/doc_X/content")]
    [InlineData("https://filemaster.example.test/a/b", "folders?parent_id=FATTURE", "https://filemaster.example.test/a/b/folders?parent_id=FATTURE")]
    [InlineData("https://filemaster.example.test/proxy", "documents?created_from=2026-01-01T10:00:00Z", "https://filemaster.example.test/proxy/documents?created_from=2026-01-01T10:00:00Z")]
    public void A_relative_path_without_a_leading_slash_keeps_the_path_prefix_of_the_normalized_address(string address, string relative, string expected)
    {
        var options = Valid();
        options.BaseAddress = new Uri(address);
        options.Validate();

        var resolved = new Uri(options.GetNormalizedBaseAddress(), relative);

        Assert.Equal(expected, resolved.AbsoluteUri);
    }

    [Fact]
    public void Without_the_normalization_a_path_prefix_would_be_lost()
    {
        // Documenta perche' serve la barra finale: senza, il percorso relativo sostituisce l'ultimo segmento del prefisso.
        var resolved = new Uri(new Uri("https://filemaster.example.test/proxy"), "documents");

        Assert.Equal("https://filemaster.example.test/documents", resolved.AbsoluteUri);
    }

    // ----- ApiKey -----

    [Fact]
    public void A_null_or_empty_ApiKey_is_rejected()
    {
        var options = Valid();
        options.ApiKey = null;
        Rejected(options, nameof(FilemasterOptions.ApiKey));

        options.ApiKey = string.Empty;
        Rejected(options, nameof(FilemasterOptions.ApiKey));
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("saf_with space")]
    [InlineData(" saf_leading")]
    [InlineData("saf_trailing ")]
    [InlineData("saf_tab\there")]
    [InlineData("saf_line\nfeed")]
    [InlineData("saf_carriage\rreturn")]
    [InlineData("saf_crlf\r\nX-Evil: 1")]
    [InlineData("saf_nul\0byte")]
    [InlineData("saf_del\u007Fchar")]
    [InlineData("saf_accent\U000000E9")]
    [InlineData("saf_\U00004E2D\U00006587")]
    [InlineData("saf_nbsp\U000000A0here")]
    [InlineData("saf_line\U00002028separator")]
    public void An_ApiKey_with_a_space_a_control_character_or_a_non_ASCII_character_is_rejected_without_echoing_it(string key)
    {
        var options = Valid();
        options.ApiKey = key;

        var exception = Rejected(options, nameof(FilemasterOptions.ApiKey));

        if (key.Length > 1) // un solo carattere (lo spazio) compare per forza nel testo fisso del messaggio
        {
            Assert.DoesNotContain(key, exception.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("a")]
    [InlineData("!")]
    [InlineData("~")]
    [InlineData("saf_AbC-123_xyz.0+/=")]
    [InlineData("!\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~")]
    public void An_ApiKey_of_visible_ASCII_characters_is_accepted_from_the_first_to_the_last_printable(string key)
    {
        var options = Valid();
        options.ApiKey = key;

        options.Validate();
    }

    [Fact]
    public void ToString_never_contains_the_ApiKey_but_says_whether_one_is_set()
    {
        var options = Valid();

        var text = options.ToString();

        Assert.DoesNotContain(FakeKey, text, StringComparison.Ordinal);
        Assert.DoesNotContain("saf_", text, StringComparison.Ordinal);
        Assert.Contains("***", text, StringComparison.Ordinal);
        Assert.Contains("https://filemaster.example.test/", text, StringComparison.Ordinal);
        Assert.Contains("(non impostata)", new FilemasterOptions().ToString(), StringComparison.Ordinal);
    }

    // ----- Tempi -----

    [Theory]
    [InlineData(1)]
    [InlineData(30_000)]
    [InlineData(int.MaxValue)]
    public void A_positive_RequestTimeout_up_to_int_MaxValue_milliseconds_is_accepted(int milliseconds)
    {
        var options = Valid();
        options.RequestTimeout = TimeSpan.FromMilliseconds(milliseconds);

        options.Validate();
    }

    [Fact]
    public void RequestTimeout_zero_negative_infinite_or_too_long_is_rejected()
    {
        foreach (var value in new[]
        {
            TimeSpan.Zero,
            TimeSpan.FromTicks(-1),
            TimeSpan.FromSeconds(-30),
            Timeout.InfiniteTimeSpan,
            TimeSpan.FromMilliseconds(int.MaxValue) + TimeSpan.FromTicks(1),
            TimeSpan.MaxValue,
        })
        {
            var options = Valid();
            options.RequestTimeout = value;

            var exception = RejectedRange(options, nameof(FilemasterOptions.RequestTimeout));

            Assert.Equal(value, exception.ActualValue);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1_800_000)]
    [InlineData(int.MaxValue)]
    public void A_positive_TransferTimeout_up_to_int_MaxValue_milliseconds_is_accepted(int milliseconds)
    {
        var options = Valid();
        options.TransferTimeout = TimeSpan.FromMilliseconds(milliseconds);

        options.Validate();
    }

    [Fact]
    public void TransferTimeout_can_be_infinite_and_it_is_the_only_timeout_that_can()
    {
        var options = Valid();
        options.TransferTimeout = Timeout.InfiniteTimeSpan;
        options.Validate();

        options.RequestTimeout = Timeout.InfiniteTimeSpan;
        RejectedRange(options, nameof(FilemasterOptions.RequestTimeout));
    }

    [Fact]
    public void TransferTimeout_zero_negative_or_too_long_is_rejected()
    {
        foreach (var value in new[]
        {
            TimeSpan.Zero,
            TimeSpan.FromTicks(-1),
            TimeSpan.FromMinutes(-1),
            TimeSpan.FromMilliseconds(int.MaxValue) + TimeSpan.FromTicks(1),
            TimeSpan.MaxValue,
        })
        {
            var options = Valid();
            options.TransferTimeout = value;

            var exception = RejectedRange(options, nameof(FilemasterOptions.TransferTimeout));

            Assert.Equal(value, exception.ActualValue);
        }
    }

    [Fact]
    public void Validation_goes_in_the_order_of_the_properties()
    {
        var options = new FilemasterOptions
        {
            BaseAddress = new Uri("ftp://filemaster.example.test/"),
            ApiKey = "with space",
            RequestTimeout = TimeSpan.Zero,
            TransferTimeout = TimeSpan.Zero,
            Retry = new FilemasterRetryOptions { MaxAttempts = 0 },
        };
        Assert.Equal(nameof(FilemasterOptions.BaseAddress), Assert.Throws<ArgumentException>(options.Validate).ParamName);

        options.BaseAddress = new Uri("https://filemaster.example.test/");
        Assert.Equal(nameof(FilemasterOptions.ApiKey), Assert.Throws<ArgumentException>(options.Validate).ParamName);

        options.ApiKey = FakeKey;
        Assert.Equal(nameof(FilemasterOptions.RequestTimeout), Assert.Throws<ArgumentOutOfRangeException>(options.Validate).ParamName);

        options.RequestTimeout = TimeSpan.FromSeconds(1);
        Assert.Equal(nameof(FilemasterOptions.TransferTimeout), Assert.Throws<ArgumentOutOfRangeException>(options.Validate).ParamName);

        options.TransferTimeout = TimeSpan.FromSeconds(1);
        Assert.Equal(nameof(FilemasterRetryOptions.MaxAttempts), Assert.Throws<ArgumentOutOfRangeException>(options.Validate).ParamName);
    }
}
