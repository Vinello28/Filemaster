using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Filemaster.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filemaster.Infrastructure;

/// <summary>
/// Il trasporto: l'unico punto che parla HTTP con il server. Riceve un <see cref="HttpClient"/> (che <b>non smaltisce</b> e di cui non
/// modifica <c>BaseAddress</c>, intestazioni di default e <c>Timeout</c>: lo stesso client puo' essere condiviso) e le opzioni (gia'
/// validate; ne tiene una copia). Gli adapter delle risorse gli chiedono "invia questa richiesta e dammi la risposta" in tre modalita'.
/// </summary>
/// <remarks>
/// <para>
/// <b>Le tre modalita'.</b> <see cref="SendBufferedAsync"/>: la risposta e' letta intera in memoria (con un tetto) entro
/// <see cref="FilemasterOptions.RequestTimeout"/>. <see cref="SendDownloadAsync"/>: <c>ResponseHeadersRead</c>;
/// <c>RequestTimeout</c> vale fino alle intestazioni, poi <see cref="FilemasterOptions.TransferTimeout"/> sul resto, e la risposta e'
/// consegnata come <see cref="DownloadStream"/> che possiede la risposta HTTP. <see cref="SendUploadAsync"/>: il corpo della richiesta
/// e' quello che l'adapter mette nel messaggio (<c>StreamContent</c>/multipart), senza bufferizzazione da parte del trasporto, con
/// <c>TransferTimeout</c> sull'intera chiamata. Tutte e tre leggono le intestazioni con <c>ResponseHeadersRead</c>, cosi' un corpo di errore si
/// legge per al piu' 16 KiB e <c>HttpClient.Timeout</c> (se non e' infinito) copre solo l'attesa delle intestazioni.
/// </para>
/// <para>
/// <b>Intestazioni, per richiesta e mai sull'<c>HttpClient</c>:</b> <c>X-API-Key</c> (mai nei log, nelle eccezioni o in un
/// <c>ToString</c>; si aggiunge senza validazione perche' un'eccezione di formato la riporterebbe), <c>X-Request-ID</c> (32 esadecimali
/// minuscoli di un GUID: nell'alfabeto che il server tiene, quindi quello che torna e' quello inviato; <b>lo stesso</b> per tutti i
/// tentativi di una chiamata) e <c>User-Agent</c> (<c>Filemaster/versione (runtime; sistema)</c>).
/// </para>
/// <para>
/// <b>Tempo.</b> Una sola <see cref="Deadline"/> per chiamata: <c>RequestTimeout</c> copre tutti i tentativi e le attese fra l'uno e
/// l'altro (non ogni tentativo a parte). L'annullamento di chi chiama e' sempre <see cref="OperationCanceledException"/>; qualunque
/// altra cancellazione (scadenza, o un <c>HttpClient.Timeout</c> finito) e' <see cref="FilemasterTimeoutException"/>. Si distingue dal
/// token di chi chiama, mai dal tipo dell'eccezione. Un timeout del client non si ritenta. <b>L'<c>HttpClient</c> deve avere
/// <c>Timeout</c> infinito</b>: con il default di 100 secondi un caricamento lungo verrebbe interrotto comunque.
/// </para>
/// <para>
/// <b>Ritentativi</b> (<see cref="RetryPolicy"/>): solo per i <c>GET</c>, solo su errori di rete, 408, 429 e 502/503/504 che non sono
/// problem+json, solo prima di consegnare la risposta, mai su uno status che la richiesta dichiara atteso (il 503 di <c>/readyz</c>); un
/// <see cref="HttpRequestMessage"/> nuovo a ogni tentativo; la risposta del tentativo fallito e' smaltita prima dell'attesa. La
/// mappatura in eccezioni (<see cref="ProblemMapper"/>) avviene solo dopo il ciclo, sull'ultima risposta. Un 413 del limite del server web
/// (che chiude la connessione) puo' arrivare come <see cref="ConnectionException"/>: non si distingue da un errore di rete.
/// </para>
/// <para>
/// <b>Upload in streaming.</b> Il trasporto non legge ne' copia il corpo. Su .NET Framework (<c>net48</c>) <c>HttpClientHandler</c> puo'
/// bufferizzare comunque il corpo della richiesta in memoria (<c>AllowWriteStreamBuffering</c>): non si risolve qui ma dove si crea il
/// gestore (il punto di estensione e' l'<c>HttpClient</c> iniettato) e <b>non e' verificato in locale: si prova su Windows CI (T6.1)</b>. Il
/// trasporto smaltisce il messaggio di richiesta (e quindi il suo contenuto) a ogni tentativo: chi passa uno stream dell'utente deve
/// metterlo in un contenuto che non lo chiude.
/// </para>
/// </remarks>
internal sealed class FilemasterTransport
{
    private const string ApiKeyHeader = "X-API-Key";
    private const string RequestIdHeader = "X-Request-ID";
    private const int DefaultMaxBufferedBytes = 32 * 1024 * 1024;

