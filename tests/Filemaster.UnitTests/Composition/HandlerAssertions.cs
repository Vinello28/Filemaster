using System.Net;

namespace Filemaster.UnitTests.Composition;

/// <summary>
/// Il gestore primario della libreria, ispezionato: senza redirect, senza decompressione; su .NET 5+ <see cref="SocketsHttpHandler"/>
/// con <c>PooledConnectionLifetime</c> di due minuti, sull'asset netstandard2.0 <see cref="HttpClientHandler"/> con almeno 32
/// connessioni per server (sul runtime moderno il default e' gia' illimitato: il minimo si vede davvero solo su .NET Framework).
/// </summary>
internal static class HandlerAssertions
{
    internal static void IsTheLibraryPrimary(HttpMessageHandler? primary)
    {
        Assert.NotNull(primary);
        if (Composed.UsesNetStandardAsset)
        {
            var handler = Assert.IsType<HttpClientHandler>(primary);
            Assert.False(handler.AllowAutoRedirect);
            Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
            Assert.True(handler.MaxConnectionsPerServer >= 32, "MaxConnectionsPerServer = " + handler.MaxConnectionsPerServer);
            return;
        }

#if NET
        var sockets = Assert.IsType<SocketsHttpHandler>(primary);
        Assert.False(sockets.AllowAutoRedirect);
        Assert.Equal(DecompressionMethods.None, sockets.AutomaticDecompression);
        Assert.Equal(TimeSpan.FromMinutes(2), sockets.PooledConnectionLifetime);
        Assert.Equal(int.MaxValue, sockets.MaxConnectionsPerServer); // non abbassato
#else
        Assert.Fail("Su .NET Framework le librerie caricate sono sempre l'asset netstandard2.0.");
#endif
    }
}
