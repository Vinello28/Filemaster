using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Filemaster.Domain;

namespace Filemaster.Application;

/// <summary>
/// Trasforma il corpo di una consegna di webhook di Sharp-a-File (ramo dev) in un <see cref="WebhookEvent"/> del dominio.
/// Si chiama <b>dopo</b> <see cref="WebhookSignatureVerifier.Verify"/>: il parser non controlla la firma e non puo' sapere se il
/// corpo e' autentico.
/// </summary>
/// <remarks>
/// <para>
/// <b>La forma del corpo</b> (da <c>WebhookDispatcher.BuildBody</c> del server): un oggetto
/// <c>{"event","delivery_id","occurred_at","payload"}</c>. <c>event</c> e' il nome dell'evento, <c>delivery_id</c> l'identificatore della consegna
/// (un numero JSON intero positivo, per tolleranza anche un testo non vuoto; resta una stringa), <c>occurred_at</c> il momento in cui il server ha accodato la consegna (ISO 8601 UTC, con <c>Z</c>) e
/// <c>payload</c> l'oggetto dell'evento. I payload dei tre eventi che questa versione conosce, derivati dal codice del server
/// (<c>DocumentService</c>, serializzati in snake_case senza omettere i null, a differenza delle risposte delle API):
/// </para>
/// <list type="bullet">
/// <item><description><c>document.uploaded</c>: <c>{"document_id","filename","sha256","deduplicated"}</c> (attenzione: <c>filename</c>, non <c>original_filename</c>) per <see cref="DocumentUploadedEvent"/>;</description></item>
/// <item><description><c>document.deleted</c>: <c>{"document_id","sha256"}</c>, con <c>sha256</c> anche <c>null</c> (documento senza contenuto), per <see cref="DocumentDeletedEvent"/>;</description></item>
/// <item><description><c>document.integrity_failed</c>: <c>{"document_id","sha256","detail"}</c>, con <c>detail</c> anche <c>null</c>, per <see cref="DocumentIntegrityFailedEvent"/>.</description></item>
/// </list>
/// <para>
/// <b>Gli id</b>: <c>document_id</c> e' un numero JSON in <c>document.uploaded</c> e <c>document.integrity_failed</c> ma una stringa di cifre
/// in <c>document.deleted</c> (cosi' manda il server); si accettano entrambe le forme, con la regola di <see cref="DocumentId"/>
/// (decimale canonico da 1 a <c>long.MaxValue</c>: <c>1.5</c>, <c>-3</c>, <c>"042"</c> e <c>"doc_x"</c> sono un payload malformato).
/// <c>delivery_id</c> e' un numero JSON intero positivo; per tolleranza si accetta anche un testo non vuoto. In entrambi i casi
/// <see cref="WebhookEvent.DeliveryId"/> resta una stringa (un numero diventa le sue cifre decimali).
/// </para>
/// <para>
/// <b>Il mapping nome -> tipo e' di questo parser</b> (il dominio non ha stringhe del filo): il confronto del nome e' esatto
/// (maiuscole comprese). Un evento di nome sconosciuto, per esempio uno che il server ha aggiunto dopo questa versione del
/// client, diventa un <see cref="UnknownWebhookEvent"/> con il nome grezzo e il payload cosi' com'e': mai un errore.
/// </para>
/// <para>
/// <b>Un evento di nome noto con un payload malformato</b> (non e' un oggetto, un campo manca o ha il tipo sbagliato, un
/// <c>document_id</c> non valido, <c>deduplicated</c> che non e' un booleano JSON, <c>sha256</c> <c>null</c> dove non e'
/// ammesso) diventa anch'esso un <see cref="UnknownWebhookEvent"/>, con il nome noto in <see cref="UnknownWebhookEvent.EventType"/> e il
/// payload grezzo. Una consegna non lancia mai per il suo payload: un errore 500 del ricevitore farebbe ritentare inutilmente il
/// server (la consegna e' "almeno una volta"). Un campo <c>null</c> esplicito vale solo dove il tipo lo ammette; un campo
/// assente e' sempre malformato. I campi in piu' si ignorano.
/// </para>
/// <para>
/// <b>Il payload di <see cref="UnknownWebhookEvent"/> e' un <c>Clone()</c></b> dell'elemento letto: il <see cref="JsonDocument"/> del
/// parser si smaltisce prima di restituire l'evento, e il clone resta leggibile (non e' uguale, per valore o per identita', a un
/// altro elemento con lo stesso contenuto).
/// </para>
/// <para>
/// <b>La busta non conforme non e' un evento sconosciuto</b>: un corpo che non e' JSON, non e' un oggetto, o ha <c>event</c>,
/// <c>delivery_id</c> o <c>occurred_at</c> mancanti o del tipo sbagliato non si puo' attribuire a nessuna consegna, quindi
/// <see cref="TryParse"/> restituisce falso e <see cref="Parse"/> lancia <see cref="FormatException"/>. In particolare
/// <c>occurred_at</c> deve avere il fuso esplicito (<c>Z</c> oppure <c>+hh:mm</c>): una data senza fuso System.Text.Json la
/// leggerebbe come ora locale della macchina, e un orario che dipende dal fuso di chi riceve non e' accettabile. Un
/// <c>delivery_id</c> vuoto o di soli spazi e' rifiutato: e' la chiave di deduplicazione. <c>payload</c> assente non e' un errore:
/// l'evento e' sconosciuto con un payload il cui <c>ValueKind</c> e' <c>Undefined</c>. Nessuna eccezione di System.Text.Json
/// esce da questa classe, nemmeno per un testo con UTF-8 non valido o con surrogati isolati.
/// </para>
/// <para>
/// <b>Un BOM UTF-8 iniziale (<c>EF BB BF</c>) e' tollerato</b> e ignorato (RFC 8259 lo permette ai parser; System.Text.Json
/// invece lo rifiuta, quindi lo si toglie a mano). Il server non ne emette: la firma, calcolata sui byte grezzi BOM compreso, resta
/// valida o no a prescindere da questa scelta.
/// </para>
/// <para>
/// <b>Deduplica.</b> La consegna e' "almeno una volta": lo stesso evento puo' arrivare piu' volte (con firme diverse, perche' il
/// server firma a ogni tentativo con l'ora corrente). <see cref="WebhookEvent.DeliveryId"/> resta uguale tra i tentativi: e' la
/// chiave con cui chi riceve evita di elaborare due volte lo stesso evento.
/// </para>
/// </remarks>
public static class WebhookEventParser
{
    private const string DocumentUploaded = "document.uploaded";
    private const string DocumentDeleted = "document.deleted";
    private const string DocumentIntegrityFailed = "document.integrity_failed";

