using System;
using System.Text.Json;

namespace Strybok;

/// <summary>
/// A link resolved by the Strybok API.
/// </summary>
/// <remarks>
/// <see cref="RoutePath"/>, <see cref="Payload"/> and <see cref="Campaign"/> are plain data chosen
/// by whoever created or shared the link. Treat them like any other untrusted input: map them onto
/// routes your app already knows, never execute them, and use campaign values as labels only.
/// </remarks>
public sealed class ResolvedDeepLink
{
    /// <summary>Creates a resolved link. Useful for tests of your own navigation code.</summary>
    public ResolvedDeepLink(Uri uri, string linkId, string appId, string routePath, JsonElement payload)
        : this(uri, linkId, appId, routePath, payload, null)
    {
    }

    /// <summary>Creates a resolved link with campaign values. Useful for tests of your own code.</summary>
    public ResolvedDeepLink(
        Uri uri,
        string linkId,
        string appId,
        string routePath,
        JsonElement payload,
        LinkCampaign? campaign)
        : this(uri, linkId, appId, routePath, payload, campaign, false, false)
    {
    }

    /// <summary>
    /// Creates a resolved link with the deferred-link flags. Useful for tests of your own code.
    /// </summary>
    public ResolvedDeepLink(
        Uri uri,
        string linkId,
        string appId,
        string routePath,
        JsonElement payload,
        LinkCampaign? campaign,
        bool isDeferred,
        bool isProbabilisticMatch)
    {
        Uri = uri ?? throw new ArgumentNullException(nameof(uri));
        LinkId = linkId ?? throw new ArgumentNullException(nameof(linkId));
        AppId = appId ?? throw new ArgumentNullException(nameof(appId));
        RoutePath = routePath ?? throw new ArgumentNullException(nameof(routePath));
        Payload = payload;
        Campaign = campaign ?? LinkCampaign.Empty;
        IsDeferred = isDeferred;
        IsProbabilisticMatch = isProbabilisticMatch;
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
    /// Campaign values of this open: the link's defaults, overridden by the <c>utm_*</c> parameters
    /// of the tapped URL. Never <c>null</c>; <see cref="LinkCampaign.IsEmpty"/> when there are none.
    /// </summary>
    public LinkCampaign Campaign { get; }

    /// <summary>
    /// <c>true</c> when the link was restored after an installation (a deferred link: the store's
    /// install referrer on Android, a pasted link or a probabilistic match on iOS) instead of
    /// opening the app directly. <c>false</c> for every link that arrived the normal way.
    /// </summary>
    public bool IsDeferred { get; }

    /// <summary>
    /// <c>true</c> only for a link the server guessed from the network address (iOS probabilistic
    /// matching, <see cref="StrybokOptions.AllowProbabilisticMatching"/>). <b>Check it</b>: such a
    /// link may belong to another person on the same network, so it must never by itself sign
    /// anyone in, accept an invitation, apply a personal discount or show personal content.
    /// </summary>
    public bool IsProbabilisticMatch { get; }

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
