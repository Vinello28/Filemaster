using Filemaster.Domain;

namespace Filemaster.Application;

/// <summary>
/// Esito di un caricamento: il documento creato e se il suo contenuto c'era gia'. Il documento e' sempre nuovo (id nuovo,
/// con i campi di questa richiesta): la deduplica riguarda solo i byte, che il server tiene una volta sola per ente.
/// </summary>
/// <remarks>
/// <see cref="Document.Contacts"/> e' vuota anche se il documento ne avra': il server include i contatti solo nel dettaglio.
/// </remarks>
public sealed record UploadResult(Document Document, bool Deduplicated)
{
    /// <summary>Il documento creato.</summary>
    public Document Document { get; } = Document;

    /// <summary>
    /// Vero se lo stesso contenuto (stessi byte, nello stesso ente) era gia' presente, anche sotto un altro nome di file:
    /// il server non ha riscritto i byte ma ha creato comunque un documento nuovo. Falso se i byte sono nuovi.
    /// </summary>
    public bool Deduplicated { get; } = Deduplicated;
}
