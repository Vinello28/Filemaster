using Filemaster.Infrastructure;
using Filemaster.UnitTests.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Filemaster.UnitTests.Composition;

/// <summary>
/// <see cref="FilemasterHttp.CreateClient"/>: argomenti e opzioni controllati prima di creare qualcosa, <c>Timeout</c> finito rifiutato,
/// cinque porte sullo STESSO trasporto (un solo avviso <c>http</c>, stesse intestazioni), l'<see cref="HttpClient"/> non toccato.
/// </summary>
public sealed class FilemasterHttpTests
{
    private static HttpClient NewHttp(HttpMessageHandler handler) => new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

    [Fact]
    public void Null_arguments_are_rejected_with_their_names()
    {
        using var http = NewHttp(new FakeHandler());

        Assert.Equal("httpClient", Assert.Throws<ArgumentNullException>(() => FilemasterHttp.CreateClient(null!, Composed.Options())).ParamName);
        Assert.Equal("options", Assert.Throws<ArgumentNullException>(() => FilemasterHttp.CreateClient(http, null!)).ParamName);
    }

    [Fact]
    public void Invalid_options_are_rejected_with_the_property_name_and_before_the_timeout()
    {
        // HttpClient di default: Timeout di 100 secondi. Le opzioni si controllano per prime.
        using var http = new HttpClient(new FakeHandler());

        var exception = Assert.Throws<ArgumentException>(() => FilemasterHttp.CreateClient(http, Composed.Options(o => o.ApiKey = "chiave con spazi")));

        Assert.Equal(nameof(FilemasterOptions.ApiKey), exception.ParamName);
        Assert.DoesNotContain("chiave con spazi", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(100_000)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void A_finite_HttpClient_timeout_is_rejected(int milliseconds)
    {
        using var http = new HttpClient(new FakeHandler()) { Timeout = TimeSpan.FromMilliseconds(milliseconds) };

        var exception = Assert.Throws<ArgumentException>(() => FilemasterHttp.CreateClient(http, Composed.Options()));

        Assert.Equal("httpClient", exception.ParamName);
        Assert.Contains("InfiniteTimeSpan", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_client_exposes_five_ports_that_stay_the_same()
    {
        using var http = NewHttp(new FakeHandler());

        var client = FilemasterHttp.CreateClient(http, Composed.Options());

        Assert.All(Composed.PortsOf(client), Assert.NotNull);
        Assert.Equal(Composed.PortsOf(client), Composed.PortsOf(client)); // stesse istanze a ogni lettura
        Assert.Equal(5, Composed.PortsOf(client).Distinct().Count());
    }

    [Fact]
    public async Task Every_port_speaks_through_the_same_HttpClient_with_the_same_headers()
    {
        var handler = new FakeHandler();
        Composed.ScriptEveryPort(handler);
        using var http = NewHttp(handler);
        var client = FilemasterHttp.CreateClient(http, Composed.Options());

        await Composed.CallEveryPortAsync(client);

        Assert.Equal(6, handler.Requests.Count);
        Assert.All(handler.Requests, sent => Assert.StartsWith(Composed.BaseAddress.AbsoluteUri, sent.Uri.AbsoluteUri, StringComparison.Ordinal));
        Assert.Single(handler.Requests.Select(sent => sent.Header("User-Agent")).Distinct());
        Assert.All(handler.Requests, sent => Assert.Matches("^[0-9a-f]{32}$", sent.Header("X-Request-ID")!));
        Assert.All(handler.Requests.Take(4), sent => Assert.Equal(Composed.Key, sent.Header("X-API-Key")));
        Assert.All(handler.Requests.Skip(4), sent => Assert.Null(sent.Header("X-API-Key"))); // le sonde sono anonime
    }

    [Fact]
    public void The_five_ports_share_one_transport_so_the_plain_http_warning_is_written_once()
    {
        using var logs = new CollectingLoggerProvider();
        using var http = NewHttp(new FakeHandler());

        FilemasterHttp.CreateClient(http, Composed.Options(o => o.BaseAddress = new Uri("http://filemaster.example.test/")), logs);

        var record = Assert.Single(logs.Records);
        Assert.Equal(Composed.TransportCategory, record.Category);
        Assert.Equal(LogLevel.Warning, record.Record.Level);
        Assert.DoesNotContain(Composed.Key, logs.Everything, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_HttpClient_is_neither_modified_nor_disposed()
    {
        var handler = new FakeHandler();
        handler.Then(Reply.Json(200, Composed.TenantJson)).Then(Reply.Json(200, "{}"));
        using var http = NewHttp(handler);

        var client = FilemasterHttp.CreateClient(http, Composed.Options());
        await client.Tenant.GetAsync();

        Assert.Null(http.BaseAddress);
        Assert.Empty(http.DefaultRequestHeaders);
        Assert.Equal(Timeout.InfiniteTimeSpan, http.Timeout);
        using var response = await http.GetAsync(new Uri("https://other.example.test/")); // ancora utilizzabile
        Assert.Equal(200, (int)response.StatusCode);
    }

    [Fact]
    public async Task The_options_are_copied_at_creation()
    {
        var handler = new FakeHandler();
        handler.Then(Reply.Json(200, Composed.TenantJson));
        using var http = NewHttp(handler);
        var options = Composed.Options();
        var client = FilemasterHttp.CreateClient(http, options);

        options.ApiKey = "saf_another";
        options.BaseAddress = new Uri("https://elsewhere.example.test/");
        await client.Tenant.GetAsync();

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(Composed.Key, sent.Header("X-API-Key"));
        Assert.Equal(Composed.BaseAddress.AbsoluteUri + "tenant", sent.Uri.AbsoluteUri);
    }
}
