namespace Filemaster.Infrastructure;

/// <summary>Un corpo letto fino a un limite: i byte (al massimo il limite) e se ce n'erano di piu'.</summary>
internal sealed class BoundedBody
{
    internal BoundedBody(byte[] bytes, bool exceeded)
    {
        Bytes = bytes;
        Exceeded = exceeded;
    }

    /// <summary>I byte letti, al massimo il limite.</summary>
    internal byte[] Bytes { get; }

    /// <summary>Vero se il corpo aveva piu' byte del limite (quelli in eccesso non sono stati letti).</summary>
    internal bool Exceeded { get; }
}

/// <summary>Helper per leggere un corpo HTTP uguale su netstandard2.0 e sui runtime moderni.</summary>
internal static class BodyReading
{
    private const int BufferSize = 8192;

    /// <summary>
    /// Apre lo stream del contenuto. Su netstandard2.0 <c>ReadAsStreamAsync(CancellationToken)</c> non esiste: con
    /// <c>ResponseHeadersRead</c> <c>ReadAsStreamAsync()</c> restituisce subito lo stream di rete, senza attendere il corpo.
    /// </summary>
    internal static Task<Stream> OpenAsync(HttpContent content, CancellationToken cancellationToken)
    {
#if NET
        return content.ReadAsStreamAsync(cancellationToken);
#else
        cancellationToken.ThrowIfCancellationRequested();
        return content.ReadAsStreamAsync();
#endif
    }

    /// <summary>
    /// Legge il corpo di una risposta fino a <paramref name="maxBytes"/> byte. Se il <c>Content-Length</c> dichiarato e' gia' piu'
    /// grande non legge nulla; altrimenti legge fino a un byte oltre il limite per sapere se ce n'erano di piu'. Una connessione
    /// chiusa durante la lettura (<see cref="ObjectDisposedException"/> su .NET Framework) diventa <see cref="IOException"/>, cosi' si
    /// tratta come ogni altro errore di rete.
    /// </summary>
    internal static async Task<BoundedBody> ReadBoundedAsync(HttpContent? content, int maxBytes, CancellationToken cancellationToken)
    {
        if (content is null)
        {
            return new BoundedBody(Array.Empty<byte>(), exceeded: false);
        }

        if (content.Headers.ContentLength is { } declared && declared > maxBytes)
        {
            return new BoundedBody(Array.Empty<byte>(), exceeded: true);
        }

        try
        {
            using var stream = await OpenAsync(content, cancellationToken).ConfigureAwait(false);
            using var collected = new MemoryStream();
            var buffer = new byte[BufferSize];
            while (collected.Length <= maxBytes)
            {
                var wanted = (int)Math.Min(buffer.Length, maxBytes + 1L - collected.Length);
#pragma warning disable CA1835 // Su netstandard2.0 l'overload con Memory non esiste: qui si usa la stessa forma con array.
                var read = await stream.ReadAsync(buffer, 0, wanted, cancellationToken).ConfigureAwait(false);
#pragma warning restore CA1835
                if (read == 0)
                {
                    break;
                }

                collected.Write(buffer, 0, read);
            }

            if (collected.Length > maxBytes)
            {
                var truncated = new byte[maxBytes];
                Array.Copy(collected.GetBuffer(), truncated, maxBytes);
                return new BoundedBody(truncated, exceeded: true);
            }

            return new BoundedBody(collected.ToArray(), exceeded: false);
        }
        catch (ObjectDisposedException exception)
        {
            throw new IOException("La connessione e' stata chiusa durante la lettura della risposta.", exception);
        }
    }
}
