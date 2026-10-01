namespace Filemaster.Domain;

/// <summary>
/// Il server non ha risposto (o non ha finito il trasferimento) entro il tempo previsto dalle opzioni del client.
/// Non c'e' una risposta completa: <see cref="FilemasterException.StatusCode"/> e' <c>0</c>. Una cancellazione
/// richiesta dal chiamante con il suo <c>CancellationToken</c> non e' questo errore: resta un
/// <see cref="OperationCanceledException"/>.
/// </summary>
public sealed class FilemasterTimeoutException : FilemasterException
{
    private const string DefaultMessage = "Il server non ha risposto entro il tempo previsto.";

    /// <summary>Crea l'eccezione, con status 0.</summary>
    /// <param name="message">Il messaggio; null per quello di default.</param>
    /// <param name="innerException">La causa, di solito la cancellazione per tempo scaduto.</param>
    /// <param name="requestId">L'id di correlazione inviato dal client, utile per cercare nei log del server.</param>
    public FilemasterTimeoutException(string? message = null, Exception? innerException = null, string? requestId = null)
        : base(message ?? DefaultMessage, 0, null, requestId, null, innerException)
    {
    }
}
