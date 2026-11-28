using System;
using System.Collections.Generic;

namespace Strybok;

/// <summary>
/// The <see cref="StrybokOptions.ParameterLinks"/> option: links without an API call (path links
/// and Firebase long links). Copy it from <c>parameterLinks</c> of the integration guide
/// (<c>GET /v1/apps/:appId/integration</c>, shown on the dashboard's setup screen).
/// </summary>
public sealed class ParameterLinksOptions
{
    /// <summary>No payload keys yet; add them to <see cref="PayloadKeys"/>.</summary>
    public ParameterLinksOptions()
    {
    }

    /// <summary>With these payload keys, in this order.</summary>
    public ParameterLinksOptions(params string[] payloadKeys)
    {
        if (payloadKeys is null) throw new ArgumentNullException(nameof(payloadKeys));
        PayloadKeys = new List<string>(payloadKeys);
    }

    /// <summary>
    /// The query keys whose values are sent with a path link and become its payload, in the
    /// owner's order (<c>parameterLinks.payloadKeys</c> of the integration guide).
    /// </summary>
    /// <remarks>
    /// <para>
    /// At most 20 distinct keys of 1–40 characters of <c>A–Z a–z 0–9 _ . -</c>; not <c>utm_…</c>
    /// (in any case), <c>st</c>, <c>sd</c>, <c>si</c> or <c>strybok_click</c>. An empty list is
    /// valid: path links then carry no payload. Anything else is rejected with an
    /// <see cref="ArgumentException"/> when the client is created.
    /// </para>
    /// <para>
    /// The keys are compiled into the app: a key the owner adds in the dashboard reaches users
    /// with the next release. Never configure a key that carries personal data.
    /// </para>
    /// </remarks>
    public IList<string> PayloadKeys { get; set; } = new List<string>();
}
