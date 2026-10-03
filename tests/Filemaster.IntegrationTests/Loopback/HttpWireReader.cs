using System.Globalization;
using System.Text;

namespace Filemaster.IntegrationTests.Loopback;

/// <summary>Lettura bufferizzata di una connessione HTTP/1.1: righe ASCII, testa della richiesta, corpo con Content-Length o chunked.</summary>
internal sealed class HttpWireReader
{
    private const int MaxLineBytes = 64 * 1024;

    private readonly Stream _stream;
    private readonly byte[] _buffer = new byte[64 * 1024];
    private int _position;
    private int _length;

    internal HttpWireReader(Stream stream) => _stream = stream;

    /// <summary>Legge riga e intestazioni; null se la connessione si chiude prima di una nuova richiesta.</summary>
    internal async Task<RecordedRequest?> ReadHeadAsync(int connectionId, int keepBodyBytes)
    {
        var line = await ReadLineAsync().ConfigureAwait(false);
        if (line is null)
        {
            return null;
        }

        var parts = line.Split(' ');
        if (parts.Length != 3)
        {
            throw new InvalidDataException("Riga di richiesta non valida: " + line);
        }

        var headers = new List<KeyValuePair<string, string>>();
        while (true)
        {
            var header = await ReadLineAsync().ConfigureAwait(false) ?? throw new EndOfStreamException("Connessione chiusa dentro le intestazioni.");
            if (header.Length == 0)
            {
                break;
            }

            var colon = header.IndexOf(':');
            headers.Add(new KeyValuePair<string, string>(header.Substring(0, colon), header.Substring(colon + 1).Trim()));
        }

        return new RecordedRequest(connectionId, parts[0], parts[1], parts[2], headers, keepBodyBytes);
    }

    /// <summary>Legge il corpo di <paramref name="request"/> finche' non ne ha almeno <paramref name="atLeast"/> byte o finche' finisce.</summary>
    internal async Task ReadBodyAsync(RecordedRequest request, long atLeast)
    {
        var chunk = new byte[64 * 1024];
        if (request.IsChunked)
        {
            while (!request.BodyComplete && request.BodyLength < atLeast)
            {
                var sizeLine = await ReadLineAsync().ConfigureAwait(false) ?? throw new EndOfStreamException("Corpo chunked interrotto.");
                var semicolon = sizeLine.IndexOf(';');
                var size = long.Parse(semicolon < 0 ? sizeLine : sizeLine.Substring(0, semicolon), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
                if (size == 0)
                {
                    while ((await ReadLineAsync().ConfigureAwait(false) ?? string.Empty).Length > 0)
                    {
                    }

                    request.Complete();
                    return;
                }

                await CopyAsync(request, size, chunk).ConfigureAwait(false);
                if ((await ReadLineAsync().ConfigureAwait(false))?.Length != 0)
                {
                    throw new InvalidDataException("Manca il CRLF dopo un pezzo chunked.");
                }
            }

            return;
        }

        var total = request.ContentLength ?? 0;
        var wanted = Math.Min(total, atLeast) - request.BodyLength;
        if (wanted > 0)
        {
            await CopyAsync(request, wanted, chunk).ConfigureAwait(false);
        }

        if (request.BodyLength == total)
        {
            request.Complete();
        }
    }

    /// <summary>Legge fino alla chiusura della connessione (0 byte o errore); true se si e' chiusa.</summary>
    internal async Task DrainUntilClosedAsync()
    {
        var chunk = new byte[16 * 1024];
        try
        {
            while (await ReadAsync(chunk, 0, chunk.Length).ConfigureAwait(false) > 0)
            {
            }
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task CopyAsync(RecordedRequest request, long count, byte[] chunk)
    {
        while (count > 0)
        {
            var read = await ReadAsync(chunk, 0, (int)Math.Min(chunk.Length, count)).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Connessione chiusa dentro il corpo della richiesta.");
            }

            request.Append(chunk, 0, read);
            count -= read;
        }
    }

    private async Task<int> ReadAsync(byte[] target, int offset, int count)
    {
        if (_position == _length)
        {
#pragma warning disable CA1835 // net48 non ha l'overload con Memory: la stessa forma con array ovunque.
            _length = await _stream.ReadAsync(_buffer, 0, _buffer.Length).ConfigureAwait(false);
#pragma warning restore CA1835
            _position = 0;
            if (_length == 0)
            {
                return 0;
            }
        }

        var n = Math.Min(count, _length - _position);
        Buffer.BlockCopy(_buffer, _position, target, offset, n);
        _position += n;
        return n;
    }

    // Una riga terminata da CRLF, senza il terminatore; null se la connessione si chiude prima del primo byte.
    private async Task<string?> ReadLineAsync()
    {
        var line = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            if (await ReadAsync(one, 0, 1).ConfigureAwait(false) == 0)
            {
                return line.Count == 0 ? null : throw new EndOfStreamException("Connessione chiusa a meta' riga.");
            }

            if (one[0] == (byte)'\n')
            {
                if (line.Count > 0 && line[line.Count - 1] == (byte)'\r')
                {
                    line.RemoveAt(line.Count - 1);
                }

                return Encoding.ASCII.GetString(line.ToArray());
            }

            line.Add(one[0]);
            if (line.Count > MaxLineBytes)
            {
                throw new InvalidDataException("Riga troppo lunga.");
            }
        }
    }
}
