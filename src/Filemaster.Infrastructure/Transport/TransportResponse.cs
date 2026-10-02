using System.Net.Http.Headers;

namespace Filemaster.Infrastructure;

/// <summary>
/// Una risposta letta per intero in memoria (chiamate JSON e risposta di un caricamento): lo status, il corpo, le intestazioni.
/// La risposta HTTP e' gia' smaltita; le intestazioni restano leggibili.
/// </summary>
internal sealed class TransportResponse
{
    internal TransportResponse(int statusCode, string? requestId, byte[] body, HttpResponseHeaders headers, HttpContentHeaders contentHeaders)
    {
        StatusCode = statusCode;
        RequestId = requestId;
        Body = body;
        Headers = headers;
        ContentHeaders = contentHeaders;
    }

    /// <summary>Lo status HTTP: un 2xx, oppure uno status che la richiesta ha dichiarato atteso (per esempio il 503 di <c>/readyz</c>).</summary>
    internal int StatusCode { get; }

    /// <summary>L'id di correlazione: l'intestazione <c>X-Request-ID</c> della risposta, altrimenti quello inviato dal client.</summary>
    internal string? RequestId { get; }

    /// <summary>Il corpo (vuoto per un 204).</summary>
    internal byte[] Body { get; }

    /// <summary>Le intestazioni della risposta.</summary>
    internal HttpResponseHeaders Headers { get; }

    /// <summary>Le intestazioni del contenuto (<c>Content-Type</c>, <c>Content-Length</c>...).</summary>
    internal HttpContentHeaders ContentHeaders { get; }

    /// <summary>Il tipo di contenuto senza parametri (per esempio <c>application/json</c>); null se manca.</summary>
    internal string? MediaType => ContentHeaders.ContentType?.MediaType;
}

/// <summary>
/// Un download consegnato in streaming: lo stato, le intestazioni e lo <see cref="Content"/>, che <b>possiede la risposta HTTP e la
/// scadenza del trasferimento</b>: smaltirlo li rilascia (finche' non si smaltisce, la connessione resta occupata). Chi costruisce
/// un oggetto di piu' alto livello sopra questo stream e fallisce prima di consegnarlo deve smaltirlo.
/// </summary>
internal sealed class DownloadResponse
{
    internal DownloadResponse(int statusCode, string? requestId, Stream content, long? contentLength, HttpResponseHeaders headers, HttpContentHeaders contentHeaders)
    {
        StatusCode = statusCode;
        RequestId = requestId;
        Content = content;
        ContentLength = contentLength;
        Headers = headers;
        ContentHeaders = contentHeaders;
    }

    /// <summary>Lo status HTTP: un 2xx (200 o 206).</summary>
    internal int StatusCode { get; }

    /// <summary>L'id di correlazione: l'intestazione <c>X-Request-ID</c> della risposta, altrimenti quello inviato dal client.</summary>
    internal string? RequestId { get; }

    /// <summary>Lo stream dei byte (un <see cref="DownloadStream"/>), di sola lettura e in avanti.</summary>
    internal Stream Content { get; }

    /// <summary>Il <c>Content-Length</c> della risposta; null se manca (corpo <c>chunked</c>).</summary>
    internal long? ContentLength { get; }

    /// <summary>Le intestazioni della risposta.</summary>
    internal HttpResponseHeaders Headers { get; }

    /// <summary>Le intestazioni del contenuto (<c>Content-Type</c>, <c>Content-Disposition</c>, <c>Content-Range</c>, <c>Last-Modified</c>...).</summary>
    internal HttpContentHeaders ContentHeaders { get; }
}
