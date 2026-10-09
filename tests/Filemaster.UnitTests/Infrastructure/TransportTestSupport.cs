using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Filemaster.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Filemaster.UnitTests.Infrastructure;

/// <summary>Una richiesta come l'ha vista il gestore finto: copiata subito, perche' il trasporto smaltisce il messaggio.</summary>
internal sealed class RecordedRequest
{
    internal RecordedRequest(HttpRequestMessage message, CancellationToken token, HttpCompletionOption? option = null)
    {
        Message = message;
        Token = token;
        Method = message.Method;
        Uri = message.RequestUri!;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in message.Headers)
        {
            // User-Agent e' una lista separata da spazi (prodotti e commenti); le altre da virgole.
            headers[header.Key] = string.Join(header.Key == "User-Agent" ? " " : ",", header.Value);
        }

        Headers = headers;
        CompletionOption = option;
    }

    internal HttpRequestMessage Message { get; }

    internal CancellationToken Token { get; }

    internal HttpMethod Method { get; }

    internal Uri Uri { get; }

    internal IReadOnlyDictionary<string, string> Headers { get; }

    internal HttpCompletionOption? CompletionOption { get; }

    internal string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

internal delegate Task<HttpResponseMessage> Step(HttpRequestMessage request, CancellationToken cancellationToken);

/// <summary>Un <see cref="HttpMessageHandler"/> scritto a mano: registra le richieste e risponde con la sequenza programmata. Nessuna rete.</summary>
internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly Queue<Step> _steps = new();

    internal List<RecordedRequest> Requests { get; } = new();

    internal List<string> Events { get; } = new();

    internal FakeHandler Then(Step step)
    {
        _steps.Enqueue(step);
        return this;
    }

    internal FakeHandler Then(HttpResponseMessage response) => Then((_, _) => Task.FromResult(response));

    internal FakeHandler ThenFail(Exception exception) => Then((_, _) => throw exception);

    internal FakeHandler ThenFailAsync(Exception exception) => Then(async (_, _) =>
    {
        await Task.Yield();
        throw exception;
    });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Come un gestore vero: con il token gia' scattato non invia nulla.
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(new RecordedRequest(request, cancellationToken));
        Events.Add("send" + Requests.Count);
        if (_steps.Count == 0)
        {
            throw new InvalidOperationException("Nessuna risposta programmata per la richiesta numero " + Requests.Count + ".");
        }

        var step = _steps.Dequeue();
        return await step(request, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Un contenuto di risposta di prova: serve uno stream scelto dal test, dichiara (o no) la lunghezza, conta quante volte e' stato
/// "serializzato" (cioe' bufferizzato per intero: con <c>ResponseHeadersRead</c> non deve succedere) e quante volte e' stato smaltito.
/// </summary>
internal sealed class StubContent : HttpContent
{
    private readonly Stream _stream;
    private readonly long? _declaredLength;

    internal StubContent(Stream stream, long? declaredLength, string? mediaType = "application/octet-stream")
    {
        _stream = stream;
        _declaredLength = declaredLength;
        if (mediaType is not null)
        {
            Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        }
    }

    internal int SerializeCalls { get; private set; }

    internal int DisposeCount { get; private set; }

    internal Action? OnDispose { get; set; }

    protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(_stream);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        SerializeCalls++;
        await _stream.CopyToAsync(stream).ConfigureAwait(false);
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _declaredLength ?? 0;
        return _declaredLength.HasValue;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeCount++;
            OnDispose?.Invoke();
        }

        base.Dispose(disposing);
    }
}

/// <summary>Come costruire le risposte finte.</summary>
internal static class Reply
{
    internal static HttpResponseMessage Json(int status, string json, string mediaType = "application/json", string? requestId = "srv-req-1")
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        return Make(status, new StubContent(new MemoryStream(bytes), bytes.Length, mediaType), requestId);
    }

    /// <summary>Un problem+json come lo scrive il server, con il suo content type; senza l'intestazione X-Request-ID (come il server).</summary>
    internal static HttpResponseMessage Problem(int status, string slug, string? detail = null, string requestId = "srv-req-1")
    {
        var json = "{\"type\":\"/problems/" + slug + "\",\"title\":\"titolo\",\"status\":" + status
            + (detail is null ? string.Empty : ",\"detail\":\"" + detail + "\"")
            + ",\"request_id\":\"" + requestId + "\"}";
        return Json(status, json, "application/problem+json", requestId: null);
    }

    /// <summary>Un corpo non JSON, come quello di un proxy (pagina HTML), senza X-Request-ID.</summary>
    internal static HttpResponseMessage Text(int status, string text = "Service Unavailable", string mediaType = "text/html")
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return Make(status, new StubContent(new MemoryStream(bytes), bytes.Length, mediaType), requestId: null);
    }

    internal static HttpResponseMessage Empty(int status, string? requestId = "srv-req-1") =>
        Make(status, new StubContent(new MemoryStream(), 0, mediaType: null), requestId);

    internal static HttpResponseMessage Bytes(int status, byte[] body, long? declaredLength, string mediaType = "application/octet-stream", string? requestId = "srv-req-1") =>
        Make(status, new StubContent(new MemoryStream(body), declaredLength, mediaType), requestId);

    internal static HttpResponseMessage Streamed(int status, Stream stream, long? declaredLength, string mediaType = "application/octet-stream", string? requestId = "srv-req-1") =>
        Make(status, new StubContent(stream, declaredLength, mediaType), requestId);

    internal static HttpResponseMessage WithHeader(this HttpResponseMessage response, string name, string value)
    {
        response.Headers.TryAddWithoutValidation(name, value);
        return response;
    }

    internal static StubContent ContentOf(this HttpResponseMessage response) => (StubContent)response.Content;

    private static HttpResponseMessage Make(int status, StubContent content, string? requestId)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = content };
        if (requestId is not null)
        {
            response.Headers.TryAddWithoutValidation("X-Request-ID", requestId);
        }

        return response;
    }
}

