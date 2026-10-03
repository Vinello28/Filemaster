using Filemaster.Application;
using Microsoft.Extensions.Logging;

namespace Filemaster.Infrastructure;

/// <summary>
/// La composizione dell'adapter HTTP senza iniezione delle dipendenze: da un <see cref="HttpClient"/> e dalle opzioni crea il
/// <see cref="IFilemasterClient"/> (un solo trasporto condiviso dalle cinque porte). E' il livello piu' basso: chi usa
/// <c>Microsoft.Extensions.DependencyInjection</c> chiama <c>AddFilemaster</c> del pacchetto <c>Filemaster</c>, chi vuole un client
/// pronto senza DI (anche da .NET Framework o VB.NET) usa <c>FilemasterClientFactory</c> dello stesso pacchetto.
/// </summary>
/// <remarks>
/// <para>
/// <b>L'<see cref="HttpClient"/> resta di chi lo passa</b>: il client non lo smaltisce e non lo modifica (ne' <c>BaseAddress</c>,
/// ne' le intestazioni di default, ne' <c>Timeout</c>); va tenuto vivo quanto il client restituito e smaltito dopo. Puo' essere
/// condiviso con altro codice, ma le sue intestazioni di default partono anche verso il server di Filemaster.
/// </para>
/// <para>
/// <b>Il suo <c>Timeout</c> deve essere <see cref="Timeout.InfiniteTimeSpan"/></b>, altrimenti <see cref="CreateClient"/> lancia. I
/// tempi del client sono per chiamata (<see cref="FilemasterOptions.RequestTimeout"/>, <see cref="FilemasterOptions.TransferTimeout"/>):
/// con il default di <c>HttpClient</c> (100 secondi) un caricamento o un download lungo verrebbe interrotto comunque, e l'errore
/// comparirebbe solo in esercizio e solo con i file grandi. Lanciare subito, alla creazione, e' piu' sicuro che scriverlo nel log.
/// </para>
/// <para>
/// <b>Il gestore dell'<see cref="HttpClient"/></b> (che da qui non si puo' ispezionare) deve avere i redirect automatici spenti
/// (<c>AllowAutoRedirect = false</c>: <c>X-API-Key</c> verrebbe copiata nella richiesta verso il nuovo indirizzo, anche su un altro
/// host) e la decompressione automatica spenta (il conteggio dei byte dei download contro <c>Content-Length</c> presuppone un corpo
/// non compresso). La factory e <c>AddFilemaster</c> lo garantiscono da sole.
/// </para>
/// </remarks>
public static class FilemasterHttp
{
    /// <summary>
    /// Crea il client sull'<see cref="HttpClient"/> indicato. Le opzioni sono controllate (<see cref="FilemasterOptions.Validate"/>) e
    /// copiate: cambiarle dopo non ha effetto.
    /// </summary>
    /// <param name="httpClient">Il client HTTP, con <c>Timeout</c> infinito; non viene smaltito ne' modificato.</param>
    /// <param name="options">Le opzioni.</param>
    /// <param name="loggerFactory">Per gli avvisi del client (<c>http</c> su un host non di loopback, ritentativi); null per non scrivere.</param>
    /// <param name="timeProvider">L'orologio per le scadenze; null per quello di sistema (serve ai test).</param>
    /// <returns>Il client, thread-safe: se ne crea uno per applicazione e lo si riusa.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="httpClient"/> o <paramref name="options"/> e' null.</exception>
    /// <exception cref="ArgumentException">
    /// Le opzioni non sono valide (vedi <see cref="FilemasterOptions.Validate"/>: <see cref="ArgumentException.ParamName"/> e' il nome
    /// della proprieta'), oppure <c>httpClient.Timeout</c> non e' <see cref="Timeout.InfiniteTimeSpan"/> (<c>ParamName</c>
    /// <c>httpClient</c>). Le opzioni si controllano per prime.
    /// </exception>
    /// <example>
    /// <code>
    /// var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    /// var options = new FilemasterOptions { BaseAddress = new Uri("https://filemaster.example.test/"), ApiKey = chiave };
    /// IFilemasterClient client = FilemasterHttp.CreateClient(http, options);
    /// </code>
    /// </example>
    public static IFilemasterClient CreateClient(
        HttpClient httpClient,
        FilemasterOptions options,
        ILoggerFactory? loggerFactory = null,
        TimeProvider? timeProvider = null)
    {
        Guard.NotNull(httpClient);
        Guard.NotNull(options);
        options.Validate();
        if (httpClient.Timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentException(
                "L'HttpClient deve avere Timeout = Timeout.InfiniteTimeSpan: i tempi del client Filemaster sono per chiamata "
                    + "(RequestTimeout, TransferTimeout) e un Timeout finito interromperebbe i trasferimenti lunghi.",
                nameof(httpClient));
        }

        return Create(() => httpClient, options, loggerFactory, timeProvider);
    }

    /// <summary>
    /// La composizione vera, usata anche da <c>AddFilemaster</c>: UN trasporto sulla sorgente di client (chiamata a ogni tentativo,
    /// vedi <see cref="FilemasterTransport.FromSource"/>) e le cinque porte su quel trasporto. Non controlla il <c>Timeout</c> dei client:
    /// lo fa chi chiama.
    /// </summary>
    /// <param name="httpClientSource">La sorgente dei client HTTP.</param>
    /// <param name="options">Le opzioni (validate dal trasporto).</param>
    /// <param name="loggerFactory">Per il logger del trasporto; null per non scrivere.</param>
    /// <param name="timeProvider">L'orologio; null per quello di sistema.</param>
    /// <returns>La facciata.</returns>
    internal static IFilemasterClient Create(
        Func<HttpClient> httpClientSource,
        FilemasterOptions options,
        ILoggerFactory? loggerFactory,
        TimeProvider? timeProvider)
    {
        var logger = loggerFactory?.CreateLogger(typeof(FilemasterTransport).FullName!);
        var transport = FilemasterTransport.FromSource(httpClientSource, options, logger, timeProvider);
        return new HttpFilemasterClient(transport);
    }
}
