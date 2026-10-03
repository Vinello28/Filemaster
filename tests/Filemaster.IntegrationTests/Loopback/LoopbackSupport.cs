using System.Text;
using Filemaster.Domain;
using Filemaster.Infrastructure;

namespace Filemaster.IntegrationTests.Loopback;

/// <summary>Client, corpi di risposta e attese comuni ai test contro il server di loopback.</summary>
internal static class LoopbackSupport
{
    internal const string Key = "saf_FakeKeyForTestsOnly_0123456789abcdef";

    internal static readonly DocumentId DocId = new("doc_01M3VEESG5KBYR5PYAJ0TDT4B2");

    // Corpi presi dalle catture del server @8aec8bb (47-doc-upload, 71-docs-list-limit1-page1, 03-tenant, 02-readyz).
    internal const string UploadJson = """{"id":"doc_01M3VEESG5KBYR5PYAJ0TDT4B2","folder_id":"FATTURE","original_filename":"fattura.pdf","mime_type":"application/pdf","sha256":"cc1ba284a9fe9cefa40d4bd9dfb8d9e7fb395431aaf79478efca4e04da6c9d7e","size_bytes":590,"owner":"maria","tag":"fattura","sender":"Acme Srl","recipient":"Beta Spa","metadata":{"arxivar":{"docnumber":12345,"categoria":"X"}},"created_at":"2026-10-01T09:59:15.20534Z","has_content":true,"deduplicated":false}""";

    internal const string PageJson = """{"items":[{"id":"doc_01M3VEETKY7G9QMZHV4DPQ3QCV","original_filename":"random5m.bin","mime_type":"application/octet-stream","sha256":"7be2c8bb3d25189eafebf9688085116d87b7853a629f0d2dea12702353d30d9a","size_bytes":5242880,"tag":"grande","metadata":{},"created_at":"2026-10-01T09:59:16.350136Z","has_content":true}],"next_cursor":null}""";

    internal const string TenantJson = """{"id":"ten_01M3VCWS5PAKMSC47JNJ1PTFJQ","slug":"acme-test","name":"Acme Test","status":"active","created_at":"2026-10-01T09:59:09.501341Z"}""";

    internal const string ReadyJson = """{"status":"ready"}""";

    /// <summary>Le opzioni verso il server: tempi brevi ma non tirati, ritentativi senza attesa (i test contano le richieste, non i tempi).</summary>
    internal static FilemasterOptions Options(LoopbackServer server, Action<FilemasterOptions>? configure = null)
    {
        var options = new FilemasterOptions
        {
            BaseAddress = server.BaseAddress,
            ApiKey = Key,
            RequestTimeout = TimeSpan.FromSeconds(15),
            TransferTimeout = TimeSpan.FromSeconds(60),
        };
        options.Retry.InitialDelay = TimeSpan.Zero;
        options.Retry.MaxDelay = TimeSpan.FromMilliseconds(1);
        configure?.Invoke(options);
        return options;
    }

    /// <summary>Il client della factory con il SUO gestore primario (mai uno finto): e' quello che si prova.</summary>
    internal static FilemasterClient Client(LoopbackServer server, Action<FilemasterOptions>? configure = null) =>
        FilemasterClientFactory.Create(Options(server, configure));

    /// <summary>Un contenuto deterministico di <paramref name="length"/> byte (lo stesso di <see cref="GeneratedStream"/>).</summary>
    internal static byte[] Bytes(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = GeneratedStream.ByteAt(i);
        }

        return bytes;
    }

    internal static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>Legge tutto lo stream (a pezzi, come un utente).</summary>
    internal static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var collected = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
#pragma warning disable CA1835 // net48 non ha l'overload con Memory: la stessa forma con array ovunque.
            var read = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
#pragma warning restore CA1835
            if (read == 0)
            {
                return collected.ToArray();
            }

            collected.Write(buffer, 0, read);
        }
    }

    /// <summary>
    /// Aspetta un task al massimo <see cref="LoopbackServer.Limit"/>: se non finisce il test fallisce invece di restare appeso. Non
    /// cambia l'eccezione del task (niente <c>AggregateException</c>, lezione 34).
    /// </summary>
    internal static async Task<T> Within<T>(Task<T> task)
    {
        if (await Task.WhenAny(task, Task.Delay(LoopbackServer.Limit)).ConfigureAwait(false) != task)
        {
            throw new TimeoutException("Il task non e' finito entro il limite: una chiamata e' rimasta appesa.");
        }

        return await task.ConfigureAwait(false);
    }

    /// <inheritdoc cref="Within{T}(Task{T})"/>
    internal static async Task Within(Task task)
    {
        if (await Task.WhenAny(task, Task.Delay(LoopbackServer.Limit)).ConfigureAwait(false) != task)
        {
            throw new TimeoutException("Il task non e' finito entro il limite: una chiamata e' rimasta appesa.");
        }

        await task.ConfigureAwait(false);
    }

    /// <summary>Aspetta l'eccezione di <paramref name="call"/>, con il limite; fallisce se non lancia o se lancia altro.</summary>
    internal static async Task<TException> ThrowsWithin<TException>(Func<Task> call)
        where TException : Exception
    {
        return await Within(Assert.ThrowsAsync<TException>(call)).ConfigureAwait(false);
    }
}
