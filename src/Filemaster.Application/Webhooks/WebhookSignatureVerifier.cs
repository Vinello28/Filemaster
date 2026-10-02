using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace Filemaster.Application;

/// <summary>
/// Verifica la firma di una consegna di webhook di Sharp-a-File (ramo dev) prima di fidarsi del corpo. Il formato e' quello
/// che emette il server, riletto da <c>WebhookSignature.Sign</c> e da <c>WebhookDispatcher</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Il formato.</b> L'header <see cref="WebhookHeaders.Signature"/> vale <c>t=&lt;secondi unix&gt;,v1=&lt;esadecimale
/// minuscolo di 64 caratteri&gt;</c>. La firma e' un HMAC-SHA256 il cui <b>messaggio</b> e' il testo <c>&lt;t&gt;.</c>
/// (UTF-8) seguito dai <b>byte grezzi del corpo</b> cosi' come sono arrivati, e la cui <b>chiave</b> e' l'UTF-8 dell'<b>intero
/// segreto, prefisso <c>whsec_</c> compreso</b>: il prefisso non va tolto e il segreto non va decodificato. Il server firma
/// a ogni tentativo con l'ora di quel momento, quindi un ritentativo ha un <c>t</c> e una firma nuovi, mentre il corpo (e il
/// suo <c>delivery_id</c>) e' lo stesso.
/// </para>
/// <para>
/// <b>Il corpo si passa come <c>byte[]</c>, mai come testo.</b> Per questo non esiste un overload che prenda una
/// <see cref="string"/>: ricodificare il testo ricevuto (un altro Unicode, un altro fine riga, un BOM tolto o aggiunto) o
/// rileggere e riscrivere il JSON (anche solo re-indentato) cambia i byte e la firma non torna piu'. Si legge il corpo della
/// richiesta come byte, si verifica, e solo dopo lo si interpreta (<see cref="WebhookEventParser"/>).
/// </para>
/// <para>
/// <b>Cosa controlla</b>, in quest'ordine: l'header, la firma (confronto a tempo costante contro <b>tutte</b> le <c>v1</c>,
/// senza uscita anticipata), poi la finestra di tempo. La firma viene prima della finestra, cosi' chi non ha il segreto
/// non ricava nulla sulla finestra. La finestra e' simmetrica: <c>|ora - t| &lt;= tolleranza</c>, quindi un <c>t</c> nel
/// futuro oltre la tolleranza e' rifiutato come uno nel passato; esattamente alla tolleranza e' accettato, un secondo oltre
/// no. L'ora e' <c>TimeProvider.GetUtcNow()</c> in secondi interi.
/// </para>
/// <para>
/// <b>Rotazione del segreto.</b> Un header con piu' elementi <c>v1</c> e' valido se almeno uno corrisponde (oggi il server ne
/// emette una sola). Gli elementi con chiave sconosciuta (per esempio <c>v2</c>) si ignorano.
/// </para>
/// <para>
/// <b>Una firma valida non basta per non elaborare due volte.</b> La consegna e' "almeno una volta": il server ritenta finche'
/// il ricevitore non risponde con successo, e anche un ritentativo ha una firma valida. Chi riceve deve deduplicare per
/// <c>delivery_id</c> (<c>WebhookEvent.DeliveryId</c>), che resta uguale tra i ritentativi. La finestra di tolleranza limita il
/// riuso di una firma rubata, non rende l'elaborazione idempotente.
/// </para>
/// <para>
/// <b>Input di rete e argomenti di programmazione.</b> Un header o un corpo sbagliati non lanciano mai: danno un
/// <see cref="WebhookSignatureResult"/> non valido. Lanciano solo gli argomenti che indicano un errore di chi scrive il
/// codice (corpo null, segreto null o vuoto, tolleranza non positiva).
/// </para>
/// <para>
/// <b>Il segreto</b> non compare mai in <see cref="ToString"/>, nelle eccezioni ne' nei risultati, e non e' esposto da nessuna
/// proprieta'. Non viene modificato: nessun <c>Trim</c> (un segreto letto da un file con un a capo finale e' un altro segreto,
/// e le consegne risulteranno tutte <see cref="WebhookSignatureFailure.SignatureMismatch"/>). L'istanza e' immutabile e
/// sicura da usare da piu' thread: ogni chiamata crea il proprio HMAC.
/// </para>
/// </remarks>
public sealed class WebhookSignatureVerifier
{
    /// <summary>La tolleranza di default: 5 minuti, da una parte e dall'altra dell'ora del ricevitore.</summary>
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromMinutes(5);

    // SHA-256: 32 byte, 64 caratteri esadecimali.
    private const int MacSize = 32;

    private readonly byte[] _key;
    private readonly long _toleranceSeconds;
    private readonly TimeProvider _timeProvider;

