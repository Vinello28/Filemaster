namespace Filemaster.Domain;

/// <summary>
/// Un evento consegnato dal server a un webhook, nella busta comune a tutti: l'identificatore della consegna e il
/// momento. Il tipo concreto dice quale evento e': <see cref="DocumentUploadedEvent"/>,
/// <see cref="DocumentDeletedEvent"/> e <see cref="DocumentIntegrityFailedEvent"/>; qualunque altro (un evento che il
/// server ha aggiunto dopo questa versione del client) e' un <see cref="UnknownWebhookEvent"/>, mai un errore.
/// </summary>
/// <remarks>
/// <para>
/// La busta del server ha solo l'identificatore della consegna e il momento: non c'e' l'ente (un abbonamento
/// appartiene a un ente solo) ne' un altro id dell'evento. Un evento consegnato di nuovo (il server ritenta se il ricevente
/// non risponde con successo) ha lo stesso <see cref="DeliveryId"/> e lo stesso <see cref="OccurredAt"/>: chi non vuole
/// elaborarlo due volte deduplica per <see cref="DeliveryId"/>.
/// </para>
/// <para>
/// Interpretare il corpo di una consegna e verificarne la firma e' compito di Application; qui ci sono solo i tipi.
/// </para>
/// </remarks>
public abstract record WebhookEvent(string DeliveryId, DateTimeOffset OccurredAt)
{
    /// <summary>L'identificatore della consegna, uguale a ogni ritentativo dello stesso evento (chiave di idempotenza del ricevente).</summary>
    public string DeliveryId { get; } = DeliveryId;

    /// <summary>
    /// Il momento in cui il server ha accodato la consegna (UTC), non quello dell'eventuale ritentativo e non un orario
    /// dell'ente.
    /// </summary>
    public DateTimeOffset OccurredAt { get; } = OccurredAt;
}
