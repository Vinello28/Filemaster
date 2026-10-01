namespace Filemaster.Domain;

/// <summary>
/// Il client non e' riuscito a parlare con il server: rete, DNS, connessione rifiutata, interrotta o resettata (anche
/// a meta' upload, per esempio dopo un 413 del server web). Non c'e' una risposta: <see cref="FilemasterException.StatusCode"/>
/// e' <c>0</c>. La causa (<c>HttpRequestException</c>, <c>IOException</c>, <c>SocketException</c>) e' in
/// <see cref="Exception.InnerException"/>. Il tempo scaduto e' <see cref="FilemasterTimeoutException"/>.
/// </summary>
public sealed class ConnectionException : FilemasterException
{
    private const string DefaultMessage = "Impossibile comunicare con il server: errore di rete, DNS o connessione interrotta.";

    /// <summary>Crea l'eccezione, con status 0.</summary>
    /// <param name="message">Il messaggio; null per quello di default.</param>
    /// <param name="innerException">La causa di rete.</param>
    /// <param name="requestId">L'id di correlazione inviato dal client, utile per cercare nei log del server.</param>
    public ConnectionException(string? message = null, Exception? innerException = null, string? requestId = null)
        : base(message ?? DefaultMessage, 0, null, requestId, null, innerException)
    {
    }
}
