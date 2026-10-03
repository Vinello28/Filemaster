using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace Filemaster.IntegrationTests.Loopback;

/// <summary>
/// Una richiesta ricevuta dal server di loopback e la connessione su cui rispondere. Le risposte sono programmabili: normale, chunked,
/// troncata (dichiara N byte e chiude dopo M &lt; N), solo intestazioni con il corpo a pezzi, chiusura pulita o reset (RST).
/// Ogni attesa ha il limite <see cref="LoopbackServer.Limit"/> dentro l'helper.
/// </summary>
internal sealed class LoopbackExchange
{
    private readonly LoopbackServer _server;
    private readonly TcpClient _client;
    private readonly Stream _stream;
    private readonly HttpWireReader _reader;

    internal LoopbackExchange(LoopbackServer server, TcpClient client, Stream stream, HttpWireReader reader, RecordedRequest request)
    {
        _server = server;
        _client = client;
        _stream = stream;
        _reader = reader;
        Request = request;
    }

    internal RecordedRequest Request { get; }

    /// <summary>Il percorso della richiesta senza la query.</summary>
    internal string Path => Request.Target.Split('?')[0];

    internal bool Responded { get; private set; }

    internal bool Closed { get; private set; }

    /// <summary>Legge tutto il corpo della richiesta (Content-Length o chunked).</summary>
    internal Task ReadBodyAsync() => _reader.ReadBodyAsync(Request, long.MaxValue);

    /// <summary>Legge il corpo finche' non ne ha almeno <paramref name="count"/> byte (o finche' finisce).</summary>
    internal Task ReadBodyAsync(long count) => _reader.ReadBodyAsync(Request, count);

    /// <summary>Risponde con un corpo testuale UTF-8 (JSON per default) e Content-Length.</summary>
    internal Task RespondAsync(int status, string body = "", string contentType = "application/json; charset=utf-8", params string[] headers) =>
        RespondAsync(status, Encoding.UTF8.GetBytes(body), body.Length == 0 && status is 204 or 304 ? null : contentType, headers);

    /// <summary>Risponde con i byte dati e Content-Length; <paramref name="headers"/> sono righe "Nome: valore".</summary>
    internal async Task RespondAsync(int status, byte[] body, string? contentType, params string[] headers)
    {
        await WriteHeadAsync(status, contentType, body.Length, headers).ConfigureAwait(false);
        await WriteAsync(body).ConfigureAwait(false);
        Responded = true;
    }

    /// <summary>Risponde con Transfer-Encoding chunked, un pezzo per ogni elemento di <paramref name="chunks"/>.</summary>
    internal async Task RespondChunkedAsync(int status, string contentType, params byte[][] chunks)
    {
        await WriteHeadAsync(status, contentType, contentLength: null, "Transfer-Encoding: chunked").ConfigureAwait(false);
        foreach (var chunk in chunks.Where(c => c.Length > 0))
        {
            await WriteAsync(Encoding.ASCII.GetBytes(chunk.Length.ToString("x", CultureInfo.InvariantCulture) + "\r\n")).ConfigureAwait(false);
            await WriteAsync(chunk).ConfigureAwait(false);
            await WriteAsync(Encoding.ASCII.GetBytes("\r\n")).ConfigureAwait(false);
        }

        await WriteAsync(Encoding.ASCII.GetBytes("0\r\n\r\n")).ConfigureAwait(false);
        Responded = true;
    }

    /// <summary>Dichiara <c>Content-Length</c> = lunghezza di <paramref name="body"/>, ne manda solo <paramref name="sent"/> byte e chiude (FIN).</summary>
    internal async Task RespondTruncatedAsync(int status, byte[] body, int sent, string contentType, params string[] headers)
    {
        await WriteHeadAsync(status, contentType, body.Length, headers).ConfigureAwait(false);
        await WriteAsync(body.Take(sent).ToArray()).ConfigureAwait(false);
        Close();
    }

    /// <summary>Scrive riga di stato e intestazioni (con Content-Length se dato). Il corpo lo scrive chi chiama con <see cref="WriteAsync"/>.</summary>
    internal async Task WriteHeadAsync(int status, string? contentType, long? contentLength, params string[] headers)
    {
        var head = new StringBuilder();
        head.Append("HTTP/1.1 ").Append(status.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(Reason(status)).Append("\r\n");
        head.Append("Date: ").Append(DateTimeOffset.UtcNow.ToString("r", CultureInfo.InvariantCulture)).Append("\r\n");
        if (contentType is not null)
        {
            head.Append("Content-Type: ").Append(contentType).Append("\r\n");
        }

        if (contentLength is { } length)
        {
            head.Append("Content-Length: ").Append(length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        }

        foreach (var header in headers)
        {
            head.Append(header).Append("\r\n");
        }

        head.Append("\r\n");
        await WriteAsync(Encoding.ASCII.GetBytes(head.ToString())).ConfigureAwait(false);
        Responded = true;
    }

    /// <summary>Scrive byte grezzi sulla connessione e li spinge subito.</summary>
    internal async Task WriteAsync(byte[] bytes)
    {
#pragma warning disable CA1835 // net48 non ha l'overload con Memory: la stessa forma con array ovunque.
        await _stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
#pragma warning restore CA1835
        await _stream.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>Aspetta <paramref name="signal"/> (dal test) al massimo <see cref="LoopbackServer.Limit"/>; false se il limite scade o il server si ferma.</summary>
    internal async Task<bool> WaitAsync(Task signal)
    {
        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (_server.Stopping.Register(() => stopped.TrySetResult(true)))
        {
            var finished = await Task.WhenAny(signal, Task.Delay(LoopbackServer.Limit), stopped.Task).ConfigureAwait(false);
            return finished == signal;
        }
    }

    /// <summary>Non legge e non risponde: tiene la connessione ferma finche' il server si ferma (o il limite scade).</summary>
    internal Task HoldAsync() => WaitAsync(new TaskCompletionSource<bool>().Task);

    /// <summary>Non risponde: legge finche' il client chiude la connessione (o il limite scade, o il server si ferma).</summary>
    internal Task HangUntilClientClosesAsync() => WaitAsync(_reader.DrainUntilClosedAsync());

    /// <summary>Chiude la connessione in modo ordinato (FIN), senza rispondere oltre.</summary>
    internal void Close()
    {
        Closed = true;
        _client.Close();
    }

    /// <summary>Chiude la connessione con un reset (RST: linger a zero), come un server che la abbatte a meta' corpo.</summary>
    internal void Reset()
    {
        Closed = true;
        _client.LingerState = new LingerOption(true, 0);
        _client.Close();
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        201 => "Created",
        204 => "No Content",
        206 => "Partial Content",
        301 => "Moved Permanently",
        302 => "Found",
        303 => "See Other",
        307 => "Temporary Redirect",
        308 => "Permanent Redirect",
        413 => "Payload Too Large",
        503 => "Service Unavailable",
        _ => "Status",
    };
}