/// <summary>Un <see cref="ILogger"/> che raccoglie tutto: livello, testo formattato, valori strutturati ed eccezione (come testo completo).</summary>
internal sealed class CollectingLogger : ILogger
{
    internal List<LogRecord> Records { get; } = new();

    /// <summary>Tutto cio' che e' stato scritto, in un'unica stringa: per cercarci dentro un segreto.</summary>
    internal string Everything => string.Join("\n", Records.Select(r => r.Text + " | " + r.State + " | " + r.ExceptionText));

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var structured = state is IEnumerable<KeyValuePair<string, object?>> pairs
            ? string.Join(";", pairs.Select(p => p.Key + "=" + p.Value))
            : string.Empty;
        Records.Add(new LogRecord(logLevel, formatter(state, exception), structured, exception?.ToString() ?? string.Empty));
    }
}

internal sealed record LogRecord(LogLevel Level, string Text, string State, string ExceptionText);

/// <summary>Registra le attese del backoff e le completa subito: nessun test aspetta davvero.</summary>
internal sealed class DelayRecorder
{
    internal List<TimeSpan> Delays { get; } = new();

    internal Action<TimeSpan>? OnDelay { get; set; }

    internal Task Wait(TimeSpan delay, CancellationToken cancellationToken)
    {
        Delays.Add(delay);
        OnDelay?.Invoke(delay);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

/// <summary>
/// Un trasporto con tutto cio' che serve ai test: gestore finto, orologio finto, attese registrate, log raccolto, jitter fisso (0.5,
/// modificabile). Le opzioni di partenza sono valide; <paramref name="configure"/> le cambia prima della creazione.
/// </summary>
internal sealed class TransportRig : IDisposable
{
    internal const string Key = "saf_FakeKeyForTestsOnly_0123456789abcdef";

    internal TransportRig(
        Action<FilemasterOptions>? configure = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        int? maxBufferedBytes = null)
    {
        Options = new FilemasterOptions { BaseAddress = new Uri("https://filemaster.example.test/"), ApiKey = Key };
        configure?.Invoke(Options);
        Client = new HttpClient(Handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        var hooks = new TransportHooks
        {
            Delay = delay ?? Delays.Wait,
            NextJitter = () => Jitter,
            MaxBufferedBytes = maxBufferedBytes,
        };
        Transport = new FilemasterTransport(Client, Options, Log, Time, hooks);
    }

    internal FakeHandler Handler { get; } = new();

    internal HttpClient Client { get; }

    internal FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero));

    internal DelayRecorder Delays { get; } = new();

    internal CollectingLogger Log { get; } = new();

    internal FilemasterOptions Options { get; }

    internal FilemasterTransport Transport { get; }

    internal double Jitter { get; set; } = 0.5;

    internal static TransportRequest Get(string relative = "documents") => new(HttpMethod.Get, relative);

    internal static TransportRequest Post(string relative = "documents/bulk/move") => new(HttpMethod.Post, relative);

    internal static TransportRequest Delete(string relative = "documents/42") => new(HttpMethod.Delete, relative);

    internal static TransportRequest Patch(string relative = "documents/42/folder") => new(new HttpMethod("PATCH"), relative);

    public void Dispose() => Client.Dispose();
}

/// <summary>Passi di gestore finto che aspettano.</summary>
internal static class Waiting
{
    /// <summary>Un passo che segnala di essere entrato e poi aspetta per sempre, finche' il token non scatta (come una connessione che non risponde).</summary>
    internal static Step Hang(TaskCompletionSource<bool> entered) => async (_, cancellationToken) =>
    {
        entered.TrySetResult(true);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException("irraggiungibile: l'attesa infinita finisce solo con l'annullamento");
    };

    /// <summary>
    /// Un passo che segnala di essere entrato e aspetta o che il test completi <paramref name="release"/> (poi risponde con
    /// <paramref name="response"/>) o che il token scatti (poi lancia un <see cref="TaskCanceledException"/> <b>senza token</b>: lo
    /// stesso tipo e lo stesso aspetto qualunque dei due abbia fatto scattare il token).
    /// </summary>
    internal static Step UntilReleased(TaskCompletionSource<bool> entered, TaskCompletionSource<bool> release, Func<HttpResponseMessage> response) => async (_, cancellationToken) =>
    {
        entered.TrySetResult(true);
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), cancelled))
        {
            var finished = await Task.WhenAny(release.Task, cancelled.Task).ConfigureAwait(false);
            if (finished == cancelled.Task)
            {
                throw new TaskCanceledException("annullata (senza token)");
            }
        }

        return response();
    };

    internal static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Aspetta un task al massimo 10 secondi veri: se non finisce il test fallisce invece di bloccare l'esecuzione (un bug di tempo scaduto si vede cosi').</summary>
    internal static async Task Within(Task task)
    {
        var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
        if (finished != task)
        {
            throw new TimeoutException("Il task non e' finito entro 10 secondi: una lettura bloccata non e' stata rilasciata.");
        }

        await task.ConfigureAwait(false);
    }
}
