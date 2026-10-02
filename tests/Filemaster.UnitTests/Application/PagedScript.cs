using Filemaster.Application;

namespace Filemaster.UnitTests.Application;

/// <summary>
/// Le pagine di un elenco finto, indicizzate per cursore (la chiave null e' la prima pagina): chi chiede un cursore sconosciuto
/// o supera <see cref="MaxCalls"/> riceve un errore subito, cosi' un'iterazione che non finisce (un mutante che non avanza il cursore, o
/// che chiede una pagina in piu') fa fallire il test invece di bloccare l'esecuzione. Registra ogni richiesta e ogni token.
/// </summary>
public sealed class PagedScript<T>
{
    private const string FirstPageKey = "<first-page>";

    private readonly Dictionary<string, Page<T>> _pages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Exception> _failures = new(StringComparer.Ordinal);

    /// <summary>Le richieste ricevute, nell'ordine.</summary>
    public List<PageRequest?> Requests { get; } = new();

    /// <summary>I token ricevuti, nell'ordine.</summary>
    public List<CancellationToken> Tokens { get; } = new();

    /// <summary>Oltre questo numero di richieste la risposta e' un errore.</summary>
    public int MaxCalls { get; set; } = 30;

    /// <summary>Chiamata a ogni richiesta (numero da 1) prima di rispondere; puo' annullare, aspettare o lanciare.</summary>
    public Func<int, CancellationToken, Task>? OnCall { get; set; }

    /// <summary>Aggiunge la pagina che si ottiene con <paramref name="cursor"/> (null per la prima).</summary>
    public PagedScript<T> Add(string? cursor, IReadOnlyList<T> items, string? next)
    {
        _pages[cursor ?? FirstPageKey] = new Page<T>(items, next);
        return this;
    }

    /// <summary>Fa lanciare <paramref name="exception"/> a chi chiede la pagina con <paramref name="cursor"/>.</summary>
    public PagedScript<T> Fail(string? cursor, Exception exception)
    {
        _failures[cursor ?? FirstPageKey] = exception;
        return this;
    }

    /// <summary>La risposta di <c>ListAsync</c>.</summary>
    public async Task<Page<T>> ListAsync(PageRequest? page, CancellationToken cancellationToken)
    {
        Requests.Add(page);
        Tokens.Add(cancellationToken);
        if (Requests.Count > MaxCalls)
        {
            throw new InvalidOperationException($"Piu' di {MaxCalls} richieste: l'iterazione non finisce.");
        }

        if (OnCall is not null)
        {
            await OnCall(Requests.Count, cancellationToken);
        }

        var key = page?.Cursor ?? FirstPageKey;
        if (_failures.TryGetValue(key, out var failure))
        {
            throw failure;
        }

        return _pages.TryGetValue(key, out var result)
            ? result
            : throw new InvalidOperationException($"Nessuna pagina per il cursore '{key}'.");
    }
}
