using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Filemaster.Application;
using Filemaster.Domain;
using Filemaster.Infrastructure;

namespace Filemaster.IntegrationTests.Live;

/// <summary>
/// Il server vero della suite live: indirizzo e chiavi dalle variabili d'ambiente che stampa <c>eng/e2e/run-e2e.sh</c>.
/// Senza <c>FILEMASTER_E2E_URL</c> ogni test live e' SALTATO (CI normale, sviluppo locale); con
/// <c>FILEMASTER_E2E_REQUIRED=1</c> (lo imposta <c>e2e.yml</c>) invece fallisce, cosi' una variabile persa non diventa un
/// "tutto verde" fatto di test saltati.
/// </summary>
internal static class LiveServer
{
    internal const string UrlVariable = "FILEMASTER_E2E_URL";
    internal const string WriteKeyVariable = "FILEMASTER_E2E_KEY";
    internal const string ReadKeyVariable = "FILEMASTER_E2E_READ_KEY";
    internal const string AdminKeyVariable = "FILEMASTER_E2E_ADMIN_KEY";
    internal const string MaxUploadVariable = "FILEMASTER_E2E_MAX_UPLOAD_BYTES";
    internal const string RequiredVariable = "FILEMASTER_E2E_REQUIRED";

    /// <summary>
    /// Suffisso di questa esecuzione (data e ora UTC + 6 cifre esadecimali casuali): rende unici nomi, codici e contenuti, cosi'
    /// la suite si rilancia contro lo stesso server senza scontrarsi con cio' che ha lasciato un'esecuzione interrotta.
    /// </summary>
    internal static readonly string RunId =
        DateTime.UtcNow.ToString("yyMMddHHmmss", CultureInfo.InvariantCulture)
            + (Guid.NewGuid().GetHashCode() & 0xFFFFFF).ToString("x6", CultureInfo.InvariantCulture);

    private static int _sequence;

    /// <summary>L'indirizzo del server; salta (o fallisce, con <c>FILEMASTER_E2E_REQUIRED=1</c>) se manca.</summary>
    internal static Uri Url
    {
        get
        {
            var url = Variable(UrlVariable);
            if (url is null)
            {
                Missing(UrlVariable + " non impostata: la suite live gira solo contro un server vero (eng/e2e/run-e2e.sh).");
            }

            return new Uri(url!, UriKind.Absolute);
        }
    }

    /// <summary>La chiave con scope write (carica, sposta, cancella; implica read).</summary>
    internal static string WriteKey => Key(WriteKeyVariable);

    /// <summary>La chiave con scope read.</summary>
    internal static string ReadKey => Key(ReadKeyVariable);

    /// <summary>La chiave con scope admin.</summary>
    internal static string AdminKey => Key(AdminKeyVariable);

    /// <summary>Il limite di upload configurato sul server (<c>SHARPAFILE_MAX_UPLOAD_SIZE_BYTES</c>); salta se non e' noto.</summary>
    internal static long MaxUploadBytes
    {
        get
        {
            _ = Url;
            var raw = Variable(MaxUploadVariable);
            if (raw is null || !long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 1)
            {
                Missing(MaxUploadVariable + " assente o non valida: il limite di upload del server non e' noto.");
            }

            return long.Parse(raw!, NumberStyles.None, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Il client della factory pubblica (gestore primario della libreria) con la chiave indicata (default: write).</summary>
    internal static FilemasterClient Client(string? apiKey = null) => FilemasterClientFactory.Create(Options(apiKey ?? WriteKey));

    internal static FilemasterOptions Options(string apiKey) =>
        new()
        {
            BaseAddress = Url,
            ApiKey = apiKey,
            RequestTimeout = TimeSpan.FromSeconds(30),
            TransferTimeout = TimeSpan.FromMinutes(2),
        };

    /// <summary>Un nome unico per questa esecuzione (valido anche come codice di cartella: lettere, cifre e '-').</summary>
    internal static string Unique(string prefix) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "{0}-{1}-{2}",
            prefix,
            RunId,
            Interlocked.Increment(ref _sequence));

    internal static FolderCode UniqueFolder(string prefix) => new(Unique(prefix));

    /// <summary>
    /// Un contenuto di <paramref name="length"/> byte che nessun'altra chiamata produce (testata unica + riempimento): un upload
    /// nuovo non trova mai un blob identico, quindi <c>Deduplicated</c> e' falso al primo caricamento anche sullo stesso server.
    /// </summary>
    internal static byte[] UniqueBytes(int length)
    {
        var header = Encoding.ASCII.GetBytes("%PDF-1.4 filemaster e2e " + Unique("content") + " " + Guid.NewGuid().ToString("N") + "\n");
        var bytes = new byte[Math.Max(length, header.Length)];
        Array.Copy(header, bytes, header.Length);
        for (var i = header.Length; i < bytes.Length; i++)
        {
            bytes[i] = (byte)((i * 31) + 7);
        }

        return bytes;
    }

    /// <summary>SHA-256 in esadecimale minuscolo (la forma del server), con API presenti anche su net48.</summary>
    internal static string Sha256Hex(byte[] bytes)
    {
        using var sha = SHA256.Create();
#pragma warning disable CA1850 // SHA256.HashData non esiste su net48: la stessa forma su ogni TFM.
        var hash = sha.ComputeHash(bytes);
#pragma warning restore CA1850
        var text = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
        {
            text.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    /// <summary>Legge tutto lo stream (a pezzi, come un utente).</summary>
    internal static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var collected = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
#pragma warning disable CA1835 // net48 non ha l'overload con Memory: la stessa forma con array ovunque.
            var read = await stream.ReadAsync(buffer, 0, buffer.Length, TestContext.Current.CancellationToken).ConfigureAwait(false);
#pragma warning restore CA1835
            if (read == 0)
            {
                return collected.ToArray();
            }

            collected.Write(buffer, 0, read);
        }
    }

    /// <summary>Carica <paramref name="bytes"/> e registra il documento in <paramref name="cleanup"/>.</summary>
    internal static async Task<UploadResult> UploadAsync(
        IFilemasterClient client,
        LiveCleanup cleanup,
        byte[] bytes,
        string fileName,
        Action<UploadDocumentRequest>? configure = null)
    {
        using var content = new MemoryStream(bytes, writable: false);
        var request = new UploadDocumentRequest(content, fileName) { ContentType = "application/pdf" };
        configure?.Invoke(request);
        var result = await client.Documents.UploadAsync(request, TestContext.Current.CancellationToken).ConfigureAwait(false);
        cleanup.Document(result.Document.Id);
        return result;
    }

    private static string Key(string variable)
    {
        _ = Url;
        var key = Variable(variable);
        if (key is null)
        {
            Missing(variable + " non impostata (la stampa eng/e2e/run-e2e.sh insieme a " + UrlVariable + ").");
        }

        return key!;
    }

    private static string? Variable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value!.Trim();
    }

    private static void Missing(string reason)
    {
        if (Environment.GetEnvironmentVariable(RequiredVariable) == "1")
        {
            Assert.Fail(RequiredVariable + "=1 ma " + reason);
        }

        Assert.Skip(reason);
    }
}
