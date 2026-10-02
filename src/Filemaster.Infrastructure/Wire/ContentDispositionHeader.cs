using System.Text;

namespace Filemaster.Infrastructure;

/// <summary>
/// L'intestazione <c>Content-Disposition</c>, in lettura (il nome del file di un download) e in scrittura (le parti di un modulo multipart
/// di un caricamento). Tutto a mano su testo ASCII, senza i tipi di <c>System.Net.Http.Headers</c>: il loro modo di leggere e di scrivere
/// un nome non ASCII cambia tra .NET Framework e .NET moderno (<c>FileName</c> con base64 <c>=?utf-8?B?...?=</c>, decodifica di <c>FileNameStar</c>
/// diversa), mentre qui il testo e' lo stesso ovunque.
/// </summary>
internal static class ContentDispositionHeader
{
    private const string FallbackName = "documento";

    /// <summary>
    /// Il nome del file da un <c>Content-Disposition</c> di una risposta, per esempio
    /// <c>attachment; filename="perch_ _.pdf"; filename*=UTF-8''perch%C3%A9%20%C3%A8.pdf</c> (il server ne emette sempre entrambe le forme: la
    /// prima e' un ripiego ASCII, la seconda ha il nome vero). Vale <c>filename*</c> (RFC 5987/6266: <c>charset'lingua'testo-con-%XX</c>, con
    /// UTF-8 o ISO-8859-1, charset senza distinguere maiuscole); se manca o non e' valido (charset sconosciuto, percent errato, UTF-8 non
    /// valido, vuoto) vale <c>filename</c>, tra virgolette (con <c>\</c> come escape) o senza; altrimenti null. Il parametro si riconosce senza
    /// distinguere maiuscole e gli spazi attorno a <c>;</c> e <c>=</c> si tollerano. I punti e virgola dentro le virgolette non separano. Nessuna
    /// pulizia del nome (percorsi, caratteri di controllo): e' il nome del documento cosi' com'e' sul server, e chi lo usa per scrivere un file
    /// lo deve ripulire.
    /// </summary>
    /// <param name="value">Il valore dell'intestazione; null o vuoto da' null.</param>
    internal static string? ReadFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string? plain = null;
        string? extended = null;
        foreach (var parameter in SplitParameters(value!))
        {
            var equals = parameter.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var name = parameter.Substring(0, equals).Trim();
            var raw = parameter.Substring(equals + 1).Trim();
            if (extended is null && string.Equals(name, "filename*", StringComparison.OrdinalIgnoreCase))
            {
                extended = DecodeExtended(Unquote(raw));
            }
            else if (plain is null && string.Equals(name, "filename", StringComparison.OrdinalIgnoreCase))
            {
                var text = Unquote(raw);
                plain = text.Length == 0 ? null : text;
            }
        }

