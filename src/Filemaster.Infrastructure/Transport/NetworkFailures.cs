using System.Net;
using System.Net.Sockets;

namespace Filemaster.Infrastructure;

/// <summary>Quali eccezioni sono "la rete ha fallito" (e quindi diventano <c>ConnectionException</c> o si ritentano).</summary>
internal static class NetworkFailures
{
    /// <summary>
    /// Vero per <see cref="HttpRequestException"/>, <see cref="IOException"/>, <see cref="SocketException"/> e
    /// <see cref="WebException"/> (su .NET Framework un <see cref="HttpRequestException"/> puo' avvolgere un <see cref="WebException"/>:
    /// il tipo esterno basta). Non lo e' <see cref="OperationCanceledException"/>, che si distingue dal token di chi chiama, e
    /// nemmeno un errore di programmazione (<see cref="InvalidOperationException"/>, <see cref="ArgumentException"/>).
    /// </summary>
    internal static bool IsNetworkFailure(Exception exception) =>
        exception is HttpRequestException or IOException or SocketException or WebException;
}
