using Filemaster;
using Filemaster.Application;
using Filemaster.Infrastructure;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// La registrazione del client Filemaster in <c>Microsoft.Extensions.DependencyInjection</c> (ASP.NET Core, Worker Service, host
/// generico). Sta nel namespace di <see cref="IServiceCollection"/>, come le estensioni di Microsoft, cosi' basta il riferimento al
/// pacchetto.
/// </summary>
public static class FilemasterServiceCollectionExtensions
{
    /// <summary>
    /// Il nome del client HTTP di Filemaster in <see cref="IHttpClientFactory"/> (e delle sue opzioni): serve per configurarlo per nome,
    /// per esempio da <c>ConfigureHttpClientDefaults</c> o da un <c>IHttpMessageHandlerBuilderFilter</c>.
    /// </summary>
    public const string HttpClientName = "Filemaster";

    // Le intestazioni con un segreto, oscurate nei log di IHttpClientFactory (che le scrive a livello Trace).
    private static readonly string[] RedactedHeaders = { "X-API-Key", "Authorization" };

    /// <summary>
    /// Registra il client Filemaster: <see cref="IFilemasterClient"/> e le cinque porte (<see cref="IDocumentStore"/>,
    /// <see cref="IFolderCatalog"/>, <see cref="IContactDirectory"/>, <see cref="ITenantInfo"/>, <see cref="IFilemasterHealth"/>), tutte
    /// singleton, cosi' lo strato applicativo dipende solo dalla porta che usa. Restituisce il costruttore del client HTTP nominato
    /// <see cref="HttpClientName"/>, a cui agganciare gestori propri (per esempio Polly).
    /// </summary>
    /// <param name="services">La collezione dei servizi.</param>
    /// <param name="configure">Imposta le opzioni (indirizzo, chiave, tempi, ritentativi).</param>
    /// <returns>Il costruttore del client HTTP nominato, per aggiungere gestori o configurarlo.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> o <paramref name="configure"/> e' null.</exception>
    /// <remarks>
    /// <para>
    /// <b>Opzioni controllate all'avvio.</b> Con l'host generico (ASP.NET Core, Worker Service) opzioni non valide fanno fallire l'avvio
    /// dell'host con <see cref="OptionsValidationException"/> (<c>ValidateOnStart</c>); senza host l'errore arriva alla prima
    /// risoluzione del client o di una porta. Il messaggio non riporta mai la chiave. Le opzioni si leggono una volta, alla creazione
    /// del client: un cambio successivo della configurazione non ha effetto fino al riavvio. Chiamare questo metodo una sola volta
    /// per contenitore: una seconda chiamata aggiunge un'altra <paramref name="configure"/> alle stesse opzioni.
    /// </para>
    /// <para>
    /// <b>Il client HTTP.</b> <c>Timeout</c> infinito (i tempi sono per chiamata, nelle opzioni); gestore primario senza redirect
    /// automatici (<c>X-API-Key</c> verrebbe copiata verso il nuovo indirizzo) e senza decompressione (il controllo dei download contro
    /// <c>Content-Length</c> la presuppone spenta); su .NET 5+ un <c>SocketsHttpHandler</c>, su .NET Framework un
    /// <see cref="HttpClientHandler"/> con almeno 32 connessioni per server. Le intestazioni <c>X-API-Key</c> e <c>Authorization</c>
    /// sono oscurate nei log di <see cref="IHttpClientFactory"/>. Chi sostituisce il gestore primario
    /// (<c>ConfigurePrimaryHttpMessageHandler</c>) con un <see cref="HttpClientHandler"/> o <c>SocketsHttpHandler</c> che segue i redirect
    /// o decomprime riceve <see cref="InvalidOperationException"/> alla creazione del client; un <c>Timeout</c> finito impostato dopo
    /// questa chiamata da' lo stesso errore.
    /// </para>
    /// <para>
    /// <b>Durata.</b> Il client e le porte sono singleton e si possono iniettare ovunque, anche in altri singleton: il client non tiene
    /// un <see cref="HttpClient"/>, ne chiede uno a <see cref="IHttpClientFactory"/> a ogni tentativo, quindi la rotazione dei gestori
    /// della factory (default due minuti) vale anche per lui. E' la differenza con un client tipizzato, che e' transient e,
    /// catturato da un singleton, terrebbe lo stesso gestore per sempre. Logger (<see cref="ILoggerFactory"/>) e orologio
    /// (<see cref="TimeProvider"/>, se registrato) vengono dal contenitore.
    /// </para>
    /// <para>
    /// <b>Ritentativi e Polly.</b> Il client ritenta da solo le letture (<see cref="FilemasterRetryOptions"/>). Chi aggancia una propria
    /// politica (Polly, <c>AddStandardResilienceHandler</c>) al costruttore restituito imposti <c>options.Retry.MaxAttempts = 1</c>
    /// per spegnere la nostra, altrimenti i tentativi si moltiplicano; una politica esterna vede ogni tentativo, anche i <c>POST</c>,
    /// che il client invece non ritenta mai, e un suo timeout si somma a quelli delle opzioni.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddFilemaster(options =>
    /// {
    ///     options.BaseAddress = new Uri(builder.Configuration["Filemaster:BaseAddress"]!);
    ///     options.ApiKey = builder.Configuration["Filemaster:ApiKey"];
    /// });
    ///
    /// // Altrove, nello strato applicativo:
    /// public sealed class Archivio(IDocumentStore documents) { ... }
    /// </code>
    /// </example>
    public static IHttpClientBuilder AddFilemaster(this IServiceCollection services, Action<FilemasterOptions> configure)
    {
        Filemaster.Guard.NotNull(services);
        Filemaster.Guard.NotNull(configure);

        services.AddOptions<FilemasterOptions>(HttpClientName).Configure(configure).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<FilemasterOptions>, FilemasterOptionsValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHttpMessageHandlerBuilderFilter, FilemasterHandlerCheck>());

