using Filemaster.Domain;

namespace Filemaster.Infrastructure;

/// <summary>
/// I percorsi del server, relativi all'indirizzo base e SENZA barra iniziale (una barra iniziale scarterebbe l'eventuale prefisso di
/// percorso dell'indirizzo base: vedi <c>TransportRequest</c>). Gli id si usano gia' validati: un id o un codice vuoto (<c>default</c>)
/// costruirebbe un percorso sbagliato (<c>documents/</c>) e viene rifiutato con <see cref="ArgumentException"/>. I valori validi sono
/// fatti solo di caratteri sicuri in un percorso (lettere e cifre ASCII, <c>_</c>, <c>.</c>, <c>-</c>), quindi non si codificano.
/// </summary>
internal static class Routes
{
    /// <summary><c>documents</c>: caricamento (<c>POST</c>) ed elenco (<c>GET</c>).</summary>
    internal const string Documents = "documents";

    /// <summary><c>POST documents/bulk/move</c>.</summary>
    internal const string DocumentsBulkMove = "documents/bulk/move";

    /// <summary><c>POST documents/bulk/verify</c>.</summary>
    internal const string DocumentsBulkVerify = "documents/bulk/verify";

    /// <summary><c>POST folders</c> e <c>GET folders</c>.</summary>
    internal const string Folders = "folders";

    /// <summary><c>GET contacts</c>.</summary>
    internal const string Contacts = "contacts";

    /// <summary><c>GET contact-categories</c>.</summary>
    internal const string ContactCategories = "contact-categories";

    /// <summary><c>GET tenant</c>.</summary>
    internal const string Tenant = "tenant";

    /// <summary><c>GET healthz</c> (anonima).</summary>
    internal const string Healthz = "healthz";

    /// <summary><c>GET readyz</c> (anonima).</summary>
    internal const string Readyz = "readyz";

    /// <summary><c>documents/{id}</c>: dettaglio e cancellazione.</summary>
    /// <exception cref="ArgumentException"><paramref name="id"/> e' l'id vuoto.</exception>
    internal static string Document(DocumentId id) => Documents + "/" + Segment(id.IsEmpty, id.Value, nameof(id), "L'id del documento");

    /// <summary><c>documents/{id}/content</c>.</summary>
    /// <exception cref="ArgumentException"><paramref name="id"/> e' l'id vuoto.</exception>
    internal static string DocumentContent(DocumentId id) => Document(id) + "/content";

    /// <summary><c>documents/{id}/preview</c>.</summary>
    /// <exception cref="ArgumentException"><paramref name="id"/> e' l'id vuoto.</exception>
    internal static string DocumentPreview(DocumentId id) => Document(id) + "/preview";

    /// <summary><c>documents/{id}/verify</c>.</summary>
    /// <exception cref="ArgumentException"><paramref name="id"/> e' l'id vuoto.</exception>
    internal static string DocumentVerify(DocumentId id) => Document(id) + "/verify";

    /// <summary><c>documents/{id}/folder</c>: spostamento di un documento (<c>PATCH</c>).</summary>
    /// <exception cref="ArgumentException"><paramref name="id"/> e' l'id vuoto.</exception>
    internal static string DocumentFolder(DocumentId id) => Document(id) + "/folder";

    /// <summary><c>folders/{code}</c>: modifica (<c>PATCH</c>) e cancellazione di una cartella.</summary>
    /// <exception cref="ArgumentException"><paramref name="code"/> e' il codice vuoto.</exception>
    internal static string Folder(FolderCode code) => Folders + "/" + Segment(code.IsEmpty, code.Value, nameof(code), "Il codice della cartella");

    /// <summary><c>contacts/{id}</c>.</summary>
    /// <exception cref="ArgumentException"><paramref name="id"/> e' l'id vuoto.</exception>
    internal static string Contact(ContactId id) => Contacts + "/" + Segment(id.IsEmpty, id.Value, nameof(id), "L'id del contatto");

    private static string Segment(bool isEmpty, string value, string paramName, string what) =>
        isEmpty
            ? throw new ArgumentException(what + " e' vuoto (default): indicare un valore valido.", paramName)
            : value;
}
