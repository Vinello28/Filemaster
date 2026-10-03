namespace Filemaster.Infrastructure;

/// <summary>
/// La chiamata JSON letta in memoria di tutti gli adapter (documenti, cartelle, contatti, ente): <c>Accept: application/json</c> e,
/// se c'e' un corpo, <c>Content-Type: application/json</c> (<see cref="WireJson.ContentType"/>). Status attesi: ogni 2xx (il default
/// del trasporto). I ritentativi li decide il trasporto (solo i <c>GET</c>).
/// </summary>
internal static class JsonCalls
{
    /// <summary>Invia la richiesta con <see cref="FilemasterTransport.SendBufferedAsync"/> e restituisce la risposta letta.</summary>
    /// <param name="transport">Il trasporto.</param>
    /// <param name="method">Il metodo HTTP.</param>
    /// <param name="path">Il percorso relativo (vedi <see cref="Routes"/>), con l'eventuale query.</param>
    /// <param name="body">Il corpo JSON gia' scritto; null per nessun corpo.</param>
    /// <param name="cancellationToken">Per annullare la chiamata.</param>
    /// <returns>La risposta 2xx letta per intero.</returns>
    internal static Task<TransportResponse> SendAsync(
        FilemasterTransport transport,
        HttpMethod method,
        string path,
        byte[]? body,
        CancellationToken cancellationToken)
    {
        var request = new TransportRequest(method, path)
        {
            Customize = message =>
            {
                AcceptJson(message);
                if (body is not null)
                {
                    var content = new ByteArrayContent(body);
                    content.Headers.TryAddWithoutValidation("Content-Type", WireJson.ContentType);
                    message.Content = content;
                }
            },
        };
        return transport.SendBufferedAsync(request, cancellationToken);
    }

    /// <summary>Aggiunge <c>Accept: application/json</c> al messaggio.</summary>
    /// <param name="message">Il messaggio appena creato dal trasporto.</param>
    internal static void AcceptJson(HttpRequestMessage message) =>
        message.Headers.TryAddWithoutValidation("Accept", WireJson.ContentType);
}