        services.TryAddSingleton(CreateClient);
        services.TryAddSingleton(provider => provider.GetRequiredService<IFilemasterClient>().Documents);
        services.TryAddSingleton(provider => provider.GetRequiredService<IFilemasterClient>().Folders);
        services.TryAddSingleton(provider => provider.GetRequiredService<IFilemasterClient>().Contacts);
        services.TryAddSingleton(provider => provider.GetRequiredService<IFilemasterClient>().Tenant);
        services.TryAddSingleton(provider => provider.GetRequiredService<IFilemasterClient>().Health);

        return services.AddHttpClient(HttpClientName)
            .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(FilemasterHandlers.CreatePrimary)
            .RedactLoggedHeaders(RedactedHeaders);
    }

    // Il client: opzioni validate (OptionsValidationException se non lo sono), un client di prova per il Timeout (e, creando il
    // gestore, per il controllo del primario), poi la composizione su una sorgente che chiede un client alla factory a ogni tentativo.
    private static IFilemasterClient CreateClient(IServiceProvider provider)
    {
        var options = provider.GetRequiredService<IOptionsMonitor<FilemasterOptions>>().Get(HttpClientName);
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        using var probe = factory.CreateClient(HttpClientName);
        if (probe.Timeout != Timeout.InfiniteTimeSpan)
        {
            throw new InvalidOperationException(
                "Il client HTTP '" + HttpClientName + "' ha un Timeout finito: i tempi del client Filemaster sono per chiamata "
                    + "(RequestTimeout, TransferTimeout). Non impostare HttpClient.Timeout dopo AddFilemaster.");
        }

        return FilemasterHttp.Create(
            () => factory.CreateClient(HttpClientName),
            options,
            provider.GetService<ILoggerFactory>(),
            provider.GetService<TimeProvider>());
    }
}
