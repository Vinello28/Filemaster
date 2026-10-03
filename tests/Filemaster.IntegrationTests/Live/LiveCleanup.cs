using Filemaster.Application;
using Filemaster.Domain;

namespace Filemaster.IntegrationTests.Live;

/// <summary>
/// Cio' che un test live ha creato sul server, da cancellare alla fine anche se il test fallisce: prima i documenti, poi le
/// cartelle dalla piu' recente (le figlie prima delle madri). Un 404 non e' un errore (il test l'ha gia' cancellato); ogni
/// altro errore della pulizia si raccoglie e fa fallire il test, dopo aver provato a cancellare tutto il resto.
/// </summary>
internal sealed class LiveCleanup
{
    private readonly List<DocumentId> _documents = new();
    private readonly List<FolderCode> _folders = new();

    /// <summary>
    /// Esegue <paramref name="body"/> e poi la pulizia. Se il corpo fallisce, la pulizia si tenta comunque ma vince l'errore del
    /// test (un errore di pulizia non deve nascondere la causa vera).
    /// </summary>
    internal static async Task WithCleanupAsync(IFilemasterClient client, Func<LiveCleanup, Task> body)
    {
        var cleanup = new LiveCleanup();
        try
        {
            await body(cleanup).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await cleanup.RunAsync(client).ConfigureAwait(false);
            }
            catch (AggregateException)
            {
                // Vince l'errore del test, rilanciato qui sotto.
            }

            throw;
        }

        await cleanup.RunAsync(client).ConfigureAwait(false);
    }

    internal void Document(DocumentId id) => _documents.Add(id);

    internal void Folder(FolderCode id) => _folders.Add(id);

    /// <summary>Una cartella rinominata nel test: la pulizia deve cercarla col codice nuovo.</summary>
    internal void Renamed(FolderCode from, FolderCode to)
    {
        var at = _folders.IndexOf(from);
        if (at >= 0)
        {
            _folders[at] = to;
        }
    }

    internal async Task RunAsync(IFilemasterClient client)
    {
        // CancellationToken.None: la pulizia deve finire anche quando il test e' stato annullato.
        var failures = new List<Exception>();
        foreach (var id in _documents)
        {
            await Attempt(() => client.Documents.DeleteAsync(id, CancellationToken.None), failures).ConfigureAwait(false);
        }

        for (var i = _folders.Count - 1; i >= 0; i--)
        {
            var id = _folders[i];
            await Attempt(() => client.Folders.DeleteAsync(id, CancellationToken.None), failures).ConfigureAwait(false);
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("Pulizia del test live non riuscita.", failures);
        }
    }

    private static async Task Attempt(Func<Task> delete, List<Exception> failures)
    {
        try
        {
            await delete().ConfigureAwait(false);
        }
        catch (NotFoundException)
        {
            // Gia' cancellato dal test.
        }
#pragma warning disable CA1031 // Si raccolgono tutti gli errori e si rilanciano insieme alla fine.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failures.Add(ex);
        }
    }
}
