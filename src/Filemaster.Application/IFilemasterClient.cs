namespace Filemaster.Application;

/// <summary>
/// La facciata del client: raggruppa le porte per risorsa e le <b>espone come proprieta'</b>, non le eredita (una porta per
/// risorsa, nessuna interfaccia con quaranta metodi). Chi ha bisogno di una sola risorsa dichiara la dipendenza dalla
/// porta (<see cref="IDocumentStore"/>) e non dall'intero client; chi li vuole tutti inietta questo.
/// </summary>
/// <remarks>
/// <para>
/// L'implementazione sta in Infrastructure e si crea con la composizione del pacchetto <c>Filemaster</c>
/// (<c>AddFilemaster</c> o la factory per chi non usa l'iniezione delle dipendenze). Questa interfaccia non e'
/// <see cref="IDisposable"/> di proposito: il client riusa un <c>HttpClient</c> che dura quanto il processo, e non possiede
/// niente che chi lo usa debba rilasciare.
/// </para>
/// <para>
/// <b>Aggiungere un membro a un'interfaccia e' una modifica incompatibile</b> per chi la implementa (su netstandard2.0 non
/// esistono i metodi di interfaccia con implementazione di default): finche' la libreria e' in 0.x puo' succedere, e il
/// changelog lo dira'. Chi scrive un fake per i propri test puo' implementare una sola porta: quella che il codice sotto test usa.
/// </para>
/// </remarks>
public interface IFilemasterClient
{
    /// <summary>I documenti: caricamento, elenco, contenuto, verifica, spostamento, cancellazione.</summary>
    IDocumentStore Documents { get; }

    /// <summary>Le cartelle: creazione, rinomina, elenco dei figli, cancellazione.</summary>
    IFolderCatalog Folders { get; }

    /// <summary>L'anagrafica dei contatti, in sola lettura.</summary>
    IContactDirectory Contacts { get; }

    /// <summary>L'ente della chiave API, e la sonda di connettivita' e autenticazione.</summary>
    ITenantInfo Tenant { get; }

    /// <summary>Le sonde di salute anonime del server (<c>/healthz</c>, <c>/readyz</c>).</summary>
    IFilemasterHealth Health { get; }
}
