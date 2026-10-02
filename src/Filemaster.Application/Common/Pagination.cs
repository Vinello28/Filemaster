using System.Runtime.CompilerServices;
using Filemaster.Domain;

namespace Filemaster.Application;

/// <summary>
/// Il ciclo di auto-paginazione condiviso da <c>EnumerateAsync</c> dei documenti e dei contatti: un solo posto per la regola
/// "si va avanti finche' c'e' un cursore, e si ferma un server che non avanza".
/// </summary>
internal static class Pagination
{
    // Lo status della risposta riuscita che ha portato il cursore sbagliato: il server ha risposto 200, ma con un corpo che non si puo' seguire.
    private const int SuccessStatusCode = 200;

    /// <summary>Controlla la dimensione di pagina di <c>EnumerateAsync</c> subito (non alla prima iterazione), con il nome del parametro giusto.</summary>
    internal static void CheckPageSize(int? pageSize)
    {
        if (pageSize is < 1 or > PageRequest.MaxLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), pageSize, $"La dimensione di pagina deve essere compresa fra 1 e {PageRequest.MaxLimit}.");
        }
    }

    /// <summary>
    /// Legge pagina dopo pagina, nell'ordine del server, e restituisce gli elementi uno a uno. Prima di ogni pagina controlla
    /// l'annullamento (uno store che ignora il token non deve far proseguire l'iterazione); dopo l'ultima pagina (cursore null)
    /// non fa altre chiamate. Gli elementi di una pagina si consegnano prima di guardare il suo cursore: se il cursore e' una
    /// ripetizione (quello appena usato o uno gia' visto) o e' vuoto, l'eccezione arriva dopo quegli elementi e prima di
    /// qualunque altra richiesta.
    /// </summary>
    internal static async IAsyncEnumerable<T> IterateAsync<T>(
        Func<PageRequest, CancellationToken, Task<Page<T>>> listPage,
        int? pageSize,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await listPage(new PageRequest(cursor, pageSize), cancellationToken).ConfigureAwait(false);
            foreach (var item in page.Items)
            {
                yield return item;
            }

            cursor = page.NextCursor;
            if (cursor is not null)
            {
                if (string.IsNullOrWhiteSpace(cursor))
                {
                    throw new UnexpectedResponseException(
                        "Il server ha restituito un cursore vuoto per la pagina successiva: la paginazione non puo' proseguire.",
                        SuccessStatusCode);
                }

                if (!seenCursors.Add(cursor))
                {
                    throw new UnexpectedResponseException(
                        "Il server ha restituito un cursore gia' usato: la paginazione non avanza e andrebbe avanti all'infinito.",
                        SuccessStatusCode);
                }
            }
        }
        while (cursor is not null);
    }
}
