using Filemaster.Application;
using Filemaster.Domain;

namespace Filemaster.Infrastructure;

/// <summary>
/// Le cartelle sul filo. Il server le descrive con <c>{"id","parent_id"?,"name","created_at"}</c>: l'<c>id</c> e' il codice scelto
/// da chi la crea (<see cref="FolderCode"/>), <c>parent_id</c> manca per una cartella di primo livello.
/// </summary>
internal static class FolderWire
{
    /// <summary>Legge una cartella (risposta di creazione e di modifica).</summary>
    /// <param name="body">I byte del corpo.</param>
    /// <param name="context">Lo status e l'id di correlazione della risposta.</param>
    /// <exception cref="UnexpectedResponseException">Il corpo non ha la forma di una cartella, o un codice non e' valido.</exception>
    internal static Folder ReadFolder(byte[]? body, WireContext context) =>
        WireJson.ReadObject(body, context, "cartella", ReadFolder);

    /// <summary>Legge l'elenco dei figli di una cartella: <c>{"items":[...]}</c>, non paginato.</summary>
    /// <param name="body">I byte del corpo.</param>
    /// <param name="context">Lo status e l'id di correlazione della risposta.</param>
    /// <exception cref="UnexpectedResponseException">Il corpo non ha la forma di un elenco di cartelle.</exception>
    internal static IReadOnlyList<Folder> ReadFolders(byte[]? body, WireContext context) =>
        PageWire.ReadItems(body, context, ReadFolder);

    /// <summary>
    /// Il corpo di <c>POST folders</c>: <c>{"id":"...","parent_id":"..."?,"name":"..."}</c>, in quest'ordine e con <c>parent_id</c> solo se c'e'
    /// (e' l'ordine delle richieste che il server ha accettato nelle catture 21 e 22). Il nome si manda cosi' com'e' (il server lo trimma).
    /// </summary>
    /// <param name="request">La richiesta; si valida con <see cref="CreateFolderRequest.Validate"/> prima di scrivere.</param>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> e' null.</exception>
    /// <exception cref="ArgumentException">La richiesta non e' valida, o il nome ha un surrogato isolato.</exception>
    internal static byte[] CreateBody(CreateFolderRequest request)
    {
        Guard.NotNull(request);
        request.Validate();
        PercentEncoding.RequireWellFormed(request.Name, nameof(CreateFolderRequest.Name));
        return WireJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("id", request.Code.Value);
            if (request.ParentId is { } parent)
            {
                writer.WriteString("parent_id", parent.Value);
            }

            writer.WriteString("name", request.Name);
            writer.WriteEndObject();
        });
    }

    /// <summary>
    /// Il corpo di <c>PATCH folders/{id}</c>: <c>{"id":"..."?,"name":"..."?}</c> con solo i campi impostati (un campo assente resta com'e'; un
    /// <c>null</c> esplicito non e' la stessa cosa per il server, quindi non si scrive mai). Il nuovo codice si chiama <c>id</c> sul filo.
    /// </summary>
    /// <param name="request">La richiesta; si valida con <see cref="UpdateFolderRequest.Validate"/> (almeno un campo).</param>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> e' null.</exception>
    /// <exception cref="ArgumentException">La richiesta non e' valida, o il nome ha un surrogato isolato.</exception>
    internal static byte[] UpdateBody(UpdateFolderRequest request)
    {
        Guard.NotNull(request);
        request.Validate();
        if (request.Name is not null)
        {
            PercentEncoding.RequireWellFormed(request.Name, nameof(UpdateFolderRequest.Name));
        }

        return WireJson.Write(writer =>
        {
            writer.WriteStartObject();
            if (request.NewCode is { } code)
            {
                writer.WriteString("id", code.Value);
            }

            if (request.Name is not null)
            {
                writer.WriteString("name", request.Name);
            }

            writer.WriteEndObject();
        });
    }

    /// <summary>
    /// Il percorso dell'elenco dei figli: <c>folders</c> per il primo livello, <c>folders?parent_id=CODICE</c> per le sottocartelle (non
    /// paginato).
    /// </summary>
    /// <param name="parent">La cartella madre; null per il primo livello.</param>
    /// <exception cref="ArgumentException"><paramref name="parent"/> e' il codice vuoto: per il primo livello si passa null.</exception>
    internal static string ListPath(FolderCode? parent)
    {
        if (parent is { IsEmpty: true })
        {
            throw new ArgumentException("Il codice della cartella madre e' vuoto (default): per il primo livello si passa null.", nameof(parent));
        }

        return Routes.Folders + new QueryBuilder().Add("parent_id", parent?.Value, nameof(parent)).Build();
    }

    private static Folder ReadFolder(WireObject folder) =>
        new(
            folder.RequiredId<FolderCode>("id", FolderCode.TryParse, "un codice di cartella"),
            folder.OptionalId<FolderCode>("parent_id", FolderCode.TryParse, "un codice di cartella"),
            folder.RequiredString("name"),
            folder.RequiredDate("created_at"));
}