    /// <summary>Crea il verificatore per il segreto di un abbonamento.</summary>
    /// <param name="secret">
    /// Il segreto dell'abbonamento cosi' come lo restituisce il server alla creazione, con il prefisso <c>whsec_</c> (il server
    /// lo mostra una volta sola). Non viene modificato.
    /// </param>
    /// <param name="tolerance">
    /// Quanto l'ora <c>t</c> della firma puo' distare dall'ora del ricevitore, in ciascuna direzione; null per
    /// <see cref="DefaultTolerance"/> (5 minuti). Deve essere positiva. Il confronto e' in secondi interi: le frazioni di
    /// secondo si trascurano.
    /// </param>
    /// <param name="timeProvider">La fonte dell'ora; null per <see cref="TimeProvider.System"/>. Nei test si passa un orologio finto.</param>
    /// <exception cref="ArgumentNullException"><paramref name="secret"/> e' null.</exception>
    /// <exception cref="ArgumentException"><paramref name="secret"/> e' vuoto o di soli spazi.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tolerance"/> e' zero o negativa.</exception>
    public WebhookSignatureVerifier(string secret, TimeSpan? tolerance = null, TimeProvider? timeProvider = null)
    {
        Guard.NotNull(secret);
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new ArgumentException("Il segreto del webhook non puo' essere vuoto.", nameof(secret));
        }