        return extended ?? plain;
    }

    /// <summary>
    /// <c>form-data; name="NOME"</c>: l'intestazione di una parte di testo del modulo multipart (<c>owner</c>, <c>tag</c>...). Il nome e' una
    /// costante del codice (ASCII senza virgolette), non un dato dell'utente.
    /// </summary>
    /// <param name="name">Il nome del campo.</param>
    internal static string FormField(string name)
    {
        Guard.NotNull(name);
        return "form-data; name=\"" + name + "\"";
    }

    /// <summary>
    /// <c>form-data; name="NOME"; filename="FALLBACK"; filename*=utf-8''PERCENT</c>: l'intestazione della parte del file di un caricamento, come
    /// il server l'ha accettata nella cattura 54. <c>filename*</c> ha il nome VERO in UTF-8, percent-codificato byte per byte (restano scoperti
    /// solo le lettere e le cifre ASCII e <c>! # $ &amp; + - . ^ _ ` | ~</c>, l'insieme <c>attr-char</c> della RFC 5987: apice, asterisco,
    /// percento, spazio, virgolette e backslash si codificano) e il server lo preferisce. <c>filename</c> e' un ripiego ASCII per chi non legge
    /// <c>filename*</c>, scritto come fa il server nelle sue risposte: ogni carattere fuori da ASCII stampabile, ogni virgolette e ogni backslash
    /// (anche una coppia surrogata, che conta uno) diventa <c>_</c>, gli spazi ai bordi si tolgono e un risultato vuoto diventa <c>documento</c>.
    /// Cosi' non c'e' mai un a capo, un controllo o una virgoletta che chiuda il valore: l'iniezione di altre intestazioni e' impossibile.
    /// </summary>
    /// <param name="name">Il nome del campo (<c>file</c>).</param>
    /// <param name="fileName">Il nome del file, cosi' com'e' (il server toglie il percorso).</param>
    /// <exception cref="ArgumentException"><paramref name="fileName"/> e' vuoto o contiene un surrogato isolato (UTF-16 non valido).</exception>
    internal static string FilePart(string name, string fileName)
    {
        Guard.NotNull(name);
        Guard.NotNull(fileName);
        if (fileName.Length == 0)
        {
            throw new ArgumentException("Il nome del file e' vuoto.", nameof(fileName));
        }

        PercentEncoding.RequireWellFormed(fileName, nameof(fileName));
        return FormField(name) + "; filename=\"" + AsciiFallback(fileName) + "\"; filename*=utf-8''" + EncodeExtended(fileName);
    }

    private static string AsciiFallback(string fileName)
    {
        var builder = new StringBuilder(fileName.Length);
        for (var i = 0; i < fileName.Length; i++)
        {
            var c = fileName[i];
            if (char.IsHighSurrogate(c))
            {
                i++; // la coppia (gia' verificata) e' un solo carattere
                builder.Append('_');
            }
            else
            {
                builder.Append(c >= 0x20 && c <= 0x7E && c != '"' && c != '\\' ? c : '_');
            }
        }

        var result = builder.ToString().Trim();
        return result.Length == 0 ? FallbackName : result;
    }

    // Percent-codifica dei byte UTF-8 con l'insieme attr-char della RFC 5987 (lo stesso di ContentDisposition.IsAttrChar del server).
    private static string EncodeExtended(string fileName)
    {
        const string Hex = "0123456789ABCDEF";
        var builder = new StringBuilder(fileName.Length * 3);
        foreach (var b in Encoding.UTF8.GetBytes(fileName))
        {
            if (IsAttributeCharacter(b))
            {
                builder.Append((char)b);
            }
            else
            {
                builder.Append('%').Append(Hex[b >> 4]).Append(Hex[b & 0xF]);
            }
        }

        return builder.ToString();
    }

    private static bool IsAttributeCharacter(byte b) =>
        (b >= (byte)'a' && b <= (byte)'z')
        || (b >= (byte)'A' && b <= (byte)'Z')
        || (b >= (byte)'0' && b <= (byte)'9')
        || b == (byte)'!' || b == (byte)'#' || b == (byte)'$' || b == (byte)'&' || b == (byte)'+' || b == (byte)'-'
        || b == (byte)'.' || b == (byte)'^' || b == (byte)'_' || b == (byte)'`' || b == (byte)'|' || b == (byte)'~';

    // I parametri separati da ';' fuori dalle virgolette (il ripiego del server puo' contenere ';' dentro le virgolette). Il primo elemento
    // (il tipo: attachment, inline) non ha '=' e si scarta dal chiamante.
    private static List<string> SplitParameters(string value)
    {
        var parts = new List<string>();
        var start = 0;
        var quoted = false;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (quoted && c == '\\' && i + 1 < value.Length)
            {
                i++;
            }
            else if (c == '"')
            {
                quoted = !quoted;
            }
            else if (c == ';' && !quoted)
            {
                parts.Add(value.Substring(start, i - start));
                start = i + 1;
            }
        }

        parts.Add(value.Substring(start));
        return parts;
    }

    // Toglie le virgolette esterne e il backslash degli escape (quoted-pair); un valore senza virgolette resta com'e'.
    private static string Unquote(string raw)
    {
        if (raw.Length < 2 || raw[0] != '"' || raw[raw.Length - 1] != '"')
        {
            return raw;
        }

        var builder = new StringBuilder(raw.Length);
        for (var i = 1; i < raw.Length - 1; i++)
        {
            if (raw[i] == '\\' && i + 1 < raw.Length - 1)
            {
                i++;
            }

            builder.Append(raw[i]);
        }

        return builder.ToString();
    }

    // charset'lingua'testo: UTF-8 o ISO-8859-1 (senza distinguere maiuscole), %XX con cifre esadecimali ASCII. Null se non e' valido o e' vuoto.
    private static string? DecodeExtended(string value)
    {
        var first = value.IndexOf('\'');
        var second = first < 0 ? -1 : value.IndexOf('\'', first + 1);
        if (first <= 0 || second < 0)
        {
            return null;
        }

        var charset = value.Substring(0, first);
        var utf8 = string.Equals(charset, "utf-8", StringComparison.OrdinalIgnoreCase);
        if (!utf8 && !string.Equals(charset, "iso-8859-1", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var bytes = new List<byte>(value.Length - second);
        for (var i = second + 1; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '%')
            {
                if (i + 2 >= value.Length)
                {
                    return null;
                }

                var high = HexValue(value[i + 1]);
                var low = HexValue(value[i + 2]);
                if (high < 0 || low < 0)
                {
                    return null;
                }

                bytes.Add((byte)((high << 4) | low));
                i += 2;
            }
            else if (c > 0x7E || c < 0x21)
            {
                return null; // fuori da ASCII stampabile senza spazio: non e' un ext-value
            }
            else
            {
                bytes.Add((byte)c);
            }
        }

        if (bytes.Count == 0)
        {
            return null;
        }

        if (!utf8)
        {
            // ISO-8859-1: ogni byte e' il carattere con lo stesso numero (nessuna tabella, uguale su ogni runtime).
            var latin1 = new StringBuilder(bytes.Count);
            foreach (var b in bytes)
            {
                latin1.Append((char)b);
            }

            return latin1.ToString();
        }

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes.ToArray());
        }
        catch (ArgumentException)
        {
            return null; // UTF-8 non valido (DecoderFallbackException e' un ArgumentException)
        }
    }

    private static int HexValue(char c) =>
        c >= '0' && c <= '9' ? c - '0'
        : c >= 'a' && c <= 'f' ? c - 'a' + 10
        : c >= 'A' && c <= 'F' ? c - 'A' + 10
        : -1;
}
