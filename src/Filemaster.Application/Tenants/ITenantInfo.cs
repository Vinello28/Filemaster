using Filemaster.Domain;

namespace Filemaster.Application;

/// <summary>
/// L'ente a cui appartiene la chiave API (<c>GET /tenant</c>). E' anche la sonda di <b>connettivita' e autenticazione</b>:
/// una chiamata che riesce prova che il server e' raggiungibile e che la chiave e' valida e attiva, cosa che
/// <see cref="IFilemasterHealth"/> (anonima) non puo' dire.
/// </summary>
/// <remarks>
/// Valgono le regole comuni di <see cref="IDocumentStore"/> (annullamento, errori 401/403/connessione/5xx). Richiede lo scope
/// <c>read</c>, quindi una chiave valida di qualunque scope va bene.
/// </remarks>
public interface ITenantInfo
{
    /// <summary>Legge l'ente della chiave API in uso.</summary>
    /// <param name="cancellationToken">Per annullare la chiamata.</param>
    /// <returns>L'ente.</returns>
    /// <exception cref="UnauthorizedException">La chiave e' assente, errata o revocata (401): e' l'esito tipico di una configurazione sbagliata.</exception>
    /// <exception cref="ForbiddenException">L'ente e' sospeso (403): con la chiave di un ente sospeso il server rifiuta ogni richiesta.</exception>
    Task<Tenant> GetAsync(CancellationToken cancellationToken = default);
}
