using System.Globalization;
using System.Text;

namespace Filemaster.Infrastructure;

/// <summary>
/// Costruisce la query string di una richiesta: i parametri nell'ordine in cui si aggiungono (l'ordine e' stabile, quindi il testo e'
/// prevedibile), nessun parametro per un valore null, ogni valore codificato con <see cref="PercentEncoding"/>, i numeri con la cultura
/// invariante. I nomi sono costanti del codice (minuscole e sottolineatura), mai dati dell'utente: non si codificano. Il risultato e'
/// una stringa vuota o <c>?nome=valore&amp;...</c>, da accodare al percorso.
/// </summary>
internal sealed class QueryBuilder
{
    private readonly StringBuilder _text = new();

    /// <summary>Aggiunge <paramref name="name"/>=<paramref name="value"/> codificato; un valore null non aggiunge nulla (una stringa vuota si': <c>nome=</c>).</summary>
    /// <param name="name">Il nome del parametro, come lo vuole il server (canonico: mai gli alias italiani).</param>
    /// <param name="value">Il valore, cosi' com'e': il server lo trimma.</param>
    /// <param name="paramName">La proprieta' dell'utente da citare in un'eccezione (surrogato isolato).</param>
    /// <exception cref="ArgumentException"><paramref name="value"/> contiene un surrogato isolato.</exception>
    internal QueryBuilder Add(string name, string? value, string paramName)
    {
        if (value is not null)
        {
            Append(name, PercentEncoding.Encode(value, paramName));
        }

        return this;
    }

    /// <summary>Aggiunge un numero (cifre ASCII, cultura invariante); null non aggiunge nulla.</summary>
    internal QueryBuilder Add(string name, int? value)
    {
        if (value is { } number)
        {
            Append(name, number.ToString(CultureInfo.InvariantCulture));
        }

        return this;
    }

    /// <summary>Il testo da accodare al percorso: vuoto se non c'e' nessun parametro, altrimenti comincia con <c>?</c>.</summary>
    internal string Build() => _text.Length == 0 ? string.Empty : "?" + _text;

    private void Append(string name, string encodedValue)
    {
        if (_text.Length > 0)
        {
            _text.Append('&');
        }

        _text.Append(name).Append('=').Append(encodedValue);
    }
}
