namespace Filemaster.Domain;

/// <summary>
/// Radice di tutti gli errori che il client segnala dopo aver parlato (o tentato di parlare) con il server.
/// Un tipo per ogni ramo di comportamento: chi chiama sceglie il <c>catch</c> senza confrontare messaggi.
/// Le violazioni lato client (argomento null, id o codice malformato, richiesta incompleta) non sono qui: sono
/// <see cref="ArgumentException"/> e <see cref="ArgumentNullException"/>, lanciate prima di toccare la rete.
/// </summary>
/// <remarks>
/// <para>
/// Proprieta' comuni. <see cref="StatusCode"/>: lo status HTTP della risposta, <c>0</c> se non c'e' stata una risposta
/// (errore del client o di rete). <see cref="ProblemType"/>: lo slug del <c>type</c> del corpo problem+json, senza il
/// prefisso <c>/problems/</c> (il server scrive <c>/problems/not-found</c>, qui c'e' <c>not-found</c>), null se il
/// corpo non c'era o non era problem+json. <see cref="RequestId"/>: l'id di correlazione con i log del server; sugli
/// errori si legge dal campo <c>request_id</c> del corpo (l'header <c>X-Request-ID</c> non c'e' sulle risposte
/// problem+json), altrimenti dall'header. <see cref="Detail"/>: il campo <c>detail</c> del corpo, in italiano ma con
/// possibili frammenti inglesi di ASP.NET: va mostrato o registrato, mai usato per decidere.
/// </para>
/// <para>
/// Mappatura di riferimento dal server (Sharp-a-File, ramo <c>dev</c>), che applica Infrastructure: prima lo slug,
/// poi lo status quando lo slug manca o non e' riconosciuto; fa eccezione il 415, dove vale sempre lo status.
/// </para>
/// <list type="bullet">
/// <item><description>400 <c>validation-error</c>: <see cref="InvalidRequestException"/> (il server non emette mai 422).</description></item>
/// <item><description>401 <c>unauthorized</c>: <see cref="UnauthorizedException"/>.</description></item>
/// <item><description>403 <c>forbidden</c> (scope insufficiente o ente sospeso): <see cref="ForbiddenException"/>.</description></item>
/// <item><description>404 <c>not-found</c> (anche id malformato, risorsa di un altro ente, route inesistente): <see cref="NotFoundException"/>.</description></item>
/// <item><description>409 <c>conflict</c>: <see cref="ConflictException"/>.</description></item>
/// <item><description>409 <c>content-unavailable</c> (documento importato con i soli metadati): <see cref="ContentUnavailableException"/>. Ha lo stesso status del conflitto: si distingue solo per slug.</description></item>
/// <item><description>413 <c>request-too-large</c> (due percorsi: limite dello store e limite del server web, che chiude la connessione): <see cref="RequestTooLargeException"/>.</description></item>
/// <item><description>415 qualunque slug (<c>unsupported-media-type</c>, ma anche <c>error</c> e <c>validation-error</c> quando manca o e' sbagliato il Content-Type): <see cref="UnsupportedMediaTypeException"/>.</description></item>
/// <item><description>500 <c>internal-error</c>, ogni altro 5xx (anche con slug <c>error</c>) e i 502/503/504 che non sono problem+json: <see cref="ServerErrorException"/>.</description></item>
/// <item><description>503 <c>storage-not-configured</c> (password dell'archivio non impostata): <see cref="StorageNotConfiguredException"/>.</description></item>
/// <item><description>405 <c>method-not-allowed</c>, 416 senza corpo, slug <c>error</c> con uno status che non e' 5xx ne' 415, qualunque status non previsto, e una risposta di successo non interpretabile (JSON non valido, campo mancante): <see cref="UnexpectedResponseException"/>.</description></item>
/// <item><description>Nessuna risposta (<see cref="StatusCode"/> 0): <see cref="ConnectionException"/> per rete, DNS o connessione interrotta, <see cref="FilemasterTimeoutException"/> per il tempo scaduto.</description></item>
/// <item><description>Download troncato o con hash diverso dal previsto, a risposta 200 o 206 gia' arrivata: <see cref="ContentIntegrityException"/>.</description></item>
/// </list>
/// <para>
/// Il 503 di <c>/readyz</c> non e' problem+json (<c>{"status":"unavailable"}</c>) e non e' un'eccezione: e' l'esito
/// di una sonda di salute. La classe e' astratta: ogni errore ha il suo tipo concreto, tutti <c>sealed</c>.
/// </para>
/// </remarks>
public abstract class FilemasterException : Exception
{
    /// <summary>Inizializza l'eccezione. Chi deriva passa tutti i campi: nessuno e' opzionale qui.</summary>
    /// <param name="message">Il messaggio, mai vuoto.</param>
    /// <param name="statusCode">Lo status HTTP della risposta, <c>0</c> se non c'e' stata.</param>
    /// <param name="problemType">Lo slug del problem+json, senza <c>/problems/</c>; null se assente.</param>
    /// <param name="requestId">L'id di correlazione con i log del server; null se non noto.</param>
    /// <param name="detail">Il campo <c>detail</c> del problem+json; null se assente.</param>
    /// <param name="innerException">La causa, se c'e'.</param>
    protected FilemasterException(
        string message,
        int statusCode,
        string? problemType,
        string? requestId,
        string? detail,
        Exception? innerException)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        ProblemType = problemType;
        RequestId = requestId;
        Detail = detail;
    }

    /// <summary>Lo status HTTP della risposta; <c>0</c> se non c'e' stata una risposta (errore del client o di rete).</summary>
    public int StatusCode { get; }

    /// <summary>Lo slug del problem+json (per esempio <c>not-found</c>), senza il prefisso <c>/problems/</c>; null se assente.</summary>
    public string? ProblemType { get; }

    /// <summary>L'id di correlazione con i log del server; null se non noto.</summary>
    public string? RequestId { get; }

    /// <summary>Il campo <c>detail</c> del problem+json (testo per le persone, non per la logica); null se assente.</summary>
    public string? Detail { get; }
}
