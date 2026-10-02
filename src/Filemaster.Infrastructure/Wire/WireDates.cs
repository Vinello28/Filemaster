using System.Globalization;

namespace Filemaster.Infrastructure;

/// <summary>
/// Le date sul filo: RFC 3339 con il fuso scritto. In lettura si usano i due formati che il server stesso usa per le sue date
/// (<c>yyyy-MM-ddTHH:mm:ss[.f]Z</c> e <c>yyyy-MM-ddTHH:mm:ss[.f]+hh:mm</c>, da 0 a 7 decimali), mai l'ora locale: una data senza
/// fuso non e' valida (<c>System.Text.Json</c> la leggerebbe come ora locale della macchina). In scrittura si produce sempre
/// un istante UTC con la <c>Z</c>. Tutto con la cultura invariante: la cultura corrente (per esempio ar-SA, con il calendario
/// Um Al Qura) non influisce mai.
/// </summary>
internal static class WireDates
{
    // Stessi formati di QueryParsing.Bound del server. Il primo ha la Z come testo fisso: senza AssumeUniversal il risultato
    // sarebbe un'ora locale con una Z finta.
    private static readonly string[] ReadFormats =
    {
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
    };

    private const string WriteFormat = "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'";

    /// <summary>
    /// Legge una data RFC 3339 con fuso. Falso se manca il fuso, se il testo e' null o ha spazi ai bordi, se i decimali sono piu' di sette
    /// o se l'istante UTC esce dall'intervallo di <see cref="DateTimeOffset"/> (per esempio <c>9999-12-31T23:59:59-01:00</c>).
    /// Tollera, come il server, il fuso senza due punti (<c>+0200</c>) e il punto senza decimali (<c>12.Z</c>).
    /// </summary>
    internal static bool TryParse(string? text, out DateTimeOffset value) =>
        DateTimeOffset.TryParseExact(text, ReadFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out value);

    /// <summary>
    /// L'istante in UTC con la <c>Z</c>, per esempio <c>2026-10-01T10:00:00Z</c> per mezzogiorno a +02:00. I decimali (fino a sette)
    /// compaiono solo se non sono zero.
    /// </summary>
    internal static string Format(DateTimeOffset value) =>
        value.UtcDateTime.ToString(WriteFormat, CultureInfo.InvariantCulture);
}
