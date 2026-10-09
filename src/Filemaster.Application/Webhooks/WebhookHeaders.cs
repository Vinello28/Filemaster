namespace Filemaster.Application;

/// <summary>
/// I nomi degli header HTTP che il server Sharp-a-File mette su ogni consegna di un webhook (sono quelli di
/// <c>WebhookSignature</c> nel server, ramo dev). Gli header HTTP non distinguono maiuscole e minuscole: chi li legge puo'
/// usare questi nomi cosi' come sono.
/// </summary>
public static class WebhookHeaders
{
    /// <summary>
    /// L'header della firma, <c>X-SharpAFile-Signature</c>, nella forma <c>t=&lt;secondi unix&gt;,v1=&lt;esadecimale&gt;</c>:
    /// il suo valore si passa a <see cref="WebhookSignatureVerifier.Verify"/>.
    /// </summary>
    public const string Signature = "X-SharpAFile-Signature";

    /// <summary>
    /// L'header del tipo di evento, <c>X-SharpAFile-Event</c> (per esempio <c>document.uploaded</c>). E' una comodita' per
    /// instradare la richiesta prima di leggerla: il tipo che fa fede e' il campo <c>event</c> del corpo firmato, l'header
    /// non e' coperto dalla firma.
    /// </summary>
    public const string Event = "X-SharpAFile-Event";

    /// <summary>
    /// L'header dell'identificatore della consegna, <c>X-SharpAFile-Delivery</c> (un intero decimale scritto come testo): uguale al
    /// campo <c>delivery_id</c> del corpo (li' e' un numero JSON) e a ogni ritentativo dello stesso evento. Come <see cref="Event"/>, non e' coperto dalla
    /// firma: per deduplicare si usa <c>WebhookEvent.DeliveryId</c>, letto dal corpo verificato.
    /// </summary>
    public const string Delivery = "X-SharpAFile-Delivery";
}
