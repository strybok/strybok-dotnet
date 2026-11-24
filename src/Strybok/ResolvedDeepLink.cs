using System;
using System.Text.Json;

namespace Strybok;

/// <summary>
/// A link resolved by the Strybok API.
/// </summary>
/// <remarks>
/// <see cref="RoutePath"/> and <see cref="Payload"/> are plain data chosen by whoever created the
/// link. Treat them like any other untrusted input: map them onto routes your app already knows,
/// never execute them.
/// </remarks>
public sealed class ResolvedDeepLink
{
    /// <summary>Creates a resolved link. Useful for tests of your own navigation code.</summary>
    public ResolvedDeepLink(Uri uri, string linkId, string appId, string routePath, JsonElement payload)
    {
        Uri = uri ?? throw new ArgumentNullException(nameof(uri));
        LinkId = linkId ?? throw new ArgumentNullException(nameof(linkId));
        AppId = appId ?? throw new ArgumentNullException(nameof(appId));
        RoutePath = routePath ?? throw new ArgumentNullException(nameof(routePath));
        Payload = payload;
    }

    /// <summary>The full URL that opened the app, as received (including query and fragment).</summary>
    public Uri Uri { get; }

    /// <summary>Identifier of the link.</summary>
    public string LinkId { get; }

    /// <summary>Identifier of the Strybok application the link belongs to.</summary>
    public string AppId { get; }

    /// <summary>In-app route, always starting with a single <c>/</c> (for example <c>/invite/abc123</c>).</summary>
    public string RoutePath { get; }

    /// <summary>
    /// JSON object attached to the link. Always of kind <see cref="JsonValueKind.Object"/>; empty
    /// when the link has no payload.
    /// </summary>
    public JsonElement Payload { get; }

    /// <summary>
    /// Reads a string property of <see cref="Payload"/>. Returns <c>null</c> when the property is
    /// missing or is not a JSON string.
    /// </summary>
    public string? GetPayloadString(string name)
    {
        if (name is null) throw new ArgumentNullException(nameof(name));
        return Payload.ValueKind == JsonValueKind.Object
            && Payload.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    /// <inheritdoc />
    public override string ToString() => "ResolvedDeepLink(" + RoutePath + ", linkId: " + LinkId + ")";
}
