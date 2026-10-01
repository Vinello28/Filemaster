using Filemaster.Domain;

namespace Filemaster.Application;

/// <summary>
/// Modifica di una cartella (<c>PATCH /folders/{id}</c>, solo sul server dev): un nuovo codice, un nuovo nome o entrambi.
/// Quel che e' null resta com'e'; almeno uno dei due deve essere impostato. E' un tipo di input: nessun argomento
/// obbligatorio, le modifiche si impostano con <c>set</c> (nessun <c>init</c>, quindi si usa anche da C# 7.3).
/// </summary>
/// <remarks>
/// Cambiare il codice lo propaga alle sottocartelle e ai documenti che lo usano (il server lo fa in un'unica istruzione);
/// chi tiene il vecchio codice in giro (un archivio, un segnalibro) ora ha un riferimento a una cartella che non esiste piu'.
/// La cartella madre non si cambia: il server non sposta le cartelle.
/// </remarks>
public sealed class UpdateFolderRequest
{
    /// <summary>
    /// Il nuovo codice, che sostituisce quello attuale (<c>id</c> nel corpo del <c>PATCH</c>); null per non cambiarlo. Se e'
    /// gia' usato da un'altra cartella dell'ente il server risponde 409 (<see cref="ConflictException"/>).
    /// </summary>
    public FolderCode? NewCode { get; set; }

    /// <summary>
    /// Il nuovo nome; null per non cambiarlo. Non vuoto dopo il trim e di al massimo
    /// <see cref="CreateFolderRequest.MaxNameLength"/> caratteri; deve restare unico fra le cartelle con lo stesso padre
    /// (altrimenti 409). Una stringa vuota non e' "non cambiare": il server la rifiuta.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>Controlla i limiti del server prima di spedire; lancia alla prima violazione.</summary>
    /// <exception cref="ArgumentException">
    /// Nessuna modifica e' impostata (<see cref="ArgumentException.ParamName"/> null: la colpa e' della combinazione),
    /// <see cref="NewCode"/> e' il codice vuoto (<c>default</c>), oppure <see cref="Name"/> e' vuoto, di soli spazi o
    /// oltre <see cref="CreateFolderRequest.MaxNameLength"/> caratteri dopo il trim.
    /// </exception>
    public void Validate()
    {
        if (NewCode is null && Name is null)
        {
            throw new ArgumentException("Indicare almeno uno fra il nuovo codice e il nuovo nome della cartella.");
        }

        RequestChecks.NotEmpty(NewCode?.IsEmpty, nameof(NewCode), "Il nuovo codice della cartella");
        if (Name is not null)
        {
            RequestChecks.NotBlank(Name, nameof(Name));
            RequestChecks.MaxTrimmedLength(Name, CreateFolderRequest.MaxNameLength, nameof(Name));
        }
    }
}
