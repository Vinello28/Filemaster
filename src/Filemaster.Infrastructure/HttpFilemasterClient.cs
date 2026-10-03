using Filemaster.Application;

namespace Filemaster.Infrastructure;

/// <summary>
/// La facciata HTTP: le cinque porte, create una volta sullo <b>stesso</b> <see cref="FilemasterTransport"/> (stesse opzioni, stessa
/// chiave, un solo avviso per <c>http</c> non di loopback) ed esposte come proprieta'. Non tiene altro stato: e' thread-safe.
/// </summary>
internal sealed class HttpFilemasterClient : IFilemasterClient
{
    /// <summary>Crea le porte sul trasporto indicato.</summary>
    /// <param name="transport">Il trasporto condiviso.</param>
    /// <exception cref="ArgumentNullException"><paramref name="transport"/> e' null.</exception>
    internal HttpFilemasterClient(FilemasterTransport transport)
    {
        Guard.NotNull(transport);
        Documents = new HttpDocumentStore(transport);
        Folders = new HttpFolderCatalog(transport);
        Contacts = new HttpContactDirectory(transport);
        Tenant = new HttpTenantInfo(transport);
        Health = new HttpFilemasterHealth(transport);
    }

    /// <inheritdoc />
    public IDocumentStore Documents { get; }

    /// <inheritdoc />
    public IFolderCatalog Folders { get; }

    /// <inheritdoc />
    public IContactDirectory Contacts { get; }

    /// <inheritdoc />
    public ITenantInfo Tenant { get; }

    /// <inheritdoc />
    public IFilemasterHealth Health { get; }
}
