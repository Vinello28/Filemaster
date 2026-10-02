using System.Globalization;
using System.Text.Json;
using Filemaster.Domain;

namespace Filemaster.Application;

/// <summary>
/// Estensioni di <see cref="IDocumentStore"/> costruite sopra i metodi della porta, senza altro accesso al server: scorrere tutte le
/// pagine di un elenco, cercare per profilo ARXivar e aprire il contenuto con la verifica dell'hash. Valgono per qualunque
/// implementazione della porta, anche un fake nei test.
/// </summary>
public static class DocumentStoreExtensions
{
    private const int PartialContentStatusCode = 206;

    /// <summary>
    /// Elenca tutti i documenti che soddisfano i filtri, scorrendo da solo le pagine (<see cref="IDocumentStore.ListAsync"/> con il
    /// cursore di ogni pagina) finche' <see cref="Page{T}.NextCursor"/> e' null. L'ordine e' quello del server (dal piu' recente);
    /// dopo l'ultima pagina non si fa nessuna richiesta in piu', e una richiesta parte solo quando serve davvero: smaltire
    /// l'enumeratore (per esempio con <c>break</c>) o annullare il token ferma le richieste.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Argomenti controllati subito.</b> <paramref name="store"/>, <paramref name="query"/> (con <see cref="DocumentQuery.Validate"/>)
    /// e <paramref name="pageSize"/> si controllano quando si chiama il metodo, non alla prima iterazione: un errore di
    /// programmazione non resta nascosto in un enumeratore mai letto. L'annullamento invece e' pigro: un token gia' annullato
    /// lancia <see cref="OperationCanceledException"/> alla prima richiesta di elementi.
    /// </para>
    /// <para>
    /// <b>Server che non avanza.</b> Se una pagina restituisce lo stesso cursore appena usato, un cursore gia' visto o un cursore
    /// vuoto, l'iterazione lancia <see cref="UnexpectedResponseException"/> (<see cref="FilemasterException.StatusCode"/> 200)
    /// invece di girare per sempre. Gli elementi di quella pagina sono gia' stati consegnati e non si fanno altre richieste.
    /// </para>
    /// <para>
    /// <b>Errori e annullamento.</b> L'errore di una pagina (per esempio di rete) si propaga alla richiesta di elementi che l'ha
    /// causato, dopo gli elementi delle pagine precedenti; il token annulla con <see cref="OperationCanceledException"/> (anche quello
    /// di <c>WithCancellation</c>, che si combina con questo).
    /// </para>
    /// <para>
    /// <b>C# 7.3.</b> <c>await foreach</c> richiede C# 8. Chi e' fermo a C# 7.3 (un progetto .NET Framework 4.8 senza <c>LangVersion</c>)
    /// scorre le pagine a mano con <see cref="IDocumentStore.ListAsync"/>: <c>new PageRequest(page.NextCursor)</c> chiede la pagina
    /// successiva, con gli stessi filtri, finche' <see cref="Page{T}.NextCursor"/> e' null.
    /// </para>
    /// </remarks>
    /// <param name="store">La porta dei documenti.</param>
    /// <param name="query">I filtri, passati invariati a ogni pagina; null per non filtrare.</param>
    /// <param name="pageSize">Quanti elementi chiedere per pagina, da 1 a <see cref="PageRequest.MaxLimit"/>; null per il default del server (50).</param>
    /// <param name="cancellationToken">Per annullare l'enumerazione; arriva a ogni <see cref="IDocumentStore.ListAsync"/>.</param>
    /// <returns>Una sequenza asincrona di tutti i documenti, che fa le richieste mentre la si legge.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> e' null.</exception>
    /// <exception cref="ArgumentException"><paramref name="query"/> non e' valida (vedi <see cref="DocumentQuery.Validate"/>).</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageSize"/> e' minore di 1 o maggiore di <see cref="PageRequest.MaxLimit"/>.</exception>
    /// <exception cref="UnexpectedResponseException">Durante l'iterazione: il server restituisce un cursore gia' usato o vuoto (vedi le note).</exception>
    public static IAsyncEnumerable<Document> EnumerateAsync(
        this IDocumentStore store,
        DocumentQuery? query = null,
        int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        Guard.NotNull(store);
        query?.Validate();
        Pagination.CheckPageSize(pageSize);
        return Pagination.IterateAsync((request, token) => store.ListAsync(query, request, token), pageSize, cancellationToken);
    }

