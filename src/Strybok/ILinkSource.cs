using System;
using System.Threading;
using System.Threading.Tasks;

namespace Strybok;

/// <summary>
/// Where incoming link URLs come from: the URL that launched the app plus the URLs received while
/// it is running. Replaceable so the SDK can be used on any platform and tested without a device.
/// </summary>
/// <remarks>
/// Build the <see cref="Uri"/> from the exact string the operating system delivered. The SDK
/// validates <see cref="Uri.OriginalString"/>, not the normalised form.
/// </remarks>
public interface ILinkSource
{
    /// <summary>The link that launched the app (cold start), or <c>null</c>.</summary>
    Task<Uri?> GetInitialLinkAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Subscribes to links received while the app is running. Implementations may also replay the
    /// launch link here; the SDK de-duplicates it. Dispose the result to unsubscribe.
    /// </summary>
    IDisposable Subscribe(Action<Uri> onLink);
}
