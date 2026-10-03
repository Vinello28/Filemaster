using Filemaster.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Filemaster;

/// <summary>
/// Crea un client Filemaster pronto senza iniezione delle dipendenze: per un'applicazione .NET Framework, WinForms, un servizio
/// Windows, uno script, o codice VB.NET. Chi usa <c>Microsoft.Extensions.DependencyInjection</c> chiama invece <c>AddFilemaster</c>.
/// </summary>
/// <remarks>
/// <para>
/// Il client restituito si crea <b>una volta per applicazione</b> e si smaltisce alla chiusura: possiede un <see cref="HttpClient"/>
/// (con <c>Timeout</c> infinito: i tempi sono quelli delle opzioni, per chiamata) e il suo pool di connessioni.
/// </para>
/// <para>
/// <b>Il gestore.</b> Senza <c>handler</c> la factory crea il proprio, con i redirect e la decompressione automatici spenti; su .NET 5+
/// e' un <c>SocketsHttpHandler</c> che rinnova le connessioni ogni due minuti (segue i cambi di DNS), su .NET Framework un
/// <see cref="HttpClientHandler"/> con almeno 32 connessioni per server (il default di .NET Framework e' 2). Su .NET Framework
/// <see cref="HttpClientHandler"/> non segue i cambi di DNS di un server gia' contattato finche' la connessione resta aperta.
/// </para>
/// <para>
/// Con un <c>handler</c> proprio (proxy, certificati, un gestore finto nei test) <b>il gestore resta di chi lo passa</b>: il client non
/// lo smaltisce, nemmeno con <see cref="FilemasterClient.Dispose"/>, e chi lo ha creato lo smaltisce dopo il client. Se il gestore (o
/// quello primario sotto una catena di <see cref="DelegatingHandler"/>) e' un <see cref="HttpClientHandler"/> o un
/// <c>SocketsHttpHandler</c>, deve avere <c>AllowAutoRedirect = false</c> e <c>AutomaticDecompression = None</c>, altrimenti la factory
/// lancia; un gestore di altro tipo non si puo' controllare e la regola resta a chi lo scrive. Su .NET Framework, un caricamento
/// grande puo' essere bufferizzato in memoria dal gestore di sistema (non verificato).
/// </para>
/// <para>
/// <b>Da C# 7.3 o VB.NET</b> (il default di un progetto .NET Framework) le opzioni si impostano con le proprieta': non hanno
/// <c>init</c>. Il client non va creato a ogni chiamata ne' smaltito dopo ognuna.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // All'avvio dell'applicazione (C#, anche 7.3):
/// var options = new FilemasterOptions();
/// options.BaseAddress = new Uri("https://filemaster.example.test/");
/// options.ApiKey = chiave;
/// FilemasterClient client = FilemasterClientFactory.Create(options);
/// // ... client.Documents.GetAsync(id) per tutta la vita dell'applicazione ...
/// // Alla chiusura:
/// client.Dispose();
/// </code>
/// In VB.NET: <c>Dim client = FilemasterClientFactory.Create(New FilemasterOptions With {.BaseAddress = New Uri("https://filemaster.example.test/"), .ApiKey = chiave})</c>,
/// tenuto in un campo condiviso e smaltito alla chiusura.
/// </example>
public static class FilemasterClientFactory
{
    /// <summary>
    /// Crea il client. Le opzioni sono controllate (<see cref="FilemasterOptions.Validate"/>) e copiate: cambiarle dopo non ha effetto.
    /// </summary>
    /// <param name="options">Le opzioni.</param>
    /// <param name="handler">
    /// Il gestore HTTP da usare, oppure null per quello della factory. Un gestore passato qui non viene smaltito dal client.
    /// </param>
    /// <param name="loggerFactory">Per gli avvisi del client; null per non scrivere.</param>
    /// <returns>Il client: lo si tiene per tutta la vita dell'applicazione e lo si smaltisce alla chiusura.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> e' null.</exception>
    /// <exception cref="ArgumentException">
    /// Le opzioni non sono valide (<see cref="ArgumentException.ParamName"/> e' il nome della proprieta'), oppure <paramref name="handler"/>
    /// segue i redirect o decomprime le risposte (<c>ParamName</c> <c>handler</c>). Niente viene creato.
    /// </exception>
    public static FilemasterClient Create(FilemasterOptions options, HttpMessageHandler? handler = null, ILoggerFactory? loggerFactory = null)
    {
        Guard.NotNull(options);
        options.Validate();
        if (handler is not null)
        {
            var problem = FilemasterHandlers.FindUnsafeSetting(handler);
            if (problem is not null)
            {
                throw new ArgumentException(problem, nameof(handler));
            }
        }

        var ownsHandler = handler is null;
        var primary = handler ?? FilemasterHandlers.CreatePrimary();
        var http = new HttpClient(primary, disposeHandler: ownsHandler) { Timeout = Timeout.InfiniteTimeSpan };
        try
        {
            var inner = FilemasterHttp.CreateClient(http, options, loggerFactory);
            return new FilemasterClient(inner, http, primary, ownsHandler);
        }
        catch
        {
            http.Dispose();
            throw;
        }
    }
}
