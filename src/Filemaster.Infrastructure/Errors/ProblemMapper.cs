using System.Globalization;
using Filemaster.Domain;

namespace Filemaster.Infrastructure;

/// <summary>
/// Dalla risposta d'errore del server all'eccezione del Domain, con la regola scritta nel doc XML di
/// <see cref="FilemasterException"/>: prima lo <b>slug</b> del problem+json, poi lo <b>status</b> quando lo slug manca o non e'
/// riconosciuto; fa eccezione il 415, dove vale sempre lo status. E' una funzione pura: non legge la rete e non conosce i tentativi
/// (il trasporto la chiama dopo il ciclo di ritentativi, sull'ultima risposta).
/// </summary>
/// <remarks>
/// <para>
/// <b>Slug noti</b> (quelli che il server <c>dev</c> emette davvero): <c>validation-error</c> 400 -> <see cref="InvalidRequestException"/>;
/// <c>unauthorized</c> -> <see cref="UnauthorizedException"/>; <c>forbidden</c> -> <see cref="ForbiddenException"/>; <c>not-found</c> ->
/// <see cref="NotFoundException"/>; <c>conflict</c> -> <see cref="ConflictException"/>; <c>content-unavailable</c> (stesso status 409 del
/// conflitto: si distingue solo per slug) -> <see cref="ContentUnavailableException"/>; <c>request-too-large</c> ->
/// <see cref="RequestTooLargeException"/>; <c>unsupported-media-type</c> -> <see cref="UnsupportedMediaTypeException"/>;
/// <c>storage-not-configured</c> -> <see cref="StorageNotConfiguredException"/>; <c>internal-error</c> -> <see cref="ServerErrorException"/>;
/// <c>method-not-allowed</c> -> <see cref="UnexpectedResponseException"/>. Con uno slug noto vince lo slug, qualunque sia lo status
/// (l'eccezione porta lo status vero): per esempio il server risponde <c>validation-error</c> anche con status 408 o 431 quando
/// e' Kestrel a rifiutare la richiesta.
/// </para>
/// <para>
/// <b>Il 415 e' sempre <see cref="UnsupportedMediaTypeException"/></b>, qualunque slug (<c>unsupported-media-type</c>, ma anche
/// <c>error</c> e <c>validation-error</c> quando manca o e' sbagliato il Content-Type).
/// </para>
/// <para>
/// <b>Lo slug generico <c>error</c></b> (lo mette il server a ogni stato senza corpo che non sia 404 o 405) non dice nulla: con uno
/// status 5xx e' <see cref="ServerErrorException"/>, con ogni altro status e' <see cref="UnexpectedResponseException"/>. (Il doc di
/// <see cref="FilemasterException"/> dice sia "ogni altro 5xx e' <see cref="ServerErrorException"/>" sia "lo slug <c>error</c> con altri
/// status e' <see cref="UnexpectedResponseException"/>": per <c>error</c> con un 5xx ha la precedenza la prima.)
/// </para>
/// <para>
/// <b>Senza slug utilizzabile</b> (corpo assente, non JSON, troppo grande, senza <c>type</c> nella forma <c>/problems/x</c>, o slug
/// sconosciuto) decide lo status: 400 -> <see cref="InvalidRequestException"/>, 401, 403, 404, 409 (<see cref="ConflictException"/>:
/// senza slug non si distingue <c>content-unavailable</c>), 413, 415, ogni 5xx (anche un 502, 503 o 504 di un proxy, e il 503 che non e'
/// <c>storage-not-configured</c>) -> <see cref="ServerErrorException"/>; tutto il resto (405, 416 senza corpo, 408, 429, 3xx, 4xx
/// non previsti) -> <see cref="UnexpectedResponseException"/>. Uno status sotto 400 e' sempre <see cref="UnexpectedResponseException"/>.
/// </para>
/// <para>
/// <b>Campi.</b> <see cref="FilemasterException.ProblemType"/> e' lo slug nudo (anche se sconosciuto); <see cref="FilemasterException.RequestId"/>
/// viene dal <c>request_id</c> del corpo (il server di riferimento, dal commit 541f378, ripete lo stesso valore anche nell'header <c>X-Request-ID</c>; i server piu' vecchi no) con ripiego sull'intestazione;
/// <see cref="FilemasterException.Detail"/> dal <c>detail</c> del corpo. Il messaggio e' quello di default del tipo; se il corpo ha un
/// <c>detail</c> diventa <c>"Il server ha risposto {status}: {detail}"</c> (per <see cref="UnexpectedResponseException"/> lo status c'e' sempre).
/// </para>
/// </remarks>
internal static class ProblemMapper
{
    /// <summary>L'eccezione per una risposta d'errore, dato il suo status, il suo corpo (se c'e') e l'intestazione <c>X-Request-ID</c>.</summary>
    /// <param name="statusCode">Lo status HTTP della risposta.</param>
    /// <param name="body">I byte del corpo, al piu' <see cref="ProblemBody.MaxBytes"/>; null se assente o troppo grande.</param>
    /// <param name="headerRequestId">Il valore dell'intestazione <c>X-Request-ID</c> della risposta, se c'era.</param>
    internal static FilemasterException Map(int statusCode, byte[]? body, string? headerRequestId) =>
        Map(statusCode, ProblemBody.Parse(body), headerRequestId);

