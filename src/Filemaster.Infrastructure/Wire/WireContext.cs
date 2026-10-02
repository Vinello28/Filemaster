using Filemaster.Domain;

namespace Filemaster.Infrastructure;

/// <summary>
/// Quale risposta si sta leggendo: lo status HTTP e l'id di correlazione. Servono a un solo scopo: ogni lettore che trova una
/// risposta di successo non interpretabile lancia <see cref="UnexpectedResponseException"/> con lo status VERO (200, 201...) e
/// con l'id di correlazione, cosi' chi chiama puo' collegare l'errore ai log del server. I lettori del livello wire non toccano
/// la rete: ricevono i byte e questo contesto, e nessuna eccezione di <c>System.Text.Json</c> esce da loro.
/// </summary>
internal sealed class WireContext
{
    internal WireContext(int statusCode, string? requestId)
    {
        StatusCode = statusCode;
        RequestId = requestId;
    }

    /// <summary>Lo status HTTP della risposta che si sta leggendo.</summary>
    internal int StatusCode { get; }

    /// <summary>L'id di correlazione della chiamata (gia' ripulito dal trasporto); null se non noto.</summary>
    internal string? RequestId { get; }

    /// <summary>Il contesto di una risposta bufferizzata del trasporto: stesso status, stesso id di correlazione.</summary>
    internal static WireContext Of(TransportResponse response)
    {
        Guard.NotNull(response);
        return new WireContext(response.StatusCode, response.RequestId);
    }

    /// <summary>L'eccezione per una risposta di successo che non si puo' interpretare. Il messaggio non contiene mai valori della risposta.</summary>
    internal UnexpectedResponseException Unexpected(string message, Exception? innerException = null) =>
        new(message, StatusCode, problemType: null, requestId: RequestId, detail: null, innerException: innerException);
}
