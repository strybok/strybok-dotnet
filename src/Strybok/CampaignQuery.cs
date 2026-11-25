using System;
using System.Text;

namespace Strybok;

/// <summary>
/// Builds the campaign query of the lookup URL (SDK contract, "Campaign query").
/// </summary>
/// <remarks>
/// Written by hand on purpose, like <see cref="LinkUrl"/>: it works on the URL exactly as it was
/// received, has the same result on every target framework and never throws.
/// </remarks>
internal static class CampaignQuery
{
    private const int MaxValueLength = 64;

    private static readonly string[] Keys =
    {
        "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content",
    };

    /// <summary>
    /// Returns an empty string, or <c>?</c> followed by those of the five <c>utm_*</c> parameters
    /// of <paramref name="rawQuery"/> (a query string without <c>?</c> and without the fragment)
    /// whose first occurrence has a valid value, in a fixed order. Every other parameter is
    /// dropped: it may hold private data.
    /// </summary>
    public static string Build(string? rawQuery)
    {
        if (rawQuery is null || rawQuery.Length == 0) return string.Empty;

        var values = new string?[Keys.Length];
        var seen = new bool[Keys.Length];
        var start = 0;
        while (start <= rawQuery.Length)
        {
            var end = rawQuery.IndexOf('&', start);
            if (end < 0) end = rawQuery.Length;
            var equals = rawQuery.IndexOf('=', start, end - start);
            var keyEnd = equals < 0 ? end : equals;

            var key = DecodeUnreserved(rawQuery, start, keyEnd);
            var index = key is null ? -1 : Array.IndexOf(Keys, key);
            if (index >= 0 && !seen[index])
            {
                seen[index] = true; // The first occurrence counts, even when its value is dropped.
                var value = equals < 0 ? null : DecodeUnreserved(rawQuery, equals + 1, end);
                if (value is not null && value.Length >= 1 && value.Length <= MaxValueLength) values[index] = value;
            }
            start = end + 1;
        }

        StringBuilder? query = null;
        for (var i = 0; i < Keys.Length; i++)
        {
            var value = values[i];
            if (value is null) continue;
            if (query is null) query = new StringBuilder("?");
            else query.Append('&');
            query.Append(Keys[i]).Append('=').Append(value);
        }
        return query is null ? string.Empty : query.ToString();
    }

    /// <summary>
    /// Percent-decodes a part of <paramref name="text"/> (<c>+</c> is a space) and returns the
    /// result only when it consists of <c>A–Z a–z 0–9 . _ ~ -</c>; <c>null</c> otherwise.
    /// </summary>
    /// <remarks>
    /// Campaign keys and valid values are made of these characters only, so anything else — a
    /// space, a malformed escape, any byte of a multi-byte UTF-8 sequence (valid or not) — can only
    /// belong to a key that is not ours or to a value that is dropped. No UTF-8 decoding is needed
    /// for that.
    /// </remarks>
    private static string? DecodeUnreserved(string text, int start, int end)
    {
        var decoded = new StringBuilder(end - start);
        var i = start;
        while (i < end)
        {
            var c = text[i];
            if (c == '%')
            {
                if (i + 2 >= end) return null;
                var high = HexValue(text[i + 1]);
                var low = HexValue(text[i + 2]);
                if (high < 0 || low < 0) return null;
                c = (char)(high * 16 + low);
                i += 3;
            }
            else
            {
                i += 1;
            }
            if (!IsUnreserved(c)) return null;
            decoded.Append(c);
        }
        return decoded.ToString();
    }

    private static int HexValue(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        return -1;
    }

    private static bool IsUnreserved(char c) =>
        (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
        || c == '.' || c == '_' || c == '~' || c == '-';
}
