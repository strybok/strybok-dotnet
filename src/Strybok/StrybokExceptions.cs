using System;
using System.Collections.Generic;

namespace Strybok;

/// <summary>Base class of every error reported by the SDK.</summary>
public abstract class StrybokException : Exception
{
    private protected StrybokException(string message, Uri? uri, Exception? innerException)
        : base(message, innerException)
    {
        Uri = uri;
    }

    /// <summary>
    /// The link that was being handled, when known. Always <c>null</c> for errors of
    /// <see cref="StrybokClient.CreateLinkAsync"/>.
    /// </summary>
    public Uri? Uri { get; }
}

/// <summary>
/// The URL is not a Strybok link of this app: wrong scheme, a host outside
/// <see cref="StrybokOptions.AllowedHosts"/>, credentials, a port, or a path that is not a
/// single slug. Thrown by <see cref="StrybokClient.ResolveAsync"/> before any network call.
/// </summary>
public sealed class UnsupportedLinkException : StrybokException
{
    /// <summary>Creates the exception.</summary>
    public UnsupportedLinkException(string message, Uri? uri = null)
        : base(message, uri, null)
    {
    }
}

/// <summary>The service does not know this link, or it is disabled or expired (HTTP 404).</summary>
public sealed class LinkNotFoundException : StrybokException
{
    /// <summary>Creates the exception.</summary>
    public LinkNotFoundException(Uri? uri = null)
        : base("No active link matches this URL", uri, null)
    {
    }
}

/// <summary>
/// The request could not be completed (offline, DNS, connection reset) after all retries.
/// The transport error is available as <see cref="Exception.InnerException"/>.
/// </summary>
public sealed class StrybokNetworkException : StrybokException
{
    /// <summary>Creates the exception.</summary>
    public StrybokNetworkException(string message, Uri? uri = null, Exception? innerException = null)
        : base(message, uri, innerException)
    {
    }
}

/// <summary>The service did not answer in time, after all retries.</summary>
public sealed class StrybokTimeoutException : StrybokException
{
    /// <summary>Creates the exception.</summary>
    public StrybokTimeoutException(Uri? uri = null)
        : base("The resolve request timed out", uri, null)
    {
    }

    internal StrybokTimeoutException(string message, Exception? innerException)
        : base(message, null, innerException)
    {
    }
}

/// <summary>The service answered with an unexpected status code or body.</summary>
public sealed class StrybokServerException : StrybokException
{
    /// <summary>Creates the exception.</summary>
    public StrybokServerException(string message, int statusCode, Uri? uri = null)
        : base(message, uri, null)
    {
        StatusCode = statusCode;
    }

    /// <summary>HTTP status of the response; 200 (or 201) when the body was malformed.</summary>
    public int StatusCode { get; }
}

/// <summary>
/// <see cref="StrybokClient.CreateLinkAsync"/> only. No <see cref="StrybokOptions.ClientKey"/> is
/// configured (no network call was made), or the service refused it (HTTP 401): the key is wrong
/// or was replaced, or creating links from the app is switched off in the dashboard. The service
/// does not say which. Retrying does not help.
/// </summary>
public sealed class StrybokUnauthorizedException : StrybokException
{
    /// <summary>Creates the exception.</summary>
    public StrybokUnauthorizedException(string message)
        : base(message, null, null)
    {
    }
}

/// <summary>
/// <see cref="StrybokClient.CreateLinkAsync"/> only. The route is not covered by the route
/// patterns listed for the application in the dashboard (HTTP 403). Retrying does not help.
/// </summary>
public sealed class RouteNotAllowedException : StrybokException
{
    /// <summary>Creates the exception.</summary>
    public RouteNotAllowedException(string routePath)
        : base("The route is not covered by the application's route patterns", null, null)
    {
        RoutePath = routePath;
    }

    /// <summary>The route that was refused.</summary>
    public string RoutePath { get; }
}

/// <summary>
/// <see cref="StrybokClient.CreateLinkAsync"/> only. Too many requests from this device's address
/// or for this app, or the app's daily limit of new links is reached (HTTP 429). Temporary: let
/// the user try again later. The SDK never retries this by itself.
/// </summary>
public sealed class StrybokRateLimitedException : StrybokException
{
    /// <summary>Creates the exception.</summary>
    public StrybokRateLimitedException(TimeSpan? retryAfter = null)
        : base("Too many requests; try again later", null, null)
    {
        RetryAfter = retryAfter;
    }

    /// <summary>
    /// How long to wait, from the <c>Retry-After</c> header; <c>null</c> when the service did not say.
    /// </summary>
    public TimeSpan? RetryAfter { get; }
}

/// <summary>One problem the service found in a create-link request.</summary>
public sealed class StrybokValidationIssue
{
    /// <summary>Creates the issue.</summary>
    public StrybokValidationIssue(string path, string message)
    {
        Path = path ?? string.Empty;
        Message = message ?? string.Empty;
    }

    /// <summary>Which field, e.g. <c>title</c> or <c>payload.a</c>; may be empty.</summary>
    public string Path { get; }

    /// <summary>What is wrong with it.</summary>
    public string Message { get; }

    /// <inheritdoc />
    public override string ToString() => Path.Length == 0 ? Message : Path + ": " + Message;
}

/// <summary>
/// <see cref="StrybokClient.CreateLinkAsync"/> only. The request was rejected before it was sent
/// (bad route path, or a payload that is not a JSON object), or by the service (HTTP 400, 413 or
/// 422). Retrying does not help.
/// </summary>
public sealed class StrybokValidationException : StrybokException
{
    /// <summary>Creates the exception.</summary>
    public StrybokValidationException(
        string message,
        int? statusCode = null,
        IReadOnlyList<StrybokValidationIssue>? issues = null)
        : base(message, null, null)
    {
        StatusCode = statusCode;
        Issues = issues ?? Array.Empty<StrybokValidationIssue>();
    }

    /// <summary>HTTP status of the response; <c>null</c> when the request was rejected before sending.</summary>
    public int? StatusCode { get; }

    /// <summary>The service's <c>error.details.issues</c>; empty when it sent none.</summary>
    public IReadOnlyList<StrybokValidationIssue> Issues { get; }
}