    private static readonly string UserAgent = BuildUserAgent();
    private static readonly object RandomLock = new();
    private static readonly Random SharedRandom = new();

    private readonly HttpClient _http;
    private readonly Uri _baseAddress;
    private readonly string _apiKey;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeSpan _transferTimeout;
    private readonly int _maxAttempts;
    private readonly TimeSpan _initialDelay;
    private readonly TimeSpan _maxDelay;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<double> _nextJitter;
    private readonly int _maxBufferedBytes;

    /// <summary>Crea il trasporto. Valida le opzioni (di nuovo, e' idempotente) e ne tiene una copia: cambiarle dopo non ha effetto.</summary>
    /// <param name="httpClient">L'<c>HttpClient</c> da usare; non si smaltisce e non si modifica. Dovrebbe avere <c>Timeout</c> infinito.</param>
    /// <param name="options">Le opzioni.</param>
    /// <param name="logger">Dove scrivere gli avvisi (<c>http</c> non loopback, ritentativi); null per non scrivere.</param>
    /// <param name="timeProvider">L'orologio per le scadenze e per <c>Retry-After</c> come data; null per quello di sistema.</param>
    /// <param name="hooks">Attesa, jitter e tetto del buffer sostituibili dai test; null per i valori veri.</param>
    internal FilemasterTransport(
        HttpClient httpClient,
        FilemasterOptions options,
        ILogger? logger = null,
        TimeProvider? timeProvider = null,
        TransportHooks? hooks = null)
    {
        Guard.NotNull(httpClient);
        Guard.NotNull(options);
        options.Validate();

        _http = httpClient;
        _baseAddress = options.GetNormalizedBaseAddress();
        _apiKey = options.ApiKey!;
        _requestTimeout = options.RequestTimeout;
        _transferTimeout = options.TransferTimeout;
        _maxAttempts = options.Retry.MaxAttempts;
        _initialDelay = options.Retry.InitialDelay;
        _maxDelay = options.Retry.MaxDelay;
        _logger = logger ?? NullLogger.Instance;
        _time = timeProvider ?? TimeProvider.System;
        _delay = hooks?.Delay ?? ((delay, token) => Task.Delay(delay, token));
        _nextJitter = hooks?.NextJitter ?? NextRandom;
        _maxBufferedBytes = hooks?.MaxBufferedBytes ?? DefaultMaxBufferedBytes;

        if (_baseAddress.Scheme == Uri.UriSchemeHttp && !_baseAddress.IsLoopback)
        {
            _logger.LogWarning(
                "L'indirizzo del server usa http su un host che non e' di loopback ({Host}): la chiave API viaggia in chiaro.",
                _baseAddress.Host);
        }
    }

