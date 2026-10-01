using System.Text.Json;

namespace Filemaster.Domain;

/// <summary>
/// Un evento che questa versione del client non sa interpretare, per esempio un tipo che il server ha aggiunto dopo.
/// Conserva il tipo grezzo e il payload cosi' come sono, senza validarli e senza lanciare: chi li vuole li legge,
/// tutti gli altri lo ignorano.
/// </summary>
/// <remarks>
/// Come per <see cref="Document"/>: <see cref="Payload"/> e' un <see cref="JsonElement"/>, quindi due eventi con lo stesso
/// contenuto ma elementi di <see cref="JsonDocument"/> diversi non sono uguali, e l'elemento non e' piu' valido se il suo
/// <see cref="JsonDocument"/> viene smaltito: chi costruisce l'evento da un corpo letto con un <see cref="JsonDocument"/>
/// deve farne <c>Clone()</c> (lo fa Application, non questo tipo).
/// </remarks>
public sealed record UnknownWebhookEvent(string DeliveryId, DateTimeOffset OccurredAt, string EventType, JsonElement Payload)
    : WebhookEvent(DeliveryId, OccurredAt)
{
    /// <summary>
    /// Il tipo dell'evento come l'ha scritto il server (il campo <c>event</c> della consegna), senza trim e senza cambio
    /// di maiuscole; puo' essere vuoto.
    /// </summary>
    public string EventType { get; } = EventType;

    /// <summary>Il payload dell'evento com'e' (il campo <c>payload</c> della consegna); <c>ValueKind</c> <c>Undefined</c> se non c'era.</summary>
    public JsonElement Payload { get; } = Payload;
}
