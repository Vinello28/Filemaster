using System.Text;

namespace Filemaster.IntegrationTests.Loopback;

/// <summary>Una parte di un corpo <c>multipart/form-data</c> ricevuto: intestazioni grezze e byte.</summary>
internal sealed class MultipartPart
{
    internal MultipartPart(IReadOnlyList<KeyValuePair<string, string>> headers, byte[] body)
    {
        Headers = headers;
        Body = body;
    }

    internal IReadOnlyList<KeyValuePair<string, string>> Headers { get; }

    internal byte[] Body { get; }

    internal string? Header(string name) =>
        Headers.Where(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value).FirstOrDefault();

    /// <summary>Il valore di <c>name="..."</c> nel Content-Disposition.</summary>
    internal string? Name
    {
        get
        {
            var disposition = Header("Content-Disposition") ?? string.Empty;
            const string Marker = "name=\"";
            var start = disposition.IndexOf(Marker, StringComparison.Ordinal);
            if (start < 0)
            {
                return null;
            }

            start += Marker.Length;
            return disposition.Substring(start, disposition.IndexOf('"', start) - start);
        }
    }
}

/// <summary>Un lettore di multipart scritto a parte (non quello del client): divide il corpo sui delimitatori della RFC 2046.</summary>
internal static class MultipartParts
{
    internal static IReadOnlyList<MultipartPart> Parse(string contentType, byte[] body)
    {
        const string Marker = "boundary=";
        var start = contentType.IndexOf(Marker, StringComparison.OrdinalIgnoreCase);
        Assert.True(start >= 0, "Content-Type senza boundary: " + contentType);
        var boundary = contentType.Substring(start + Marker.Length).Split(';')[0].Trim().Trim('"');
        var delimiter = Encoding.ASCII.GetBytes("--" + boundary);

        var parts = new List<MultipartPart>();
        var position = IndexOf(body, delimiter, 0);
        Assert.Equal(0, position);
        while (true)
        {
            position += delimiter.Length;
            if (body[position] == (byte)'-' && body[position + 1] == (byte)'-')
            {
                return parts;
            }

            position += 2; // CRLF dopo il delimitatore
            var headersEnd = IndexOf(body, Encoding.ASCII.GetBytes("\r\n\r\n"), position);
            var headerText = Encoding.ASCII.GetString(body, position, headersEnd - position);
            var headers = headerText.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => new KeyValuePair<string, string>(line.Substring(0, line.IndexOf(':')), line.Substring(line.IndexOf(':') + 1).Trim()))
                .ToList();
            var contentStart = headersEnd + 4;
            var next = IndexOf(body, Encoding.ASCII.GetBytes("\r\n--" + boundary), contentStart);
            var content = new byte[next - contentStart];
            Array.Copy(body, contentStart, content, 0, content.Length);
            parts.Add(new MultipartPart(headers, content));
            position = next + 2;
        }
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int from)
    {
        for (var i = from; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length && match; j++)
            {
                match = haystack[i + j] == needle[j];
            }

            if (match)
            {
                return i;
            }
        }

        throw new InvalidDataException("Delimitatore non trovato nel corpo multipart.");
    }
}
