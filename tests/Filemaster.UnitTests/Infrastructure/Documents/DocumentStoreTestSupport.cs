// Su net8/net10 CA2016 chiede di inoltrare un token a ReadAsByteArrayAsync, ma su net48 l'overload con token non esiste.
#pragma warning disable CA2016

using System.Net.Http.Headers;
using System.Text;
using Filemaster.Domain;
using Filemaster.Infrastructure;
using Filemaster.UnitTests.Wire;

namespace Filemaster.UnitTests.Infrastructure.Documents;

/// <summary>
/// Una richiesta come l'ha ricevuta il gestore finto, <b>corpo compreso</b>: il corpo si legge dentro il passo del gestore, perche'
/// dopo la chiamata il trasporto ha gia' smaltito il messaggio (e il suo contenuto).
/// </summary>
internal sealed class SentRequest
{
    internal SentRequest(HttpMethod method, Uri uri, IReadOnlyDictionary<string, string> headers, byte[]? body, IReadOnlyDictionary<string, string> contentHeaders, long? declaredLength)
    {
        Method = method;
        Uri = uri;
        Headers = headers;
        Body = body;
        ContentHeaders = contentHeaders;
        DeclaredLength = declaredLength;
    }

    internal HttpMethod Method { get; }

    internal Uri Uri { get; }

    /// <summary>Il percorso con la query, come lo vede il server (<c>/documents?limit=1</c>).</summary>
    internal string PathAndQuery => Uri.PathAndQuery;

    internal IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>I byte del corpo; null se la richiesta non aveva contenuto.</summary>
    internal byte[]? Body { get; }

    internal IReadOnlyDictionary<string, string> ContentHeaders { get; }

    /// <summary>Il <c>Content-Length</c> che il contenuto dichiarava PRIMA di essere letto; null se ignoto (corpo <c>chunked</c>).</summary>
    internal long? DeclaredLength { get; }

    internal string BodyText => Encoding.UTF8.GetString(Body ?? Array.Empty<byte>());

    internal string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;

    internal string? ContentHeader(string name) => ContentHeaders.TryGetValue(name, out var value) ? value : null;
}

/// <summary>Un adapter dei documenti su un trasporto con gestore finto (<see cref="TransportRig"/>), che registra ogni richiesta col suo corpo.</summary>
internal sealed class StoreRig : IDisposable
{
    internal static readonly DocumentId Id = new("doc_01M3VEESG5KBYR5PYAJ0TDT4B2");
    internal static readonly DocumentId OtherId = new("doc_01M3VEESPZ9C3HMABKJ9X24PAS");
    internal static readonly DocumentId ThirdId = new("doc_01M3VEESSR30BNB50JNTGF31D8");

    internal StoreRig(Action<FilemasterOptions>? configure = null)
    {
        Rig = new TransportRig(configure);
        Store = new HttpDocumentStore(Rig.Transport);
    }

    internal TransportRig Rig { get; }

    internal FakeHandler Handler => Rig.Handler;

    internal HttpDocumentStore Store { get; }

    internal List<SentRequest> Sent { get; } = new();

    internal SentRequest Single => Assert.Single(Sent);

    /// <summary>Al prossimo invio registra la richiesta (corpo compreso) e risponde con <paramref name="response"/>.</summary>
    internal StoreRig Then(Func<HttpResponseMessage> response) => Then(async request =>
    {
        await Task.Yield();
        return response();
    });

    /// <summary>Al prossimo invio registra la richiesta (corpo compreso) e poi esegue <paramref name="step"/>.</summary>
    internal StoreRig Then(Func<HttpRequestMessage, Task<HttpResponseMessage>> step)
    {
        Handler.Then(async (request, _) =>
        {
            Sent.Add(await RecordAsync(request).ConfigureAwait(false));
            return await step(request).ConfigureAwait(false);
        });
        return this;
    }

    /// <summary>Al prossimo invio registra la richiesta e fallisce con <paramref name="exception"/> (un errore di rete).</summary>
    internal StoreRig ThenFail(Exception exception) => Then(_ => throw exception);

    public void Dispose() => Rig.Dispose();

    private static async Task<SentRequest> RecordAsync(HttpRequestMessage request)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers)
        {
            headers[header.Key] = string.Join(",", header.Value);
        }

        var contentHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        byte[]? body = null;
        long? declared = null;
        if (request.Content is { } content)
        {
            declared = content.Headers.ContentLength;
            foreach (var header in content.Headers)
            {
                contentHeaders[header.Key] = string.Join(",", header.Value);
            }

            body = await content.ReadAsByteArrayAsync().ConfigureAwait(false);
        }

        return new SentRequest(request.Method, request.RequestUri!, headers, body, contentHeaders, declared);
    }
}

/// <summary>Risposte costruite dalle fixture catturate (<c>Wire/Fixtures/captured</c>).</summary>
internal static class FixtureReply
{
    /// <summary>Il corpo JSON catturato con lo status catturato (o quello indicato).</summary>
    internal static HttpResponseMessage Json(string fixture, int? status = null)
    {
        var bytes = WireFixtures.Captured(fixture);
        return Reply.Bytes(status ?? WireFixtures.Status(fixture), bytes, bytes.Length, "application/json");
    }

