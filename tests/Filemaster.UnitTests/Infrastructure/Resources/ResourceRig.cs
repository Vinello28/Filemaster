using Filemaster.Application;
using Filemaster.Domain;
using Filemaster.Infrastructure;
using Filemaster.UnitTests.Infrastructure.Documents;
using Filemaster.UnitTests.Wire;

namespace Filemaster.UnitTests.Infrastructure.Resources;

/// <summary>
/// I quattro adapter di cartelle, contatti, ente e sonde su UN trasporto con gestore finto. Riusa lo <see cref="StoreRig"/> dei documenti
/// (registrazione delle richieste col corpo, <see cref="SentRequest"/>), cosi' le due suite guardano le richieste allo stesso modo.
/// </summary>
internal sealed class ResourceRig : IDisposable
{
    /// <summary>L'id del contatto della fixture derivata <c>contact-detail</c>.</summary>
    internal static readonly ContactId Contact = new("con_01M3VEF0P1Q2R3S4T5V6W7X8Y9");

    internal ResourceRig(Action<FilemasterOptions>? configure = null)
    {
        Inner = new StoreRig(configure);
        var transport = Inner.Rig.Transport;
        Folders = new HttpFolderCatalog(transport);
        Contacts = new HttpContactDirectory(transport);
        Tenant = new HttpTenantInfo(transport);
        Health = new HttpFilemasterHealth(transport);
    }

    internal StoreRig Inner { get; }

    internal TransportRig Rig => Inner.Rig;

    internal FakeHandler Handler => Inner.Handler;

    internal List<SentRequest> Sent => Inner.Sent;

    internal SentRequest Single => Inner.Single;

    internal HttpFolderCatalog Folders { get; }

    internal HttpContactDirectory Contacts { get; }

    internal HttpTenantInfo Tenant { get; }

    internal HttpFilemasterHealth Health { get; }

    /// <summary>Le operazioni, per i test che le passano tutte (nome -> chiamata).</summary>
    internal static IReadOnlyList<string> AllOperations { get; } = new[]
    {
        "folder-create", "folder-update", "folder-list", "folder-delete",
        "contact-list", "contact-get", "contact-categories",
        "tenant", "healthz", "readyz",
    };

    /// <summary>Le operazioni che sono <c>GET</c> (ritentate dal trasporto).</summary>
    internal static IReadOnlyList<string> Reads { get; } = new[]
    {
        "folder-list", "contact-list", "contact-get", "contact-categories", "tenant", "healthz", "readyz",
    };

    /// <summary>Le scritture (<c>POST</c>, <c>PATCH</c>, <c>DELETE</c>): mai ritentate.</summary>
    internal static IReadOnlyList<string> Writes { get; } = new[] { "folder-create", "folder-update", "folder-delete" };

    internal ResourceRig Then(Func<HttpResponseMessage> response)
    {
        Inner.Then(response);
        return this;
    }

    internal ResourceRig Then(Func<HttpRequestMessage, Task<HttpResponseMessage>> step)
    {
        Inner.Then(step);
        return this;
    }

    internal ResourceRig ThenFail(Exception exception)
    {
        Inner.ThenFail(exception);
        return this;
    }

    /// <summary>Esegue l'operazione con argomenti validi.</summary>
    internal Task Call(string operation, CancellationToken cancellationToken = default) => operation switch
    {
        "folder-create" => Folders.CreateAsync(new CreateFolderRequest(new FolderCode("FATTURE"), "Fatture"), cancellationToken),
        "folder-update" => Folders.UpdateAsync(new FolderCode("FATTURE.2026"), new UpdateFolderRequest { Name = "Fatture 2026 rinominata" }, cancellationToken),
        "folder-list" => Folders.ListChildrenAsync(cancellationToken: cancellationToken),
        "folder-delete" => Folders.DeleteAsync(new FolderCode("FATTURE"), cancellationToken),
        "contact-list" => Contacts.ListAsync(cancellationToken: cancellationToken),
        "contact-get" => Contacts.GetAsync(Contact, cancellationToken),
        "contact-categories" => Contacts.ListCategoriesAsync(cancellationToken),
        "tenant" => Tenant.GetAsync(cancellationToken),
        "healthz" => Health.CheckLivenessAsync(cancellationToken),
        "readyz" => Health.CheckReadinessAsync(cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "operazione sconosciuta"),
    };

    /// <summary>Una risposta di successo valida per l'operazione (dalle fixture catturate o derivate).</summary>
    internal static HttpResponseMessage Success(string operation) => operation switch
    {
        "folder-create" => FixtureReply.Json("21-folders-create-parent"),
        "folder-update" => FixtureReply.Json("28-folders-patch-name"),
        "folder-list" => FixtureReply.Json("24-folders-list"),
        "folder-delete" => Reply.Empty(204),
        "contact-list" => Derived("contacts-page"),
        "contact-get" => Derived("contact-detail"),
        "contact-categories" => Derived("contact-categories"),
        "tenant" => FixtureReply.Json("03-tenant"),
        "healthz" => Healthz(),
        "readyz" => FixtureReply.Json("02-readyz"),
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "operazione sconosciuta"),
    };

    /// <summary>Il corpo di una fixture derivata (non catturata), 200, <c>application/json</c>.</summary>
    internal static HttpResponseMessage Derived(string name)
    {
        var bytes = WireFixtures.Derived(name);
        return Reply.Bytes(200, bytes, bytes.Length, "application/json");
    }

    /// <summary>La risposta catturata di <c>/healthz</c> (cattura 01): 200, <c>text/plain</c>, <c>ok</c>.</summary>
    internal static HttpResponseMessage Healthz()
    {
        var bytes = WireFixtures.Captured("01-healthz", "txt");
        return Reply.Bytes(WireFixtures.Status("01-healthz"), bytes, bytes.Length, "text/plain");
    }

    public void Dispose() => Inner.Dispose();
}
