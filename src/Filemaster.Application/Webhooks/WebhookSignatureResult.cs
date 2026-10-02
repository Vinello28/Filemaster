namespace Filemaster.Application;

/// <summary>
/// L'esito di <see cref="WebhookSignatureVerifier.Verify"/>: valido, oppure non valido con il motivo
/// (<see cref="Failure"/>). E' un valore di sola lettura che non contiene ne' il segreto ne' la firma attesa, quindi si
/// puo' scrivere nei log.
/// </summary>
/// <remarks>
/// <see cref="IsValid"/> e' derivato da <see cref="Failure"/>: non esiste un risultato "valido" con un motivo di errore.
/// Non e' un record perche' i risultati si ottengono solo dal verificatore.
/// </remarks>
public sealed class WebhookSignatureResult
{
    private static readonly WebhookSignatureResult ValidResult = new(WebhookSignatureFailure.None);

    private WebhookSignatureResult(WebhookSignatureFailure failure)
    {
        Failure = failure;
    }

    /// <summary>Vero se la firma e' valida e la consegna e' dentro la finestra di tolleranza.</summary>
    public bool IsValid => Failure == WebhookSignatureFailure.None;

    /// <summary>Il motivo del rifiuto; <see cref="WebhookSignatureFailure.None"/> se il risultato e' valido.</summary>
    public WebhookSignatureFailure Failure { get; }

    /// <summary>Il risultato valido (uno solo, condiviso).</summary>
    internal static WebhookSignatureResult Valid => ValidResult;

    /// <summary>Un risultato non valido con il motivo indicato.</summary>
    internal static WebhookSignatureResult Invalid(WebhookSignatureFailure failure) => new(failure);

    /// <summary>Una riga leggibile: <c>Valid</c>, oppure <c>Invalid (</c>motivo<c>)</c>.</summary>
    /// <returns>Il testo, senza dati riservati.</returns>
    public override string ToString() => IsValid ? "Valid" : "Invalid (" + Failure + ")";
}