    /// <summary>Come <see cref="Map(int, byte[], string)"/>, con il corpo gia' letto.</summary>
    /// <param name="statusCode">Lo status HTTP della risposta.</param>
    /// <param name="problem">Il corpo letto; null se non c'era niente di utilizzabile.</param>
    /// <param name="headerRequestId">Il valore dell'intestazione <c>X-Request-ID</c> della risposta, se c'era.</param>
    internal static FilemasterException Map(int statusCode, ProblemBody? problem, string? headerRequestId)
    {
        var slug = problem?.Slug;
        var detail = problem?.Detail;
        var requestId = problem?.RequestId ?? ProblemBody.CleanId(headerRequestId);
        var message = detail is null
            ? null
            : string.Format(CultureInfo.InvariantCulture, "Il server ha risposto {0}: {1}", statusCode, detail);

        if (statusCode < 400)
        {
            return Unexpected(statusCode, slug, requestId, detail);
        }

        if (statusCode == 415)
        {
            return new UnsupportedMediaTypeException(message, statusCode, slug, requestId, detail);
        }

        switch (slug)
        {
            case "validation-error":
                return new InvalidRequestException(message, statusCode, slug, requestId, detail);
            case "unauthorized":
                return new UnauthorizedException(message, statusCode, slug, requestId, detail);
            case "forbidden":
                return new ForbiddenException(message, statusCode, slug, requestId, detail);
            case "not-found":
                return new NotFoundException(message, statusCode, slug, requestId, detail);
            case "conflict":
                return new ConflictException(message, statusCode, slug, requestId, detail);
            case "content-unavailable":
                return new ContentUnavailableException(message, statusCode, slug, requestId, detail);
            case "request-too-large":
                return new RequestTooLargeException(message, statusCode, slug, requestId, detail);
            case "unsupported-media-type":
                return new UnsupportedMediaTypeException(message, statusCode, slug, requestId, detail);
            case "storage-not-configured":
                return new StorageNotConfiguredException(message, statusCode, slug, requestId, detail);
            case "internal-error":
                return new ServerErrorException(message, statusCode, slug, requestId, detail);
            case "method-not-allowed":
                return Unexpected(statusCode, slug, requestId, detail);
            case "error":
                return IsServerError(statusCode)
                    ? new ServerErrorException(message, statusCode, slug, requestId, detail)
                    : Unexpected(statusCode, slug, requestId, detail);
        }

        // Slug assente o sconosciuto: decide lo status.
        return statusCode switch
        {
            400 => new InvalidRequestException(message, statusCode, slug, requestId, detail),
            401 => new UnauthorizedException(message, statusCode, slug, requestId, detail),
            403 => new ForbiddenException(message, statusCode, slug, requestId, detail),
            404 => new NotFoundException(message, statusCode, slug, requestId, detail),
            409 => new ConflictException(message, statusCode, slug, requestId, detail),
            413 => new RequestTooLargeException(message, statusCode, slug, requestId, detail),
            _ when IsServerError(statusCode) => new ServerErrorException(message, statusCode, slug, requestId, detail),
            _ => Unexpected(statusCode, slug, requestId, detail),
        };
    }

    private static bool IsServerError(int statusCode) => statusCode >= 500 && statusCode <= 599;

    private static UnexpectedResponseException Unexpected(int statusCode, string? slug, string? requestId, string? detail) =>
        new(
            detail is null
                ? string.Format(CultureInfo.InvariantCulture, "Il server ha risposto {0}, che il client non sa interpretare.", statusCode)
                : string.Format(CultureInfo.InvariantCulture, "Il server ha risposto {0} (risposta non prevista): {1}", statusCode, detail),
            statusCode,
            slug,
            requestId,
            detail);
}
