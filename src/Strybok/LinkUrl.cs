using System;
using System.Collections.Generic;

namespace Strybok;

/// <summary>
/// Strict recogniser for Strybok link URLs.
/// </summary>
/// <remarks>
/// Works on the URL string exactly as it was received. <see cref="System.Uri"/> is deliberately
/// not used: it normalises its input (collapses <c>/a/../slug</c>, decodes some percent-escapes,
/// treats <c>\</c> as <c>/</c>, lower-cases the host), so a URL the operating system would never
/// have routed to a single slug could look like one after parsing.
/// </remarks>
internal static class LinkUrl
{
    private const string SchemePrefix = "https://";
    private const int MaxSlugLength = 64;
    private const int MaxHostLength = 253;

    private static readonly char[] AuthorityEnd = { '/', '?', '#' };
    private static readonly char[] PathEnd = { '?', '#' };

    /// <summary>
    /// Returns <c>true</c> when <paramref name="url"/> is <c>https://{allowed host}[:443]/{slug}</c>
    /// with an optional query and fragment. <paramref name="lookup"/> is then
    /// <c>https://{lower-cased host}/{slug}</c>: no port, no query, no fragment.
    /// </summary>
    public static bool TryParse(string? url, HashSet<string> allowedHosts, out string lookup)
    {
        lookup = string.Empty;
        if (url is null || !url.StartsWith(SchemePrefix, StringComparison.OrdinalIgnoreCase)) return false;

        var authorityStart = SchemePrefix.Length;
        var pathStart = url.IndexOfAny(AuthorityEnd, authorityStart);
        // The path is required and must directly follow the authority.
        if (pathStart < 0 || url[pathStart] != '/') return false;

        var host = url.Substring(authorityStart, pathStart - authorityStart);
        var colon = host.IndexOf(':');
        if (colon >= 0)
        {
            // Only the default port, written exactly as "443".
            if (!string.Equals(host.Substring(colon), ":443", StringComparison.Ordinal)) return false;
            host = host.Substring(0, colon);
        }

        if (host.Length == 0 || host.Length > MaxHostLength) return false;
        foreach (var c in host)
        {
            // Rejects user info ('@'), back-slashes, percent-escapes, IPv6 literals, white space
            // and every non-ASCII character (some of which lower-case to ASCII letters).
            if (!IsHostChar(c)) return false;
        }

        host = host.ToLowerInvariant();
        if (!allowedHosts.Contains(host)) return false;

        var pathEnd = url.IndexOfAny(PathEnd, pathStart);
        if (pathEnd < 0) pathEnd = url.Length;

        var slugLength = pathEnd - pathStart - 1;
        if (slugLength < 1 || slugLength > MaxSlugLength) return false;
        for (var i = pathStart + 1; i < pathEnd; i++)
        {
            if (!IsSlugChar(url[i])) return false;
        }

        lookup = SchemePrefix + host + url.Substring(pathStart, pathEnd - pathStart);
        return true;
    }

    /// <summary>Lower-cases and validates a configured hostname; <c>null</c> when it is not one.</summary>
    public static string? NormalizeHost(string? host)
    {
        if (host is null) return null;
        host = host.Trim();
        if (host.Length == 0 || host.Length > MaxHostLength) return null;
        foreach (var c in host)
        {
            if (!IsHostChar(c)) return null;
        }
        return host.ToLowerInvariant();
    }

    private static bool IsAsciiLetterOrDigit(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');

    private static bool IsHostChar(char c) => IsAsciiLetterOrDigit(c) || c == '-' || c == '.';

    private static bool IsSlugChar(char c) => IsAsciiLetterOrDigit(c) || c == '-' || c == '_';
}
