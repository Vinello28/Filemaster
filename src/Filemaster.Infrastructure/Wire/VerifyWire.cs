using Filemaster.Domain;

namespace Filemaster.Infrastructure;

/// <summary>
/// Gli esiti delle verifiche d'integrita' sul filo: <c>{"document_id","sha256","ok","detail"?,"checked_at"}</c> per un documento e
/// <c>{"total","verified","failed","without_content"}</c> per un lotto. Un contenuto che non torna e' <c>ok:false</c> con 200, non un errore.
/// </summary>
internal static class VerifyWire
{
    /// <summary>Legge l'esito della verifica di un documento.</summary>
    /// <param name="body">I byte del corpo.</param>
    /// <param name="context">Lo status e l'id di correlazione della risposta.</param>
    /// <exception cref="UnexpectedResponseException">Il corpo non ha la forma di un esito di verifica.</exception>
    internal static IntegrityCheck ReadIntegrityCheck(byte[]? body, WireContext context) =>
        WireJson.ReadObject(
            body,
            context,
            "verifica",
            check => new IntegrityCheck(
                check.RequiredId<DocumentId>("document_id", DocumentId.TryParse, "un id di documento"),
                check.RequiredSha256("sha256"),
                check.RequiredBool("ok"),
                check.OptionalString("detail"),
                check.RequiredDate("checked_at")));

    /// <summary>Legge i conteggi di una verifica in blocco.</summary>
    /// <param name="body">I byte del corpo.</param>
    /// <param name="context">Lo status e l'id di correlazione della risposta.</param>
    /// <exception cref="UnexpectedResponseException">Il corpo non ha la forma di un esito di verifica in blocco.</exception>
    internal static BulkVerifyResult ReadBulkVerify(byte[]? body, WireContext context) =>
        WireJson.ReadObject(
            body,
            context,
            "verifica in blocco",
            result => new BulkVerifyResult(
                result.RequiredCount("total"),
                result.RequiredCount("verified"),
                result.RequiredCount("failed"),
                result.RequiredCount("without_content")));
}
