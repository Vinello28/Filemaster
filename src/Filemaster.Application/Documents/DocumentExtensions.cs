using Filemaster.Domain;

namespace Filemaster.Application;

/// <summary>Estensioni di <see cref="Document"/> per leggere cio' che il server non tipizza.</summary>
public static class DocumentExtensions
{
    /// <summary>
    /// Legge la sezione <c>arxivar</c> dei metadati del documento (quella che <c>arxivar-sync</c> del server scrive nei documenti
    /// importati da ARXivar): e' <see cref="ArxivarMetadata.From"/> applicato a <see cref="Document.Metadata"/>.
    /// </summary>
    /// <remarks>
    /// Non lancia per metadati insoliti: <see cref="ArxivarMetadata.From"/> tollera sezione assente, tipi sbagliati e campi in piu'.
    /// L'elemento <see cref="Document.Metadata"/> deve essere ancora valido (il suo <c>JsonDocument</c> non smaltito); il risultato
    /// non trattiene nulla di quell'elemento.
    /// </remarks>
    /// <param name="document">Il documento.</param>
    /// <returns>I dati ARXivar, oppure null se il documento non ha una sezione <c>arxivar</c> valida (assente, non un oggetto, senza <c>docnumber</c> intero).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> e' null.</exception>
    public static ArxivarMetadata? GetArxivarMetadata(this Document document)
    {
        Guard.NotNull(document);
        return ArxivarMetadata.From(document.Metadata);
    }
}