    /// <summary>
    /// Un download con le intestazioni catturate (<c>.headers</c>: <c>Content-Type</c>, <c>Content-Length</c>, <c>Content-Range</c>,
    /// <c>Content-Disposition</c>, <c>Last-Modified</c>, <c>X-Request-ID</c>) e lo status catturato. Il corpo non e' stato catturato: e'
    /// <paramref name="body"/>, di default tanti byte quanti dice <c>Content-Length</c>.
    /// </summary>
    internal static HttpResponseMessage Download(string fixture, byte[]? body = null, int? status = null)
    {
        var headers = WireFixtures.Headers(fixture);
        var length = long.Parse(headers["Content-Length"], System.Globalization.CultureInfo.InvariantCulture);
        body ??= StreamReading.Pattern((int)length);
        var response = Reply.Bytes(status ?? WireFixtures.Status(fixture), body, length, "application/octet-stream", requestId: headers["X-Request-ID"]);
        var content = response.Content;
        content.Headers.Remove("Content-Type");
        foreach (var name in new[] { "Content-Type", "Content-Range", "Content-Disposition", "Last-Modified" })
        {
            if (headers.TryGetValue(name, out var value))
            {
                Assert.True(content.Headers.TryAddWithoutValidation(name, value), name);
            }
        }

        return response;
    }
}

/// <summary>Una parte di un corpo multipart letta dai byte: le intestazioni (testo grezzo) e il contenuto.</summary>
internal sealed class FormPart
{
    internal FormPart(IReadOnlyDictionary<string, string> headers, byte[] content)
    {
        Headers = headers;
        Content = content;
    }

    internal IReadOnlyDictionary<string, string> Headers { get; }

    internal byte[] Content { get; }

    internal string Text => Encoding.UTF8.GetString(Content);

    internal string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>
/// Un lettore minimo di <c>multipart/form-data</c> (RFC 7578), indipendente da <c>System.Net.Http</c>: cerca il delimitatore nei byte, cosi'
/// regge un contenuto binario. E' l'oracolo dei test del caricamento.
/// </summary>
internal static class Multipart
{
    internal static IReadOnlyList<FormPart> Parse(SentRequest request)
    {
        var contentType = request.ContentHeader("Content-Type");
        Assert.NotNull(contentType);
        var media = MediaTypeHeaderValue.Parse(contentType!);
        Assert.Equal("multipart/form-data", media.MediaType);
        var boundary = media.Parameters.Single(p => p.Name == "boundary").Value!.Trim('"');
        return Parse(request.Body!, boundary);
    }

    internal static IReadOnlyList<FormPart> Parse(byte[] body, string boundary)
    {
        var delimiter = Encoding.ASCII.GetBytes("--" + boundary);
        var parts = new List<FormPart>();
        var start = IndexOf(body, delimiter, 0);
        Assert.True(start >= 0, "manca il primo delimitatore");
        var position = start + delimiter.Length;
        while (true)
        {
            // Dopo il delimitatore: "--" chiude il corpo, CRLF apre una parte.
            if (body[position] == (byte)'-' && body[position + 1] == (byte)'-')
            {
                return parts;
            }

            Assert.Equal((byte)'\r', body[position]);
            Assert.Equal((byte)'\n', body[position + 1]);
            position += 2;
            var headerEnd = IndexOf(body, Encoding.ASCII.GetBytes("\r\n\r\n"), position);
            Assert.True(headerEnd >= 0, "intestazioni della parte senza fine");
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in Encoding.ASCII.GetString(body, position, headerEnd - position).Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var colon = line.IndexOf(':');
                Assert.False(headers.ContainsKey(line.Substring(0, colon)), "intestazione ripetuta: " + line);
                headers[line.Substring(0, colon)] = line.Substring(colon + 1).Trim();
            }

            var contentStart = headerEnd + 4;
            var next = IndexOf(body, Encoding.ASCII.GetBytes("\r\n--" + boundary), contentStart);
            Assert.True(next >= 0, "parte senza delimitatore finale");
            var content = new byte[next - contentStart];
            Array.Copy(body, contentStart, content, 0, content.Length);
            parts.Add(new FormPart(headers, content));
            position = next + 2 + delimiter.Length;
        }
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int from)
    {
        for (var i = from; i <= haystack.Length - needle.Length; i++)
        {
            var j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j])
            {
                j++;
            }

            if (j == needle.Length)
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>Uno stream dell'utente che conta letture e smaltimenti e puo' dichiararsi riposizionabile o no.</summary>
internal sealed class UserStream : MemoryStream
{
    private readonly bool _canSeek;

    internal UserStream(byte[] data, bool canSeek = true)
        : base(data)
    {
        _canSeek = canSeek;
    }

    internal int DisposeCount { get; private set; }

    internal int ReadCalls { get; private set; }

    public override bool CanSeek => _canSeek && base.CanSeek;

    public override long Length => _canSeek ? base.Length : throw new NotSupportedException();

    public override long Position
    {
        get => _canSeek ? base.Position : throw new NotSupportedException();
        set => base.Position = _canSeek ? value : throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ReadCalls++;
        return base.Read(buffer, offset, count);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ReadCalls++;
        return base.ReadAsync(buffer, offset, count, cancellationToken);
    }

#if NET
    public override int Read(Span<byte> buffer)
    {
        ReadCalls++;
        return base.Read(buffer);
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ReadCalls++;
        return base.ReadAsync(buffer, cancellationToken);
    }
#endif

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeCount++;
        }

        base.Dispose(disposing);
    }
}
