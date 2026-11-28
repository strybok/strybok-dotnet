using System;
using System.Collections.Generic;
using System.Text;

namespace Strybok;

/// <summary>
/// Finds the click id of a deferred link in a store's install referrer and in a pasted link
/// (SDK contract, "Deferred links"). Never throws, and never keeps or logs its input.
/// </summary>
internal static class DeferredLinkParsing
{
    private const string Key = "strybok_click";
    private const string EncodedKey = "strybok_click%3D";
    private const string IdPrefix = "dc_";
    private const int IdLength = 35;

    /// <summary><c>^dc_[0-9A-Za-z]{32}$</c>.</summary>
    public static bool IsClickId(string? value)
    {
        if (value is null || value.Length != IdLength || !value.StartsWith(IdPrefix, StringComparison.Ordinal))
        {
            return false;
        }
        for (var i = IdPrefix.Length; i < value.Length; i++)
        {
            var c = value[i];
            var ok = (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>
    /// The click id in an install referrer, or <c>null</c>. The first part whose key is exactly
    /// <c>strybok_click</c> decides. Only when no part has that key and the string contains
    /// <c>strybok_click%3D</c> is the whole string percent-decoded once and searched again.
    /// </summary>
    public static string? FindClickIdInReferrer(string? referrer)
    {
        if (referrer is null || referrer.Length == 0) return null;
        if (TryFindRaw(referrer, out var id)) return id;
        if (referrer.IndexOf(EncodedKey, StringComparison.OrdinalIgnoreCase) < 0) return null;

        var decoded = PercentDecode(referrer, 0, referrer.Length, plusIsSpace: false);
        if (decoded is null) return null;
        TryFindRaw(decoded, out id);
        return id;
    }

    /// <summary>
    /// The click id of a pasted link, or <c>null</c> when the text is not a link of this app
    /// with a valid first <c>strybok_click</c> query parameter.
    /// </summary>
    public static string? FindClickIdInPastedLink(string? pasted, HashSet<string> allowedHosts)
    {
        if (pasted is null) return null;
        var url = pasted.Trim();
        // The copied link is a stored link's: the slug rule, whatever ParameterLinks says.
        if (!LinkUrl.TryParse(url, allowedHosts, out _)) return null;

        var queryStart = url.IndexOf('?');
        var fragment = url.IndexOf('#');
        if (queryStart < 0 || (fragment >= 0 && fragment < queryStart)) return null;
        var end = fragment < 0 ? url.Length : fragment;

        var start = queryStart + 1;
        while (start <= end)
        {
            var partEnd = url.IndexOf('&', start, end - start);
            if (partEnd < 0) partEnd = end;
            var equals = url.IndexOf('=', start, partEnd - start);
            var keyEnd = equals < 0 ? partEnd : equals;

            if (string.Equals(PercentDecode(url, start, keyEnd, plusIsSpace: true), Key, StringComparison.Ordinal))
            {
                // The first occurrence decides, even when its value is not an id.
                var value = equals < 0 ? null : PercentDecode(url, equals + 1, partEnd, plusIsSpace: true);
                return IsClickId(value) ? value : null;
            }
            start = partEnd + 1;
        }
        return null;
    }

    /// <summary>Returns <c>true</c> when a part has the key; <paramref name="id"/> is then its value if valid.</summary>
    private static bool TryFindRaw(string text, out string? id)
    {
        id = null;
        var start = 0;
        while (start <= text.Length)
        {
            var end = text.IndexOf('&', start);
            if (end < 0) end = text.Length;
            var equals = text.IndexOf('=', start, end - start);
            var keyEnd = equals < 0 ? end : equals;
            if (keyEnd - start == Key.Length && string.CompareOrdinal(text, start, Key, 0, Key.Length) == 0)
            {
                var value = equals < 0 ? null : text.Substring(equals + 1, end - equals - 1);
                if (IsClickId(value)) id = value;
                return true;
            }
            start = end + 1;
        }
        return false;
    }

    /// <summary>Strict percent-decoding as UTF-8; <c>null</c> for a malformed escape.</summary>
    private static string? PercentDecode(string text, int start, int end, bool plusIsSpace)
    {
        var bytes = new byte[end - start];
        var count = 0;
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
                bytes[count++] = (byte)((high * 16) + low);
                i += 3;
            }
            else if (c > 0x7F)
            {
                // Not ASCII: cannot be part of the key or of an id. Keep the position occupied.
                bytes[count++] = (byte)'?';
                i += 1;
            }
            else
            {
                bytes[count++] = plusIsSpace && c == '+' ? (byte)' ' : (byte)c;
                i += 1;
            }
        }
        return Encoding.UTF8.GetString(bytes, 0, count);
    }

    private static int HexValue(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        return -1;
    }
}
