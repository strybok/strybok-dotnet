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
    /// with an optional query and fragment, which do not decide anything here.
    /// <paramref name="lookup"/> is then <c>https://{lower-cased host}/{slug}</c> followed by the
    /// campaign query (<see cref="CampaignQuery"/>): no port, nothing else of the query, no
    /// fragment — they may hold private data. This is the rule without
    /// <see cref="StrybokOptions.ParameterLinks"/>, and the rule for deferred links whatever the
    /// option says: the server offers them only for stored links.
    /// </summary>
    public static bool TryParse(string? url, HashSet<string> allowedHosts, out string lookup) =>
        TryParse(url, allowedHosts, null, out lookup);

    /// <summary>
    /// <see cref="TryParse(string, HashSet{string}, out string)"/> with the
    /// <see cref="StrybokOptions.ParameterLinks"/> option: with <paramref name="payloadKeys"/>, a
    /// route path or a Firebase long link is ours too, and <paramref name="lookup"/> is what
    /// <see cref="ParameterLinkRules"/> builds. <c>null</c> keys: the option is not set.
    /// </summary>
    public static bool TryParse(
        string? url,
        HashSet<string> allowedHosts,
        IReadOnlyList<string>? payloadKeys,
        out string lookup)
    {
        lookup = string.Empty;
        if (url is null || !url.StartsWith(SchemePrefix, StringComparison.OrdinalIgnoreCase)) return false;

        var authorityStart = SchemePrefix.Length;
        var pathStart = url.IndexOfAny(AuthorityEnd, authorityStart);
        if (payloadKeys is null)
        {
            // The path is required and must directly follow the authority.
            if (pathStart < 0 || url[pathStart] != '/') return false;
        }
        else if (pathStart < 0)
        {
            // Nothing after the host: an empty path, which the option reads as "/".
            pathStart = url.Length;
        }

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

        var pathEnd = pathStart < url.Length ? url.IndexOfAny(PathEnd, pathStart) : -1;
        if (pathEnd < 0) pathEnd = url.Length;

        var rawQuery = string.Empty;
        if (pathEnd < url.Length && url[pathEnd] == '?')
        {
            var queryEnd = url.IndexOf('#', pathEnd);
            if (queryEnd < 0) queryEnd = url.Length;
            rawQuery = url.Substring(pathEnd + 1, queryEnd - pathEnd - 1);
        }

        var path = url.Substring(pathStart, pathEnd - pathStart);
        if (payloadKeys is not null) return ParameterLinkRules.TryBuildLookup(host, path, rawQuery, payloadKeys, out lookup);

        var slugLength = path.Length - 1;
        if (slugLength < 1 || slugLength > MaxSlugLength) return false;
        for (var i = 1; i < path.Length; i++)
        {
            if (!IsSlugChar(path[i])) return false;
        }

        lookup = SchemePrefix + host + path + CampaignQuery.Build(rawQuery);
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