    /// <summary>
    /// Invia la richiesta e legge la risposta intera in memoria entro <see cref="FilemasterOptions.RequestTimeout"/> (tempo totale
    /// della chiamata, tentativi compresi). Restituisce un 2xx (o uno status dichiarato atteso); ogni altro status e' un'eccezione.
    /// </summary>
    /// <param name="request">Cosa chiedere.</param>
    /// <param name="cancellationToken">Per annullare: lancia <see cref="OperationCanceledException"/>.</param>
    /// <exception cref="ConnectionException">Errore di rete (dopo i tentativi, se si ritenta).</exception>
    /// <exception cref="FilemasterTimeoutException">Il tempo e' scaduto.</exception>
    /// <exception cref="FilemasterException">Una risposta d'errore del server, mappata (vedi <see cref="ProblemMapper"/>).</exception>
    internal async Task<TransportResponse> SendBufferedAsync(TransportRequest request, CancellationToken cancellationToken)
    {
        Guard.NotNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var requestId = NewRequestId();
        using var deadline = new Deadline(_time, _requestTimeout, cancellationToken);
        return await ExecuteAsync(
            request,
            requestId,
            deadline,
            canRetry: request.Method == HttpMethod.Get,
            transfersOwnership: false,
            response => CompleteBufferedAsync(response, requestId, deadline),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Invia la richiesta e restituisce la risposta in streaming appena arrivano le intestazioni. <c>RequestTimeout</c> (tentativi
    /// compresi) vale fino alle intestazioni, poi <see cref="FilemasterOptions.TransferTimeout"/> sul resto. Lo stream restituito
    /// possiede la risposta HTTP e la scadenza: smaltirlo li rilascia. Il ritentativo vale solo prima di questo punto.
    /// </summary>
    /// <param name="request">Cosa chiedere (di norma un <c>GET</c>).</param>
    /// <param name="cancellationToken">Per annullare la chiamata e, dopo, la lettura dello stream (il token e' collegato).</param>
    /// <exception cref="ConnectionException">Errore di rete prima delle intestazioni (dopo i tentativi).</exception>
    /// <exception cref="FilemasterTimeoutException">Il tempo e' scaduto prima delle intestazioni.</exception>
    /// <exception cref="FilemasterException">Una risposta d'errore del server, mappata.</exception>
    internal async Task<DownloadResponse> SendDownloadAsync(TransportRequest request, CancellationToken cancellationToken)
    {
        Guard.NotNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var requestId = NewRequestId();
        var deadline = new Deadline(_time, _requestTimeout, cancellationToken);
        var transferred = false;
        try
        {
            var download = await ExecuteAsync(
                request,
                requestId,
                deadline,
                canRetry: request.Method == HttpMethod.Get,
                transfersOwnership: true,
                response => CompleteDownloadAsync(response, requestId, deadline, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            transferred = true;
            return download;
        }
        finally
        {
            if (!transferred)
            {
                deadline.Dispose();
            }
        }
    }

    /// <summary>
    /// Invia la richiesta con il corpo che l'adapter ha messo nel messaggio (in streaming, mai ritentata) e legge la risposta
    /// (piccola) in memoria. <see cref="FilemasterOptions.TransferTimeout"/> vale sull'intera chiamata.
    /// </summary>
    /// <param name="request">Cosa chiedere; <see cref="TransportRequest.Customize"/> imposta il corpo.</param>
    /// <param name="cancellationToken">Per annullare, anche a meta' trasferimento.</param>
    /// <exception cref="ConnectionException">Errore di rete (anche la connessione chiusa dal server dopo un 413 a meta' corpo).</exception>
    /// <exception cref="FilemasterTimeoutException">Il tempo del trasferimento e' scaduto.</exception>
    /// <exception cref="FilemasterException">Una risposta d'errore del server, mappata.</exception>
    internal async Task<TransportResponse> SendUploadAsync(TransportRequest request, CancellationToken cancellationToken)
    {
        Guard.NotNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var requestId = NewRequestId();
        using var deadline = new Deadline(_time, _transferTimeout, cancellationToken);
        return await ExecuteAsync(
            request,
            requestId,
            deadline,
            canRetry: false,
            transfersOwnership: false,
            response => CompleteBufferedAsync(response, requestId, deadline),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Un id di correlazione nuovo: 32 esadecimali minuscoli, nell'alfabeto <c>[A-Za-z0-9._-]</c> (max 64) che il server tiene.</summary>
    internal static string NewRequestId() => Guid.NewGuid().ToString("N");

    // Il ciclo dei tentativi. Ogni tentativo: invia, e se la risposta e' attesa la completa (legge il corpo o costruisce lo stream);
    // se e' transitoria e si puo' ritentare aspetta e riparte; altrimenti la mappa in un'eccezione. La risposta di un tentativo che non
    // e' consegnata si smaltisce subito (prima dell'attesa).
    private async Task<T> ExecuteAsync<T>(
        TransportRequest request,
        string requestId,
        Deadline deadline,
        bool canRetry,
        bool transfersOwnership,
        Func<HttpResponseMessage, Task<T>> complete,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage? response = null;
            var handedOver = false;
            var succeeded = false;
            var result = default(T);
            FilemasterException? error = null;
            Exception? networkFailure = null;
            TimeSpan? retryAfter = null;
            string? retryReason = null;
            try
            {
                response = await SendOnceAsync(request, requestId, deadline.Token).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if (IsExpected(request, status))
                {
                    result = await complete(response).ConfigureAwait(false);
                    handedOver = transfersOwnership;
                    succeeded = true;
                }
                else if (canRetry && attempt < _maxAttempts && RetryPolicy.IsRetryableStatus(status, response.Content?.Headers.ContentType?.MediaType))
                {
                    retryAfter = RetryPolicy.ReadRetryAfter(response.Headers.RetryAfter, _time.GetUtcNow());
                    retryReason = "risposta " + status.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                else
                {
                    error = await ReadErrorAsync(response, status, requestId, deadline).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException || NetworkFailures.IsNetworkFailure(exception))
            {
                // Prima il token di chi chiama: se e' scattato e' un annullamento, qualunque sia l'eccezione. Poi la scadenza (o una
                // cancellazione di cui non si conosce la causa, per esempio HttpClient.Timeout finito): e' il tempo scaduto.
                cancellationToken.ThrowIfCancellationRequested();
                if (exception is OperationCanceledException || deadline.Expired)
                {
                    throw new FilemasterTimeoutException(
                        "Il server non ha risposto entro il tempo previsto (RequestTimeout o TransferTimeout).",
                        exception,
                        requestId);
                }

                networkFailure = exception;
            }
            finally
            {
                if (!handedOver)
                {
                    response?.Dispose();
                }
            }

            if (succeeded)
            {
                return result!;
            }

            if (error is not null)
            {
                throw error;
            }

            if (networkFailure is not null)
            {
                if (!canRetry || attempt >= _maxAttempts)
                {
                    throw new ConnectionException(innerException: networkFailure, requestId: requestId);
                }

                retryReason = "errore di connessione";
            }

            var delay = RetryPolicy.ComputeDelay(_initialDelay, _maxDelay, attempt, _nextJitter(), retryAfter);
            _logger.LogWarning(
                "Tentativo {Attempt} di {MaxAttempts} fallito per {Method} {Path} ({Reason}): nuovo tentativo fra {DelayMs} ms [{RequestId}]",
                attempt,
                _maxAttempts,
                request.Method.Method,
                PathOf(request.RelativeUri),
                retryReason,
                (long)delay.TotalMilliseconds,
                requestId);
            await WaitAsync(delay, deadline, requestId, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsExpected(TransportRequest request, int status) =>
        request.IsExpectedStatus?.Invoke(status) ?? (status >= 200 && status <= 299);

    private async Task<HttpResponseMessage> SendOnceAsync(TransportRequest request, string requestId, CancellationToken token)
    {
        using var message = CreateMessage(request, requestId);
        return await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
    }

    // Un messaggio nuovo: un HttpRequestMessage non si reinvia. Le intestazioni si aggiungono senza validazione: la chiave e' gia'
    // validata, e una FormatException ne riporterebbe il valore.
    private HttpRequestMessage CreateMessage(TransportRequest request, string requestId)
    {
        var message = new HttpRequestMessage(request.Method, new Uri(_baseAddress, request.RelativeUri));
        try
        {
            message.Headers.TryAddWithoutValidation(ApiKeyHeader, _apiKey);
            message.Headers.TryAddWithoutValidation(RequestIdHeader, requestId);
            message.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Customize?.Invoke(message);
            return message;
        }
        catch
        {
            message.Dispose();
            throw;
        }
    }

    // La risposta attesa letta per intero (chiamate JSON, risposta di un upload). Se la scadenza scatta durante la lettura la risposta
    // viene rilasciata, cosi' una lettura bloccata fallisce anche dove il token delle letture e' ignorato (.NET Framework).
    private async Task<TransportResponse> CompleteBufferedAsync(HttpResponseMessage response, string requestId, Deadline deadline)
    {
        using var abort = deadline.Token.Register(static state => AbortResponse((HttpResponseMessage)state!), response);
        var status = (int)response.StatusCode;
        var body = await BodyReading.ReadBoundedAsync(response.Content, _maxBufferedBytes, deadline.Token).ConfigureAwait(false);
        if (body.Exceeded)
        {
            throw new UnexpectedResponseException(
                string.Format(System.Globalization.CultureInfo.InvariantCulture, "La risposta del server supera {0} byte: non si legge in memoria.", _maxBufferedBytes),
                status,
                requestId: HeaderRequestId(response) ?? requestId);
        }

        return new TransportResponse(status, HeaderRequestId(response) ?? requestId, body.Bytes, response.Headers, response.Content.Headers);
    }

    // Il download: si apre lo stream senza leggere e lo si consegna avvolto in un DownloadStream che possiede risposta e scadenza.
    // Da qui la scadenza vale per il trasferimento.
    private async Task<DownloadResponse> CompleteDownloadAsync(HttpResponseMessage response, string requestId, Deadline deadline, CancellationToken caller)
    {
        var content = response.Content;
        var raw = await BodyReading.OpenAsync(content, deadline.Token).ConfigureAwait(false);
        deadline.Restart(_transferTimeout);
        var id = HeaderRequestId(response) ?? requestId;
        var status = (int)response.StatusCode;
        var length = content.Headers.ContentLength;
        var stream = new DownloadStream(raw, length, status, id, response, deadline, caller);
        return new DownloadResponse(status, id, stream, length, response.Headers, content.Headers);
    }

    // Una risposta d'errore finale: si legge al piu' qualche KiB di corpo e la si mappa. Come per ogni lettura di corpo, alla scadenza
    // (o all'annullamento di chi chiama) la risposta viene rilasciata, cosi' un corpo che si ferma non blocca la chiamata anche dove lo
    // stream ignora il token (.NET Framework). Se il corpo non si legge per un errore di rete si mappa sul solo status (la risposta c'e'
    // stata); se invece e' la scadenza o l'annullamento a interromperlo, l'errore sale e lo classifica il chiamante (annullamento di chi
    // chiama, altrimenti tempo scaduto), uguale su ogni runtime. L'id inviato e' l'ultimo ripiego per RequestId (un 502 di un proxy non
    // ha ne' corpo ne' intestazione).
    private static async Task<FilemasterException> ReadErrorAsync(HttpResponseMessage response, int status, string requestId, Deadline deadline)
    {
        using var abort = deadline.Token.Register(static state => AbortResponse((HttpResponseMessage)state!), response);
        byte[]? body = null;
        try
        {
            var read = await BodyReading.ReadBoundedAsync(response.Content, ProblemBody.MaxBytes, deadline.Token).ConfigureAwait(false);
            body = read.Exceeded ? null : read.Bytes;
        }
        catch (Exception exception) when (NetworkFailures.IsNetworkFailure(exception) && !deadline.Token.IsCancellationRequested)
        {
            // Corpo illeggibile: la risposta c'e' stata, si mappa sullo status.
        }

        return ProblemMapper.Map(status, body, HeaderRequestId(response) ?? requestId);
    }

    private async Task WaitAsync(TimeSpan delay, Deadline deadline, string requestId, CancellationToken caller)
    {
        try
        {
            await _delay(delay, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            caller.ThrowIfCancellationRequested();
            throw new FilemasterTimeoutException(
                "Il tempo concesso alla chiamata e' scaduto durante l'attesa fra due tentativi.",
                exception,
                requestId);
        }
    }

    private static string? HeaderRequestId(HttpResponseMessage response) =>
        response.Headers.TryGetValues(RequestIdHeader, out var values)
            ? ProblemBody.CleanId(values.FirstOrDefault())
            : null;

    private static string PathOf(string relativeUri)
    {
        var query = relativeUri.IndexOf('?');
        return query < 0 ? relativeUri : relativeUri.Substring(0, query);
    }

    // Gira dentro Cancel(): non deve mai lanciare.
    private static void AbortResponse(HttpResponseMessage response)
    {
        try
        {
            response.Dispose();
        }
#pragma warning disable CA1031 // Un callback di annullamento che lancia romperebbe il Cancel() di chi lo chiama: qui si inghiotte tutto.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    // Random non e' thread-safe, il trasporto e' condiviso e netstandard2.0 non ha Random.Shared: un lock. Il jitter non e' un segreto.
    private static double NextRandom()
    {
        lock (RandomLock)
        {
#pragma warning disable CA5394 // Il jitter dei ritentativi non ha bisogno di casualita' crittografica.
            return SharedRandom.NextDouble();
#pragma warning restore CA5394
        }
    }

    private static string BuildUserAgent()
    {
        var assembly = typeof(FilemasterTransport).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0.0.0";
        var plus = version.IndexOf('+');
        if (plus >= 0)
        {
            version = version.Substring(0, plus);
        }

        var system = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows"
            : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "Linux"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macOS"
            : "other";
        return "Filemaster/" + Clean(version, allowSpace: false) + " (" + Clean(RuntimeInformation.FrameworkDescription, allowSpace: true) + "; " + system + ")";
    }

    // Solo caratteri sicuri per un valore di User-Agent (token e commento): ASCII, cifre, lettere, . _ - + e, se ammesso, lo spazio.
    private static string Clean(string value, bool allowSpace)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            var safe = (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '.' || c == '_' || c == '-' || c == '+' || (allowSpace && c == ' ');
            builder.Append(safe ? c : '_');
        }

        return builder.ToString();
    }
}