    /// <summary>
    /// Cerca i documenti importati da ARXivar con questo <c>DOCNUMBER</c>: filtra i metadati con <c>{"arxivar":{"docnumber":N}}</c> (un
    /// numero JSON, non una stringa: il filtro distingue <c>1</c> da <c>"1"</c>) e legge <b>tutte</b> le pagine del risultato.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Perche' una lista e non un solo documento.</b> Il collegamento che <c>arxivar-sync</c> tiene nel server ha come chiave (ente,
    /// <c>DOCNUMBER</c>), quindi un documento importato dalla sincronizzazione e' unico. Ma il filtro agisce sui <b>metadati liberi</b>: un
    /// documento caricato via API con gli stessi metadati compare nello stesso risultato. La lista e' nell'ordine del server, dal piu'
    /// recente; e' vuota se non c'e' nessun documento. Per leggere i dati ARXivar di un risultato si usa
    /// <see cref="DocumentExtensions.GetArxivarMetadata"/>.
    /// </para>
    /// <para>
    /// <b>Valori ammessi.</b> <c>arxivar-sync</c> importa solo i profili con <c>DOCNUMBER</c> maggiore di zero (legge <c>DOCNUMBER &gt; 0</c> da
    /// ARXivar, dove e' un intero a 32 bit), quindi un valore non positivo non puo' corrispondere a un documento importato e si rifiuta,
    /// come errore di chi chiama. <see cref="ArxivarMetadata.From"/> in lettura resta invece tollerante: il server non valida i
    /// metadati liberi.
    /// </para>
    /// <para>
    /// Le pagine si chiedono alla massima dimensione (<see cref="PageRequest.MaxLimit"/>) e il filtro, che e' un <see cref="JsonDocument"/>,
    /// viene smaltito alla fine, dopo l'ultima chiamata alla porta.
    /// </para>
    /// </remarks>
    /// <param name="store">La porta dei documenti.</param>
    /// <param name="docnumber">Il <c>DOCNUMBER</c> ARXivar, maggiore di zero.</param>
    /// <param name="cancellationToken">Per annullare la ricerca; arriva a ogni richiesta.</param>
    /// <returns>I documenti trovati, dal piu' recente; lista vuota se non ce ne sono.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> e' null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="docnumber"/> e' zero o negativo.</exception>
    /// <exception cref="UnexpectedResponseException">Il server restituisce un cursore che non avanza (vedi <see cref="EnumerateAsync"/>).</exception>
    public static async Task<IReadOnlyList<Document>> FindByArxivarDocnumberAsync(
        this IDocumentStore store,
        int docnumber,
        CancellationToken cancellationToken = default)
    {
        Guard.NotNull(store);
        if (docnumber <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(docnumber),
                docnumber,
                "Il DOCNUMBER ARXivar dei documenti importati e' maggiore di zero.");
        }

        // Il numero e' scritto con la cultura invariante: nessun separatore delle migliaia, nessun segno diverso.
        using var filter = JsonDocument.Parse("{\"arxivar\":{\"docnumber\":" + docnumber.ToString(CultureInfo.InvariantCulture) + "}}");
        var query = new DocumentQuery { Metadata = filter.RootElement };
        var found = new List<Document>();
        await foreach (var document in store.EnumerateAsync(query, PageRequest.MaxLimit, cancellationToken).ConfigureAwait(false))
        {
            found.Add(document);
        }

