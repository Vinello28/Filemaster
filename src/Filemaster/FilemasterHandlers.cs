using System.Net;

namespace Filemaster;

/// <summary>
/// Il gestore HTTP primario del client e le sue regole, uguali per la factory e per <c>AddFilemaster</c>:
/// <list type="bullet">
/// <item><b>niente redirect automatici</b>: <c>X-API-Key</c> e' un'intestazione della richiesta e il gestore la copierebbe nella richiesta
/// verso il nuovo indirizzo, anche su un altro host (toglie solo <c>Authorization</c>);</item>
/// <item><b>niente decompressione automatica</b>: il conteggio dei byte dei download contro <c>Content-Length</c> presuppone il corpo
/// cosi' come e' arrivato;</item>
/// <item>su .NET 5+ <c>SocketsHttpHandler</c> con <c>PooledConnectionLifetime</c> (le connessioni si rinnovano e seguono un cambio di
/// DNS anche in un client che vive quanto il processo); su netstandard2.0 (cioe' .NET Framework) <c>HttpClientHandler</c> con
/// <c>MaxConnectionsPerServer</c> portato almeno a <see cref="MinConnectionsPerServer"/> (su .NET Framework il default e' 2, e un
/// download aperto piu' un elenco bloccherebbero la terza chiamata).</item>
/// </list>
/// </summary>
internal static class FilemasterHandlers
{
    /// <summary>Il minimo di connessioni per server del gestore su netstandard2.0 (il valore si alza, non si abbassa mai).</summary>
    internal const int MinConnectionsPerServer = 32;

    /// <summary>Quanto vive al massimo una connessione del gestore su .NET 5+.</summary>
    internal static readonly TimeSpan PooledConnectionLifetime = TimeSpan.FromMinutes(2);

    /// <summary>Crea il gestore primario con le regole del tipo.</summary>
    /// <returns>Il gestore; lo smaltisce chi lo crea (o l'<c>HttpClient</c> a cui lo si affida).</returns>
    internal static HttpMessageHandler CreatePrimary()
    {
#if NET
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = PooledConnectionLifetime,
        };
#else
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
        };
        if (handler.MaxConnectionsPerServer < MinConnectionsPerServer)
        {
            handler.MaxConnectionsPerServer = MinConnectionsPerServer;
        }

        return handler;
#endif
    }

    /// <summary>
    /// Cerca nel gestore (scendendo i <see cref="DelegatingHandler"/> fino a quello primario) un'impostazione che viola le regole del
    /// tipo. Si riconoscono <see cref="HttpClientHandler"/> e, su .NET 5+, <c>SocketsHttpHandler</c>; un gestore di altro tipo (per
    /// esempio uno finto dei test) non si puo' ispezionare e passa.
    /// </summary>
    /// <param name="handler">Il gestore; null passa.</param>
    /// <returns>Il motivo del rifiuto, oppure null se il gestore va bene.</returns>
    internal static string? FindUnsafeSetting(HttpMessageHandler? handler)
    {
        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler;
        }

        return handler switch
        {
#if NET
            SocketsHttpHandler sockets => Check(sockets.AllowAutoRedirect, sockets.AutomaticDecompression),
#endif
            HttpClientHandler client => Check(client.AllowAutoRedirect, client.AutomaticDecompression),
            _ => null,
        };
    }

    private static string? Check(bool allowAutoRedirect, DecompressionMethods decompression)
    {
        if (allowAutoRedirect)
        {
            return "Il gestore HTTP segue i redirect (AllowAutoRedirect = true): la chiave X-API-Key verrebbe copiata nella richiesta verso il nuovo indirizzo. Impostare AllowAutoRedirect = false.";
        }

        if (decompression != DecompressionMethods.None)
        {
            return "Il gestore HTTP decomprime le risposte (AutomaticDecompression diverso da None): il controllo dei download contro Content-Length presuppone il corpo non compresso. Impostare AutomaticDecompression = DecompressionMethods.None.";
        }

        return null;
    }
}
