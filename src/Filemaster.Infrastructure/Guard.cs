using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Filemaster.Infrastructure;

/// <summary>Controlli sugli argomenti (ArgumentNullException.ThrowIfNull non esiste su netstandard2.0).</summary>
internal static class Guard
{
    internal static void NotNull([NotNull] object? value, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (value is null)
        {
            throw new ArgumentNullException(name);
        }
    }
}