    /// <summary>Interpreta il corpo di una consegna gia' verificata; non lancia per un corpo che non e' una consegna valida.</summary>
    /// <param name="body">I byte del corpo, cosi' come sono arrivati (e come li ha verificati <see cref="WebhookSignatureVerifier"/>).</param>
    /// <param name="result">L'evento; null se la busta non e' conforme.</param>
    /// <returns>Vero se la busta e' conforme (anche se l'evento e' un <see cref="UnknownWebhookEvent"/>); falso altrimenti.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="body"/> e' null (un corpo vuoto e' invece un corpo non valido: restituisce falso).</exception>
    public static bool TryParse(byte[] body, [NotNullWhen(true)] out WebhookEvent? result)
    {
        Guard.NotNull(body);
        result = Read(body, out _);
        return result is not null;
    }

    /// <summary>Interpreta il corpo di una consegna gia' verificata.</summary>
    /// <param name="body">I byte del corpo, cosi' come sono arrivati (e come li ha verificati <see cref="WebhookSignatureVerifier"/>).</param>
    /// <returns>L'evento: uno dei tre tipi noti, oppure un <see cref="UnknownWebhookEvent"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="body"/> e' null.</exception>
    /// <exception cref="FormatException">La busta non e' conforme (il messaggio dice perche', senza riportare il corpo).</exception>
    public static WebhookEvent Parse(byte[] body)
    {
        Guard.NotNull(body);
        var result = Read(body, out var error);
        return result ?? throw new FormatException("Il corpo del webhook non e' una consegna valida: " + error + ".");
    }

    private static WebhookEvent? Read(byte[] body, out string? error)
    {
        ReadOnlyMemory<byte> utf8 = body;
        if (body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF)
        {
            utf8 = utf8.Slice(3);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8);
        }
        catch (JsonException)
        {
            error = "non e' JSON valido";
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "non e' un oggetto JSON";
                return null;
            }

