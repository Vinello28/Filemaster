namespace Filemaster.Application;

/// <summary>
/// Perche' una firma di webhook non e' stata accettata. Serve a registrare il motivo nei log del ricevitore, non a
/// rispondere in modo diverso al mittente: per chi spedisce la risposta giusta a qualunque rifiuto e' la stessa (401 o 400
/// senza spiegazioni), cosi' chi non ha il segreto non impara nulla.
/// </summary>
public enum WebhookSignatureFailure
{
    /// <summary>Nessun errore: la firma e' valida e la consegna e' dentro la finestra di tolleranza.</summary>
    None = 0,

    /// <summary>L'header della firma non c'e': e' null, vuoto o fatto di soli spazi.</summary>
    MissingHeader = 1,

    /// <summary>
    /// L'header non ha la forma <c>t=&lt;secondi&gt;,v1=&lt;esadecimale&gt;</c>: un elemento senza <c>=</c> (anche vuoto), nessun
    /// <c>t</c> o piu' di uno, un <c>t</c> che non e' un intero senza segno in cifre ASCII o che non sta in un <see cref="long"/>,
    /// nessun <c>v1</c>.
    /// </summary>
    MalformedHeader = 2,

    /// <summary>
    /// La firma e' valida ma il tempo <c>t</c> e' troppo lontano dall'ora del ricevitore, nel passato (consegna vecchia o
    /// riproposta) o nel futuro (orologi sfasati). Si controlla solo dopo la firma.
    /// </summary>
    TimestampOutOfTolerance = 3,

    /// <summary>
    /// Nessuna delle firme <c>v1</c> dell'header corrisponde a quella calcolata con il segreto sul corpo ricevuto: segreto
    /// sbagliato, corpo alterato o riscritto (anche solo ricodificato o re-indentato), o firma inventata.
    /// </summary>
    SignatureMismatch = 4,
}
