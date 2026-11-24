using System;

namespace Strybok;

/// <summary>Base class of every error reported by the SDK.</summary>
public abstract class StrybokException : Exception
{
    private protected StrybokException(string message, Uri? uri, Exception? innerException)
        : base(message, innerException)
    {
        Uri = uri;
    }

    /// <summary>The link that was being handled, when known.</summary>
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

    /// <summary>HTTP status of the response; 200 when the body was malformed.</summary>
    public int StatusCode { get; }
}