        return found;
    }

    /// <summary>
    /// Apre il contenuto <b>intero</b> di un documento (<see cref="IDocumentStore.OpenContentAsync"/>) e lo restituisce dentro uno
    /// <see cref="VerifiedContentStream"/>: chi legge fino in fondo ha la garanzia che i byte hanno lo SHA-256 registrato nel documento
    /// (<see cref="Document.Sha256"/>) e la sua dimensione (<see cref="Document.SizeBytes"/>), altrimenti la lettura che arriva alla
    /// fine lancia <see cref="ContentIntegrityException"/>. Il risultato va smaltito (<c>using</c>): lo smaltimento rilascia anche la
    /// risposta HTTP originale.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Solo contenuto intero:</b> non c'e' un <see cref="ByteRange"/>, perche' l'hash di una parte non si puo' verificare. Il contenuto
    /// si apre sempre senza intervallo; se la risposta e' comunque parziale (<see cref="DocumentContent.IsPartial"/>) il contenuto aperto
    /// si smaltisce e si lancia <see cref="UnexpectedResponseException"/>.
    /// </para>
    /// <para>
    /// <b>Il verdetto c'e' solo all'EOF</b> e smaltire prima non da' nessuna garanzia ne' nessuna eccezione: vedi le note di
    /// <see cref="VerifiedContentStream"/>. Le intestazioni (<see cref="DocumentContent.ContentType"/>, <see cref="DocumentContent.FileName"/>,
    /// <see cref="DocumentContent.ContentLength"/>, <see cref="DocumentContent.LastModified"/>) sono quelle della risposta del server.
    /// </para>
    /// </remarks>
    /// <param name="store">La porta dei documenti.</param>
    /// <param name="document">
    /// Il documento, con il suo contenuto (<see cref="Document.HasContent"/> vero): un documento importato con i soli metadati non ha un
    /// hash da verificare. Lo si controlla con <see cref="Document.HasContent"/> prima di chiamare.
    /// </param>
    /// <param name="cancellationToken">Per annullare l'apertura.</param>
    /// <returns>Il contenuto, con lo stream che verifica l'hash mentre si legge.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> o <paramref name="document"/> e' null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="document"/> non ha contenuto (<see cref="Document.HasContent"/> falso): la porta non viene chiamata e
    /// <see cref="ArgumentException.ParamName"/> e' <c>document</c>. Oppure <see cref="Document.Sha256"/> non e' di 64 cifre esadecimali
    /// ASCII (un documento costruito a mano: il server le scrive sempre cosi'): il contenuto aperto si smaltisce e
    /// <see cref="ArgumentException.ParamName"/> e' <c>expectedSha256</c>.
    /// </exception>
    /// <exception cref="UnexpectedResponseException">La risposta e' parziale (<see cref="FilemasterException.StatusCode"/> 206).</exception>
    /// <exception cref="NotFoundException">Il documento non esiste piu' (404, dalla porta).</exception>
    /// <exception cref="ContentUnavailableException">Il server dice che il documento non ha un file (409 <c>content-unavailable</c>, dalla porta).</exception>
    public static async Task<DocumentContent> OpenVerifiedContentAsync(
        this IDocumentStore store,
        Document document,
        CancellationToken cancellationToken = default)
    {
        Guard.NotNull(store);
        Guard.NotNull(document);
        if (!document.HasContent)
        {
            throw new ArgumentException(
                "Il documento non ha un contenuto da verificare (HasContent e' falso): non si apre ne' si verifica.",
                nameof(document));
        }

        var original = await store.OpenContentAsync(document.Id, null, cancellationToken).ConfigureAwait(false);
        VerifiedContentStream? verified = null;
        try
        {
            if (original.IsPartial)
            {
                throw new UnexpectedResponseException(
                    "Il server ha restituito solo una parte del contenuto a una richiesta senza intervallo: l'hash di una parte non si puo' verificare.",
                    PartialContentStatusCode);
            }

            // L'originale resta il proprietario dello stream interno (leaveOpen): smaltire il risultato smaltisce una volta sola lo stream
            // e poi la risposta HTTP, e l'originale non si smaltisce due volte.
            verified = new VerifiedContentStream(original.Content, document.Sha256!, document.SizeBytes, leaveOpen: true);
            return new DocumentContent(
                verified,
                original.ContentType,
                original.FileName,
                original.ContentLength,
                original.LastModified,
                range: null,
                owner: original);
        }
        catch
        {
            verified?.Dispose();
            original.Dispose();
            throw;
        }
    }
}
