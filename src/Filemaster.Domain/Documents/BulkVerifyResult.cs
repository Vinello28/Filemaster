namespace Filemaster.Domain;

/// <summary>
/// Esito di una verifica d'integrita' su piu' documenti insieme: solo i conteggi. Se un id non esiste il server non
/// verifica nulla e risponde 404 sull'intero lotto, quindi un lotto riuscito ha sempre <see cref="Total"/> uguale alla
/// somma delle altre tre voci. Il dettaglio dei singoli esiti negativi non c'e': per averlo si verifica un documento alla
/// volta (<see cref="IntegrityCheck"/>), oppure si ascolta l'evento <see cref="DocumentIntegrityFailedEvent"/>.
/// </summary>
public sealed record BulkVerifyResult(int Total, int Verified, int Failed, int WithoutContent)
{
    /// <summary>I documenti distinti del lotto: un id ripetuto conta una volta.</summary>
    public int Total { get; } = Total;

    /// <summary>I documenti con contenuto il cui SHA-256 ricalcolato coincide con quello registrato.</summary>
    public int Verified { get; } = Verified;

    /// <summary>I documenti con contenuto che non passano la verifica (contenuto alterato, corrotto o assente dal disco).</summary>
    public int Failed { get; } = Failed;

    /// <summary>I documenti importati con i soli metadati: non hanno un contenuto e non si verificano.</summary>
    public int WithoutContent { get; } = WithoutContent;
}