            try
            {
                return ReadEnvelope(root, out error);
            }
            catch (InvalidOperationException)
            {
                // System.Text.Json accetta in Parse un UTF-8 non valido o un surrogato isolato (\ud800) dentro una stringa
                // e li rifiuta solo alla lettura del testo, con InvalidOperationException.
                error = "contiene testo non valido (UTF-8 non valido o surrogato isolato)";
                return null;
            }
        }
    }

    private static WebhookEvent? ReadEnvelope(JsonElement root, out string? error)
    {
        error = null;
        if (!TryGetString(root, "event", out var eventType))
        {
            error = "manca il campo 'event' o non e' una stringa";
            return null;
        }

        if (!TryGetDeliveryId(root, out var deliveryId))
        {
            error = "manca il campo 'delivery_id' o non e' un intero positivo ne' una stringa non vuota";
            return null;
        }

        if (!TryGetOccurredAt(root, out var occurredAt))
        {
            error = "manca il campo 'occurred_at' o non e' una data ISO 8601 con fuso (Z oppure +hh:mm)";
            return null;
        }

        var known = ReadKnown(eventType, deliveryId, occurredAt, root);
        if (known is not null)
        {
            return known;
        }

        // Nome sconosciuto, oppure noto con un payload che non si lascia leggere: il payload grezzo, copiato perche' il
        // JsonDocument si smaltisce uscendo da Read.
        var payload = root.TryGetProperty("payload", out var raw) ? raw.Clone() : default;
        return new UnknownWebhookEvent(deliveryId, occurredAt, eventType, payload);
    }

    // Null se il nome non e' noto o il payload e' malformato (nel secondo caso il chiamante lo tratta come sconosciuto).
    private static WebhookEvent? ReadKnown(string eventType, string deliveryId, DateTimeOffset occurredAt, JsonElement root)
    {
        if (eventType != DocumentUploaded && eventType != DocumentDeleted && eventType != DocumentIntegrityFailed)
        {
            return null;
        }

        if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        try
        {
            switch (eventType)
            {
                case DocumentUploaded:
                    return TryGetDocumentId(payload, out var uploadedId)
                        && TryGetString(payload, "filename", out var filename)
                        && TryGetString(payload, "sha256", out var uploadedSha)
                        && TryGetBoolean(payload, "deduplicated", out var deduplicated)
                        ? new DocumentUploadedEvent(deliveryId, occurredAt, uploadedId, filename, uploadedSha, deduplicated)
                        : null;
                case DocumentDeleted:
                    return TryGetDocumentId(payload, out var deletedId)
                        && TryGetNullableString(payload, "sha256", out var deletedSha)
                        ? new DocumentDeletedEvent(deliveryId, occurredAt, deletedId, deletedSha)
                        : null;
                default:
                    return TryGetDocumentId(payload, out var failedId)
                        && TryGetString(payload, "sha256", out var failedSha)
                        && TryGetNullableString(payload, "detail", out var detail)
                        ? new DocumentIntegrityFailedEvent(deliveryId, occurredAt, failedId, failedSha, detail)
                        : null;
            }
        }
        catch (InvalidOperationException)
        {
            // Un testo del payload con UTF-8 non valido o un surrogato isolato: payload malformato, quindi evento sconosciuto.
            return null;
        }
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString()!;
            return true;
        }

        value = string.Empty;
        return false;
    }

    // Un campo che il server manda sempre, ma che puo' valere null: assente o di un altro tipo e' malformato.
    private static bool TryGetNullableString(JsonElement element, string name, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString();
            return true;
        }

        return false;
    }

    private static bool TryGetBoolean(JsonElement element, string name, out bool value)
    {
        value = false;
        if (!element.TryGetProperty(name, out var property))
        {
            return false;
        }

        switch (property.ValueKind)
        {
            case JsonValueKind.True:
                value = true;
                return true;
            case JsonValueKind.False:
                return true;
            default:
                return false;
        }
    }

    // Il server scrive document_id come NUMERO in document.uploaded e document.integrity_failed e come STRINGA di cifre in
    // document.deleted (misurato nella cattura t64): si accettano entrambe le forme, e in tutte e due vale la regola di DocumentId.TryParse
    // (cifre ASCII, niente segno, niente zero iniziale, da 1 a long.MaxValue). Il testo di un token numerico e' il suo GetRawText(),
    // senza passare da double: 1.0, 1e3, -5 e un numero oltre long.MaxValue non sono id.
    private static bool TryGetDocumentId(JsonElement payload, out DocumentId id)
    {
        id = default;
        if (!payload.TryGetProperty("document_id", out var property))
        {
            return false;
        }

        switch (property.ValueKind)
        {
            case JsonValueKind.Number:
                return DocumentId.TryParse(property.GetRawText(), out id);
            case JsonValueKind.String:
                return DocumentId.TryParse(property.GetString(), out id);
            default:
                return false;
        }
    }

    // delivery_id e' un numero JSON intero positivo (bigint sul server) o, per tolleranza, una stringa non vuota: resta una stringa
    // (la chiave di deduplicazione), nella forma decimale canonica se arriva come numero. Un numero decimale, con esponente, negativo,
    // zero o fuori da long non e' una chiave attendibile: busta non conforme.
    private static bool TryGetDeliveryId(JsonElement root, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty("delivery_id", out var property))
        {
            return false;
        }

        switch (property.ValueKind)
        {
            case JsonValueKind.String:
                var text = property.GetString()!;
                if (string.IsNullOrWhiteSpace(text))
                {
                    return false;
                }

                value = text;
                return true;
            case JsonValueKind.Number:
                var raw = property.GetRawText();
                if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
                {
                    return false;
                }

                value = raw;
                return true;
            default:
                return false;
        }
    }

    // System.Text.Json legge una data senza fuso come ora locale della macchina: qui il fuso deve essere scritto (Z oppure
    // +hh:mm), altrimenti la busta non e' conforme. Il server scrive sempre una Z.
    private static bool TryGetOccurredAt(JsonElement root, out DateTimeOffset value)
    {
        value = default;
        if (!root.TryGetProperty("occurred_at", out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = property.GetString()!;
        return HasExplicitOffset(text) && property.TryGetDateTimeOffset(out value);
    }

    private static bool HasExplicitOffset(string text)
    {
        var length = text.Length;
        if (length > 0 && text[length - 1] == 'Z')
        {
            return true;
        }

        return length >= 6 && (text[length - 6] == '+' || text[length - 6] == '-') && text[length - 3] == ':';
    }
}
