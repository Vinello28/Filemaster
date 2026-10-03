using System.Globalization;
using System.Security.Cryptography;

namespace Filemaster.IntegrationTests.Loopback;

/// <summary>
/// Una richiesta come e' arrivata sul filo: riga, intestazioni (nell'ordine, con il nome com'era scritto) e, se il gestore lo ha letto,
/// il corpo (byte fino al tetto del server, sempre conteggio e SHA-256).
/// </summary>
internal sealed class RecordedRequest
{
    private readonly object _gate = new();
    private MemoryStream? _kept;
    private readonly SHA256 _hash = SHA256.Create();
    private readonly int _keepLimit;
    private long _bodyLength;
    private bool _bodyComplete;
    private string? _sha256;

    internal RecordedRequest(int connectionId, string method, string target, string version, IReadOnlyList<KeyValuePair<string, string>> headers, int keepLimit)
    {
        ConnectionId = connectionId;
        Method = method;
        Target = target;
        Version = version;
        Headers = headers;
        _keepLimit = keepLimit;
        _kept = keepLimit > 0 ? new MemoryStream() : null;
        IsChunked = HeaderContains("Transfer-Encoding", "chunked");
        if (Header("Content-Length") is { } length)
        {
            ContentLength = long.Parse(length, NumberStyles.None, CultureInfo.InvariantCulture);
        }

        if (!IsChunked && ContentLength is null or 0)
        {
            Complete();
        }
    }

    internal int ConnectionId { get; }

    internal string Method { get; }

    /// <summary>Il bersaglio della riga di richiesta (percorso e query), com'e' scritto.</summary>
    internal string Target { get; }

    internal string Version { get; }

    internal IReadOnlyList<KeyValuePair<string, string>> Headers { get; }

    internal bool IsChunked { get; }

    internal long? ContentLength { get; }

    /// <summary>I byte del corpo letti finora.</summary>
    internal long BodyLength
    {
        get
        {
            lock (_gate)
            {
                return _bodyLength;
            }
        }
    }

    /// <summary>Vero quando il corpo e' stato letto fino alla fine (subito, se la richiesta non ne ha).</summary>
    internal bool BodyComplete
    {
        get
        {
            lock (_gate)
            {
                return _bodyComplete;
            }
        }
    }

    /// <summary>Il corpo tenuto in memoria, o null se ha superato il tetto (o il tetto e' zero).</summary>
    internal byte[]? Body
    {
        get
        {
            lock (_gate)
            {
                return _kept?.ToArray();
            }
        }
    }

    /// <summary>Lo SHA-256 del corpo (esadecimale minuscolo), disponibile a corpo completo.</summary>
    internal string? BodySha256
    {
        get
        {
            lock (_gate)
            {
                return _sha256;
            }
        }
    }

    /// <summary>Il primo valore dell'intestazione (nome senza distinzione di maiuscole), o null.</summary>
    internal string? Header(string name) =>
        Headers.Where(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value).FirstOrDefault();

    /// <summary>Quante volte l'intestazione compare.</summary>
    internal int HeaderCount(string name) => Headers.Count(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase));

#pragma warning disable CA2249 // net48 non ha string.Contains(string, StringComparison).
    internal bool HeaderContains(string name, string token) =>
        Header(name) is { } value && value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
#pragma warning restore CA2249

    internal void Append(byte[] buffer, int offset, int count)
    {
        lock (_gate)
        {
            _hash.TransformBlock(buffer, offset, count, null, 0);
            _bodyLength += count;
            if (_kept is not null)
            {
                if (_kept.Length + count <= _keepLimit)
                {
                    _kept.Write(buffer, offset, count);
                }
                else
                {
                    _kept.Dispose();
                    _kept = null;
                }
            }
        }
    }

    internal void Complete()
    {
        lock (_gate)
        {
            if (_bodyComplete)
            {
                return;
            }

            _hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            _sha256 = Hex(_hash.Hash!);
            _hash.Dispose();
            _bodyComplete = true;
        }
    }

    internal static string Hex(byte[] bytes)
    {
        var builder = new System.Text.StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
        {
            builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}
