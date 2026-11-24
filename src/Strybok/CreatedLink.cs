using System;

namespace Strybok;

/// <summary>
/// A short link made by <see cref="StrybokClient.CreateLinkAsync"/>: hand <see cref="Url"/> to the
/// share sheet.
/// </summary>
public sealed class CreatedLink
{
    /// <summary>Creates a created link. Useful for tests of your own sharing code.</summary>
    public CreatedLink(Uri url, string slug, string routePath, string linkId)
    {
        Url = url ?? throw new ArgumentNullException(nameof(url));
        Slug = slug ?? throw new ArgumentNullException(nameof(slug));
        RoutePath = routePath ?? throw new ArgumentNullException(nameof(routePath));
        LinkId = linkId ?? throw new ArgumentNullException(nameof(linkId));
    }

    /// <summary>
    /// What the user shares, e.g. <c>https://my-app.strybok.link/Ab3xK9</c>. Always <c>https</c>.
    /// <see cref="Uri.OriginalString"/> is the text exactly as the service returned it.
    /// </summary>
    public Uri Url { get; }

    /// <summary>The generated last part of <see cref="Url"/>.</summary>
    public string Slug { get; }

    /// <summary>The in-app route the link opens, as stored by the service.</summary>
    public string RoutePath { get; }

    /// <summary>Identifier of the link.</summary>
    public string LinkId { get; }

    /// <inheritdoc />
    public override string ToString() => "CreatedLink(" + Url.OriginalString + ", linkId: " + LinkId + ")";
}
