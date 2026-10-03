using System.Net;

namespace Filemaster.Infrastructure;

/// <summary>
/// La parte "file" di un caricamento: scrive lo stream dell'utente nel corpo della richiesta <b>dalla posizione corrente</b>, a pezzi,
/// <b>senza bufferizzarlo</b> e <b>senza chiuderlo</b> (il trasporto smaltisce il messaggio e quindi questo contenuto: uno
/// <see cref="StreamContent"/> chiuderebbe lo stream dell'utente, che la porta promette di lasciare aperto).
/// </summary>
/// <remarks>
/// <para>
/// <b>Lunghezza.</b> Se lo stream si puo' riposizionare (<see cref="Stream.CanSeek"/>) la lunghezza e' nota senza leggerlo:
/// <c>Length - Position</c>, fotografata alla costruzione (cioe' subito prima dell'invio); cosi' il corpo multipart ha un
/// <c>Content-Length</c> e non serve il <c>chunked</c>, che non tutti i proxy e i gestori trattano allo stesso modo. Se non si puo'
/// riposizionare (o <c>Length</c>/<c>Position</c> non sono supportati) la lunghezza e' ignota e il corpo va in <c>chunked</c>: non si
/// legge mai lo stream per misurarlo. Se lo stream consegna piu' o meno byte di quelli dichiarati e' il gestore HTTP a fallire
/// (errore di rete, mai ritentato).
/// </para>
/// <para>
/// <b>.NET Framework (net48).</b> <c>HttpClientHandler</c> puo' bufferizzare comunque il corpo della richiesta in memoria
/// (<c>AllowWriteStreamBuffering</c>): non si risolve qui ma dove si crea il gestore. <b>Non verificato in locale</b> (si prova su
/// Windows CI, T6.1).
/// </para>
/// <para>
/// Lo si scrive una volta sola: un caricamento non si ritenta mai, e lo stream dell'utente non si riavvolge.
/// </para>
/// </remarks>
internal sealed class UploadStreamContent : HttpContent
{
    private const int BufferSize = 81920;

    private readonly Stream _source;
    private readonly long? _length;

    /// <summary>Avvolge lo stream dell'utente senza leggerlo.</summary>
    /// <param name="source">Lo stream dell'utente, leggibile; non viene ne' riposizionato ne' chiuso.</param>
    internal UploadStreamContent(Stream source)
    {
        Guard.NotNull(source);
        _source = source;
        _length = RemainingLength(source);
    }

    /// <summary>Scrive lo stream dell'utente, dalla posizione corrente alla fine.</summary>
    /// <param name="stream">Il corpo della richiesta.</param>
    /// <param name="context">Il contesto del trasporto (non usato).</param>
    /// <returns>Il task della copia.</returns>
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        CopyAsync(stream, CancellationToken.None);

#if NET
    /// <summary>Scrive lo stream dell'utente, dalla posizione corrente alla fine, rispettando l'annullamento.</summary>
    /// <param name="stream">Il corpo della richiesta.</param>
    /// <param name="context">Il contesto del trasporto (non usato).</param>
    /// <param name="cancellationToken">Per interrompere la copia.</param>
    /// <returns>Il task della copia.</returns>
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
        CopyAsync(stream, cancellationToken);
#endif

    /// <summary>La lunghezza, se lo stream si puo' riposizionare; altrimenti nessuna (corpo <c>chunked</c>).</summary>
    /// <param name="length">I byte che restano dalla posizione corrente alla fine.</param>
    /// <returns>Vero se la lunghezza e' nota.</returns>
    protected override bool TryComputeLength(out long length)
    {
        length = _length ?? 0;
        return _length.HasValue;
    }

    // Nessun override di Dispose: lo smaltimento della base non tocca _source, e lo stream dell'utente resta aperto (e' suo).
    private static long? RemainingLength(Stream source)
    {
        if (!source.CanSeek)
        {
            return null;
        }

        try
        {
            return Math.Max(0, source.Length - source.Position);
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private async Task CopyAsync(Stream target, CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];
        while (true)
        {
#pragma warning disable CA1835 // Su netstandard2.0 gli overload con Memory non esistono: la stessa forma con array ovunque.
            var read = await _source.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            await target.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
#pragma warning restore CA1835
        }
    }
}
