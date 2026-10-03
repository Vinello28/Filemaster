namespace Filemaster.IntegrationTests.Live;

/// <summary>
/// Cio' che <c>eng/e2e/seed.sh</c> inserisce via SQL e le API non sanno creare (anagrafica in sola lettura, documento senza
/// contenuto). Gli stessi valori sono scritti nello script: cambiarli in entrambi i posti.
/// </summary>
internal static class LiveSeed
{
    internal const string TenantSlug = "e2e";
    internal const string TenantName = "Filemaster E2E";

    internal const string CategoryId = "E2E-FORNITORI";
    internal const string CategoryName = "Fornitori E2E";
    internal const int CategoryArxivarId = 9001;

    /// <summary>Contatto esterno, nella categoria, con quasi tutti i campi e un nome non ASCII (a con l'accento grave).</summary>
    internal const string ExternalCode = "E2E-EXT";
    internal const string ExternalName = "Fornitore E2E Citt\U000000E0 Srl";
    internal const int ExternalArxivarId = 9101;

    internal const string UserCode = "E2E-USR";
    internal const string UserName = "Utente E2E";
    internal const int UserArxivarId = 9102;

    internal const string GroupCode = "E2E-GRP";
    internal const string GroupName = "Gruppo E2E";

    /// <summary>Il testo che trova i tre contatti (e solo loro) nella ricerca libera (nome, codice, citta', partita IVA, codice fiscale).</summary>
    internal const string SearchText = "E2E";

    /// <summary>Documento SENZA contenuto (sha256 NULL, 0 byte): mittente il contatto esterno, destinatari utente e gruppo.</summary>
    internal const string DocumentOwner = "e2e-seed";
    internal const string DocumentFileName = "e2e-seed-senza-contenuto.pdf";
}
