using Filemaster.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Filemaster;

/// <summary>
/// Collega <see cref="FilemasterOptions.Validate"/> al sistema delle opzioni: un'opzione non valida diventa un fallimento con il
/// messaggio di <see cref="FilemasterOptions.Validate"/> (che non riporta mai la chiave). Vale solo per le opzioni con il nome di
/// <see cref="FilemasterServiceCollectionExtensions.HttpClientName"/>.
/// </summary>
internal sealed class FilemasterOptionsValidator : IValidateOptions<FilemasterOptions>
{
    public ValidateOptionsResult Validate(string? name, FilemasterOptions options)
    {
        if (!string.Equals(name, FilemasterServiceCollectionExtensions.HttpClientName, StringComparison.Ordinal))
        {
            return ValidateOptionsResult.Skip;
        }

        try
        {
            options.Validate();
            return ValidateOptionsResult.Success;
        }
        catch (ArgumentException exception)
        {
            return ValidateOptionsResult.Fail(exception.Message);
        }
    }
}
