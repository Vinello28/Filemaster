using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace Filemaster;

/// <summary>
/// Controlla il gestore primario del client HTTP <see cref="FilemasterServiceCollectionExtensions.HttpClientName"/> DOPO tutte le
/// configurazioni (un filtro avvolge le azioni del costruttore dei gestori, comprese le <c>ConfigurePrimaryHttpMessageHandler</c>
/// aggiunte dall'utente dopo <c>AddFilemaster</c>): un primario che segue i redirect o decomprime e' rifiutato con
/// <see cref="InvalidOperationException"/> quando la factory crea il gestore. Gli altri client nominati non si toccano.
/// </summary>
internal sealed class FilemasterHandlerCheck : IHttpMessageHandlerBuilderFilter
{
    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) =>
        builder =>
        {
            next(builder);
            if (!string.Equals(builder.Name, FilemasterServiceCollectionExtensions.HttpClientName, StringComparison.Ordinal))
            {
                return;
            }

            var problem = FilemasterHandlers.FindUnsafeSetting(builder.PrimaryHandler);
            if (problem is not null)
            {
                throw new InvalidOperationException("Client HTTP '" + FilemasterServiceCollectionExtensions.HttpClientName + "': " + problem);
            }
        };
}