        var window = tolerance ?? DefaultTolerance;
        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(tolerance), "La tolleranza deve essere positiva.");
        }

        _key = Encoding.UTF8.GetBytes(secret);
        Tolerance = window;
        _toleranceSeconds = window.Ticks / TimeSpan.TicksPerSecond;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>La tolleranza in uso, da una parte e dall'altra dell'ora del ricevitore.</summary>
    public TimeSpan Tolerance { get; }

    /// <summary>
    /// Verifica la firma di una consegna. Non lancia per un header o un corpo sbagliati: il motivo e' in
    /// <see cref="WebhookSignatureResult.Failure"/>.
    /// </summary>
    /// <param name="signatureHeader">
    /// Il valore dell'header <see cref="WebhookHeaders.Signature"/>; null se la richiesta non l'aveva. Gli elementi sono
    /// separati da virgole, ognuno <c>chiave=valore</c>; spazi e tabulazioni ai bordi di un elemento si tollerano. Serve
    /// <b>esattamente un</b> <c>t</c> (solo cifre ASCII decimali, senza segno, nel campo di un <see cref="long"/>) e
    /// <b>almeno un</b> <c>v1</c>. Una <c>v1</c> che non e' di 64 cifre esadecimali ASCII (minuscole o maiuscole) e' un
    /// candidato che non puo' corrispondere, e non impedisce agli altri di corrispondere. Il <c>t</c> si usa cosi' com'e' scritto
    /// nell'header, senza normalizzarlo: <c>t=01700000000</c> non e' lo stesso messaggio firmato di <c>t=1700000000</c>.
    /// </param>
    /// <param name="body">I byte del corpo della richiesta cosi' come sono arrivati, senza alcuna rielaborazione.</param>
    /// <returns>Il risultato: valido, oppure non valido con il motivo.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="body"/> e' null (un corpo vuoto, invece, e' un input valido da verificare).</exception>
    public WebhookSignatureResult Verify(string? signatureHeader, byte[] body)
    {
        Guard.NotNull(body);

        if (signatureHeader is null || string.IsNullOrWhiteSpace(signatureHeader))
        {
            return WebhookSignatureResult.Invalid(WebhookSignatureFailure.MissingHeader);
        }

        if (!TryParseHeader(signatureHeader, out var timestampText, out var timestamp, out var candidates))
        {
            return WebhookSignatureResult.Invalid(WebhookSignatureFailure.MalformedHeader);
        }

        var expected = ComputeMac(timestampText, body);

        // Tutti i candidati, sempre, a tempo costante: nessuna uscita al primo che corrisponde ne' al primo che no.
        var matches = 0;
        foreach (var candidate in candidates)
        {
            if (candidate is not null)
            {
                matches |= FixedTimeEquals(candidate, expected) ? 1 : 0;
            }
        }

        if (matches == 0)
        {
            return WebhookSignatureResult.Invalid(WebhookSignatureFailure.SignatureMismatch);
        }

        // Limiti calcolati con somme e non con una sottrazione tra t e l'ora: t puo' arrivare a long.MaxValue.
        var now = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        if (timestamp < now - _toleranceSeconds)
        {
            return WebhookSignatureResult.Invalid(WebhookSignatureFailure.TimestampOutOfTolerance);
        }

        if (timestamp > now + _toleranceSeconds)
        {
            return WebhookSignatureResult.Invalid(WebhookSignatureFailure.TimestampOutOfTolerance);
        }

        return WebhookSignatureResult.Valid;
    }

    /// <summary>Una riga leggibile con la sola tolleranza: il segreto non compare.</summary>
    /// <returns>Il testo, senza dati riservati.</returns>
    public override string ToString() =>
        "WebhookSignatureVerifier (tolerance " + Tolerance.ToString("c", CultureInfo.InvariantCulture) + ")";

    // HMAC-SHA256 di "<t>." + corpo con la chiave = UTF-8 del segreto intero. TransformBlock/TransformFinalBlock (e non
    // HMACSHA256.HashData, assente su netstandard2.0) evitano di copiare il corpo per anteporre il prefisso.
    private byte[] ComputeMac(string timestampText, byte[] body)
    {
        var prefix = Encoding.UTF8.GetBytes(timestampText + ".");
        using var hmac = new HMACSHA256(_key);
        hmac.TransformBlock(prefix, 0, prefix.Length, null, 0);
        hmac.TransformFinalBlock(body, 0, body.Length);
        return hmac.Hash!;
    }

    /// <summary>
    /// Legge l'header. Vero se ha esattamente un <c>t</c> valido e almeno un <c>v1</c>; <paramref name="candidates"/> ha un
    /// elemento per ogni <c>v1</c> (null se non e' di 64 cifre esadecimali ASCII).
    /// </summary>
    internal static bool TryParseHeader(string header, out string timestampText, out long timestamp, out List<byte[]?> candidates)
    {
        timestampText = string.Empty;
        timestamp = 0;
        candidates = new List<byte[]?>();
        var hasTimestamp = false;

        var start = 0;
        while (true)
        {
            var comma = header.IndexOf(',', start);
            var end = comma < 0 ? header.Length : comma;

            var from = start;
            var to = end;
            while (from < to && IsOptionalWhitespace(header[from]))
            {
                from++;
            }

            while (to > from && IsOptionalWhitespace(header[to - 1]))
            {
                to--;
            }

            var equals = header.IndexOf('=', from, to - from);
            if (equals < 0)
            {
                return false;
            }

            var key = header.Substring(from, equals - from);
            var value = header.Substring(equals + 1, to - equals - 1);
            switch (key)
            {
                case "t":
                    if (hasTimestamp || !TryParseUnixSeconds(value, out timestamp))
                    {
                        return false;
                    }

                    timestampText = value;
                    hasTimestamp = true;
                    break;
                case "v1":
                    candidates.Add(TryDecodeHex(value));
                    break;
            }

            if (comma < 0)
            {
                break;
            }

            start = comma + 1;
        }

        return hasTimestamp && candidates.Count > 0;
    }

    /// <summary>Confronto a tempo costante di due array: legge sempre tutti i byte (le lunghezze non sono un segreto).</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    internal static bool FixedTimeEquals(byte[] left, byte[] right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        var difference = 0;
        for (var i = 0; i < left.Length; i++)
        {
            difference |= left[i] ^ right[i];
        }

        return difference == 0;
    }

    /// <summary>
    /// Decodifica esattamente <c>2 * 32</c> cifre esadecimali ASCII (maiuscole o minuscole); null per qualunque altra cosa.
    /// Confronti ASCII espliciti, mai <c>char.IsDigit</c> o <c>char.IsLetter</c>: accetterebbero cifre e lettere Unicode.
    /// </summary>
    internal static byte[]? TryDecodeHex(string value)
    {
        if (value.Length != MacSize * 2)
        {
            return null;
        }

        var bytes = new byte[MacSize];
        for (var i = 0; i < bytes.Length; i++)
        {
            var high = HexValue(value[2 * i]);
            var low = HexValue(value[(2 * i) + 1]);
            if (high < 0 || low < 0)
            {
                return null;
            }

            bytes[i] = (byte)((high << 4) | low);
        }

        return bytes;
    }

    /// <summary>
    /// Vero se <paramref name="value"/> e' un intero non negativo in sole cifre ASCII che sta in un <see cref="long"/>
    /// (niente segno, niente spazi, niente cifre Unicode, niente vuoto).
    /// </summary>
    private static bool TryParseUnixSeconds(string value, out long seconds)
    {
        seconds = 0;
        if (value.Length == 0)
        {
            return false;
        }

        long result = 0;
        foreach (var c in value)
        {
            if (c < '0' || c > '9')
            {
                return false;
            }

            var digit = c - '0';
            if (result > (long.MaxValue - digit) / 10)
            {
                return false;
            }

            result = (result * 10) + digit;
        }

        seconds = result;
        return true;
    }

    private static int HexValue(char c)
    {
        if (c >= '0' && c <= '9')
        {
            return c - '0';
        }

        if (c >= 'a' && c <= 'f')
        {
            return c - 'a' + 10;
        }

        if (c >= 'A' && c <= 'F')
        {
            return c - 'A' + 10;
        }

        return -1;
    }

    // Solo spazio e tabulazione (OWS di RFC 9110): string.Trim() toglierebbe anche gli spazi Unicode.
    private static bool IsOptionalWhitespace(char c) => c == ' ' || c == '\t';
}
