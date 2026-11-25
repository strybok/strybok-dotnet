using System;

namespace Strybok;

/// <summary>
/// Campaign values of an opened link — the usual <c>utm_source</c>, <c>utm_medium</c>,
/// <c>utm_campaign</c>, <c>utm_term</c> and <c>utm_content</c> of web analytics. Each one is
/// <c>null</c> when it has no value.
/// </summary>
/// <remarks>
/// They are the defaults the owner set on the link, overridden by the <c>utm_*</c> parameters of
/// the URL that was tapped. Whoever shares a link can write any value into its URL, so treat them
/// as labels (for your analytics, say), never as something to trust or to act on.
/// </remarks>
public sealed class LinkCampaign : IEquatable<LinkCampaign>
{
    /// <summary>No campaign values.</summary>
    public static LinkCampaign Empty { get; } = new LinkCampaign();

    /// <summary>Creates campaign values. Useful for tests of your own code.</summary>
    public LinkCampaign(
        string? source = null,
        string? medium = null,
        string? campaign = null,
        string? term = null,
        string? content = null)
    {
        Source = source;
        Medium = medium;
        Campaign = campaign;
        Term = term;
        Content = content;
    }

    /// <summary><c>utm_source</c>, for example <c>instagram</c>; <c>null</c> when not set.</summary>
    public string? Source { get; }

    /// <summary><c>utm_medium</c>, for example <c>social</c>; <c>null</c> when not set.</summary>
    public string? Medium { get; }

    /// <summary><c>utm_campaign</c>, for example <c>spring_sale</c>; <c>null</c> when not set.</summary>
    public string? Campaign { get; }

    /// <summary><c>utm_term</c>; <c>null</c> when not set.</summary>
    public string? Term { get; }

    /// <summary><c>utm_content</c>; <c>null</c> when not set.</summary>
    public string? Content { get; }

    /// <summary><c>true</c> when the link has no campaign value at all.</summary>
    public bool IsEmpty => Source is null && Medium is null && Campaign is null && Term is null && Content is null;

    /// <inheritdoc />
    public bool Equals(LinkCampaign? other) =>
        other is not null
        && string.Equals(Source, other.Source, StringComparison.Ordinal)
        && string.Equals(Medium, other.Medium, StringComparison.Ordinal)
        && string.Equals(Campaign, other.Campaign, StringComparison.Ordinal)
        && string.Equals(Term, other.Term, StringComparison.Ordinal)
        && string.Equals(Content, other.Content, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as LinkCampaign);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        unchecked
        {
            var hash = 17;
            hash = hash * 31 + (Source is null ? 0 : StringComparer.Ordinal.GetHashCode(Source));
            hash = hash * 31 + (Medium is null ? 0 : StringComparer.Ordinal.GetHashCode(Medium));
            hash = hash * 31 + (Campaign is null ? 0 : StringComparer.Ordinal.GetHashCode(Campaign));
            hash = hash * 31 + (Term is null ? 0 : StringComparer.Ordinal.GetHashCode(Term));
            hash = hash * 31 + (Content is null ? 0 : StringComparer.Ordinal.GetHashCode(Content));
            return hash;
        }
    }

    /// <inheritdoc />
    public override string ToString() =>
        "LinkCampaign(source: " + Source + ", medium: " + Medium + ", campaign: " + Campaign
        + ", term: " + Term + ", content: " + Content + ")";
}
