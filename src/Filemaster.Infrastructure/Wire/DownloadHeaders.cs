using System.Net.Http.Headers;
using Filemaster.Application;
using Filemaster.Domain;

namespace Filemaster.Infrastructure;

/// <summary>
/// Cio' che le intestazioni di un download dicono del contenuto, nella forma che serve a <see cref="DocumentContent"/>: il tipo
/// (<c>Content-Type</c>), il nome del file (<c>Content-Disposition</c>, preferendo <c>filename*</c>), la lunghezza (<c>Content-Length</c>,
/// gia' letta dal trasporto), l'ultima modifica (<c>Last-Modified</c>) e, solo per una risposta 206, quale parte e' arrivata
/// (<c>Content-Range</c>).
/// </summary>
internal sealed class DownloadHeaders
{
    /// <summary>Il tipo di contenuto quando l'intestazione manca: quello che HTTP prevede per un contenuto di tipo ignoto.</summary>
    internal const string DefaultContentType = "application/octet-stream";

    private DownloadHeaders(string contentType, string? fileName, long? contentLength, DateTimeOffset? lastModified, ContentRange? range)
    {
        ContentType = contentType;
        FileName = fileName;
        ContentLength = contentLength;
        LastModified = lastModified;
        Range = range;
    }

    /// <summary>Il tipo di contenuto come l'ha scritto il server, parametri compresi; <see cref="DefaultContentType"/> se manca o e' vuoto. Mai null.</summary>
    internal string ContentType { get; }

    /// <summary>Il nome del file (<see cref="ContentDispositionHeader.ReadFileName"/>); null se l'intestazione manca o non ne ha uno.</summary>
    internal string? FileName { get; }

    /// <summary>Quanti byte consegnera' lo stream; null se il server non l'ha detto.</summary>
    internal long? ContentLength { get; }

    /// <summary>L'ultima modifica; null se manca o non e' una data HTTP valida (e' un'informazione, non un requisito).</summary>
    internal DateTimeOffset? LastModified { get; }

    /// <summary>Quale parte del contenuto e' arrivata; null se la risposta non e' 206 (e' arrivato tutto).</summary>
    internal ContentRange? Range { get; }

    /// <summary>
    /// Legge le intestazioni di un download. <c>Content-Range</c> si legge e si pretende SOLO con lo status 206: con 200 (anche se l'intestazione
    /// ci fosse, il server ha mandato tutto) il contenuto e' intero. Un 206 senza un <c>Content-Range</c> valido (<c>bytes primo-ultimo/totale</c>,
    /// non <c>*/totale</c>), oppure con una lunghezza che non torna con quella dell'intervallo, e' una risposta non interpretabile.
    /// </summary>
    /// <param name="response">Il download del trasporto (status, id di correlazione, lunghezza e intestazioni).</param>
    /// <exception cref="UnexpectedResponseException">Un 206 con un <c>Content-Range</c> mancante, non valido o incoerente con la lunghezza.</exception>
    internal static DownloadHeaders Read(DownloadResponse response)
    {
        Guard.NotNull(response);
        var context = new WireContext(response.StatusCode, response.RequestId);
        var contentHeaders = response.ContentHeaders;

        ContentRange? range = null;
        if (response.StatusCode == 206)
        {
            range = ReadRange(FirstValue(contentHeaders, "Content-Range"), response.ContentLength, context);
        }

        var contentType = FirstValue(contentHeaders, "Content-Type");
        return new DownloadHeaders(
            string.IsNullOrWhiteSpace(contentType) ? DefaultContentType : contentType!.Trim(),
            ContentDispositionHeader.ReadFileName(FirstValue(contentHeaders, "Content-Disposition")),
            response.ContentLength,
            contentHeaders.LastModified,
            range);
    }

    /// <summary>Costruisce il <see cref="DocumentContent"/> con queste intestazioni, lo stream indicato e la risorsa che lo alimenta.</summary>
    /// <param name="content">Lo stream dei byte (che il <see cref="DocumentContent"/> possiede e smaltisce).</param>
    /// <param name="owner">La risorsa da smaltire dopo lo stream (la risposta HTTP); null se lo stream basta.</param>
    internal DocumentContent ToContent(Stream content, IDisposable? owner) =>
        new(content, ContentType, FileName, ContentLength, LastModified, Range, owner);

    private static ContentRange ReadRange(string? header, long? contentLength, WireContext context)
    {
        if (header is null)
        {
            throw context.Unexpected("Risposta non interpretabile: una risposta 206 non ha l'intestazione Content-Range.");
        }

        if (!ContentRangeHeader.TryParse(header, out var range) || range is null)
        {
            throw context.Unexpected("Risposta non interpretabile: l'intestazione Content-Range non e' nella forma 'bytes primo-ultimo/totale'.");
        }

        // La lunghezza della parte deve tornare con l'intervallo: il trasporto conta i byte contro Content-Length, quindi un'incoerenza qui
        // passerebbe inosservata fino alla fine della lettura.
        if (contentLength is { } length && length != range.LastByte - range.FirstByte + 1)
        {
            throw context.Unexpected("Risposta non interpretabile: il Content-Length non corrisponde all'intervallo del Content-Range.");
        }

        return range;
    }

    // Il primo valore dell'intestazione come testo grezzo (non il valore interpretato di System.Net.Http: cambia tra .NET Framework e .NET).
    private static string? FirstValue(HttpContentHeaders headers, string name) =>
        headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
}
