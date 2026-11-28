using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Strybok;

/// <summary>Platform reported with <c>app_open</c> events.</summary>
public enum StrybokPlatform
{
    /// <summary>Reported as <c>unknown</c>.</summary>
    Unknown = 0,

    /// <summary>Reported as <c>ios</c>.</summary>
    iOS = 1,

    /// <summary>Reported as <c>android</c>.</summary>
    Android = 2,
}

/// <summary>
/// Configuration of <see cref="StrybokClient"/>. The values are copied when the client is
/// created; later changes have no effect.
/// </summary>
public sealed class StrybokOptions
{
    /// <summary>
    /// Required. Base URL of the Strybok API, e.g. <c>https://api.strybok.com</c>. A path prefix
    /// is preserved.
    /// </summary>
    public Uri? ApiBaseUrl { get; set; }

    /// <summary>
    /// Required. Link hostnames of this app, e.g. <c>my-app.strybok.link</c>. Compared
    /// lower-case; links on any other host are ignored.
    /// </summary>
    public ICollection<string> AllowedHosts { get; set; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Time allowed for one resolve or create-link attempt. Default: 5 seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Extra attempts after a timeout, network error or 5xx. Resolving is an idempotent GET, so
    /// retrying is safe. 404 and other 4xx are never retried. Default: 2.
    /// </summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>Pause before the first retry; doubles for each further retry. Default: 400 ms.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// The identical URL received again within this window is treated as a duplicate delivery of
    /// the same tap and dropped. Default: 2 seconds.
    /// </summary>
    public TimeSpan DedupeWindow { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Send a best-effort <c>app_open</c> event after a link was resolved. Default: on.</summary>
    public bool ReportAppOpens { get; set; } = true;

    /// <summary>Platform reported with <c>app_open</c> events. Default: unknown.</summary>
    public StrybokPlatform Platform { get; set; } = StrybokPlatform.Unknown;

    /// <summary>
    /// Receives errors for links that arrived from the operating system but could not be
    /// resolved. Links that are simply not ours are ignored without an error. May be called on
    /// any thread.
    /// </summary>
    public Action<StrybokException>? OnError { get; set; }

    /// <summary>
    /// Publishable client key of the application (<c>pk_…</c>) from the dashboard ("Links from the
    /// app"). Only needed for <see cref="StrybokClient.CreateLinkAsync"/>. It is safe to ship
    /// inside the app: it can only create links for the route patterns listed in the dashboard.
    /// Never put a secret API key (<c>lb_…</c>) here.
    /// </summary>
    public string? ClientKey { get; set; }

    /// <summary>
    /// Links without an API call: deliver path links (<c>https://my-app.strybok.link/product/42?id=7</c>)
    /// and Firebase long links (<c>https://my-app.strybok.link/?link=…</c>) and send the values of
    /// these payload keys with them. Default: <c>null</c>, the option is not set, and only links
    /// with a single slug path are ours, exactly as in 0.3.0.
    /// </summary>
    /// <remarks>
    /// Set it to <c>parameterLinks</c> of the integration guide when the owner switched path links
    /// or Firebase long links on. The server decides which routes, keys and web domains are
    /// allowed, and the delivered link carries the route and payload of its answer, never the
    /// tapped path. With the option any address of the link hosts that passes the route rules is
    /// resolved, so <see cref="OnError"/> gets <see cref="LinkNotFoundException"/> for one that is
    /// no link. Deferred links keep the slug rule.
    /// </remarks>
    public ParameterLinksOptions? ParameterLinks { get; set; }

    /// <summary>
    /// Optional HTTP transport (certificate pinning, proxies, tests). The SDK wraps it in its own
    /// <see cref="HttpClient"/> and does not dispose the handler. Ignored when an
    /// <see cref="HttpClient"/> is passed to the <see cref="StrybokClient"/> constructor.
    /// </summary>
    public HttpMessageHandler? HttpMessageHandler { get; set; }

    /// <summary>
    /// Restore a link that was tapped before the app was installed. Default: off. Can also be
    /// switched on later with <see cref="StrybokClient.EnableDeferredLinks"/>, for example after
    /// a consent screen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Needs <see cref="DeferredLinkStore"/> and, on Android, <see cref="AndroidPackageName"/>
    /// and <see cref="InstallReferrerSources"/>; on iOS, <see cref="IosBundleId"/>. Without the
    /// store or the identifier nothing happens and the status stays
    /// <see cref="DeferredLinkStatus.Disabled"/>. The owner must also have switched the feature
    /// on for the application in the dashboard.
    /// </para>
    /// <para>
    /// Android: on the first launch the SDK asks the sources for the store's install referrer
    /// and exchanges the click id found in it for the link. iOS: only
    /// <see cref="StrybokClient.RedeemPastedLinkAsync"/> and, with
    /// <see cref="AllowProbabilisticMatching"/>, matching. The link arrives through
    /// <see cref="StrybokClient.LinkReceived"/> with <see cref="ResolvedDeepLink.IsDeferred"/>.
    /// </para>
    /// </remarks>
    public bool DeferredLinks { get; set; }

    /// <summary>
    /// iOS only, and only together with <see cref="DeferredLinks"/>: let the server guess the
    /// link from the device's network address, device family and language. Default: off.
    /// </summary>
    /// <remarks>
    /// <b>This is fingerprinting in Apple's terms and is forbidden by the App Store Review
    /// Guidelines and the Apple Developer Program License Agreement, with or without the user's
    /// consent.</b> The App Review and legal risk is yours. A match is a guess: on a shared
    /// network it can be another person's link, so check
    /// <see cref="ResolvedDeepLink.IsProbabilisticMatch"/>. Needs <see cref="DeviceFamily"/> and
    /// <see cref="Locale"/>, the owner's setting in the dashboard, and an opt-in on each link.
    /// </remarks>
    public bool AllowProbabilisticMatching { get; set; }

    /// <summary>
    /// Android: the referrer sources, asked in order until one returns a referrer with a click
    /// id of ours. The SDK ships none: each is a function of your app around the store's install
    /// referrer API. Empty means the check ends at once with
    /// <see cref="DeferredLinkStatus.Unavailable"/>.
    /// </summary>
    public IList<InstallReferrerSource> InstallReferrerSources { get; set; } = new List<InstallReferrerSource>();

    /// <summary>Android: the app's own application ID. Required for deferred links on Android.</summary>
    public string? AndroidPackageName { get; set; }

    /// <summary>iOS: the app's own bundle identifier. Required for deferred links on iOS.</summary>
    public string? IosBundleId { get; set; }

    /// <summary>
    /// iOS probabilistic matching: returns <c>"iphone"</c>, <c>"ipad"</c> or <c>"ipod"</c>.
    /// Anything else (or no callback) ends matching with <see cref="DeferredLinkStatus.Unavailable"/>.
    /// </summary>
    public Func<string?>? DeviceFamily { get; set; }

    /// <summary>
    /// iOS probabilistic matching: returns the device's first preferred language as a BCP 47 tag,
    /// e.g. <c>en-GB</c>. <c>null</c> (or no callback) ends matching with
    /// <see cref="DeferredLinkStatus.Unavailable"/>.
    /// </summary>
    public Func<string?>? Locale { get; set; }

    /// <summary>
    /// iOS probabilistic matching, optional: answers whether the pasteboard probably holds a web
    /// URL, <b>without reading it</b> (<c>UIPasteboard.general.detectPatterns</c>). When it
    /// answers <c>true</c>, matching waits for <see cref="StrybokClient.RedeemPastedLinkAsync"/>
    /// or <see cref="StrybokClient.SkipPastedLink"/>. The SDK never touches the pasteboard.
    /// </summary>
    public Func<CancellationToken, Task<bool>>? PasteboardMayContainLink { get; set; }

    /// <summary>
    /// Where the check keeps its attempt counter and its "done" flag. Required for deferred
    /// links; <c>Strybok.Maui</c> supplies one on top of <c>Preferences</c>.
    /// </summary>
    public IDeferredLinkStore? DeferredLinkStore { get; set; }

    /// <summary>Clock used for duplicate detection. Replaceable for tests.</summary>
    public Func<DateTimeOffset>? Clock { get; set; }

    /// <summary>
    /// Waits for the given time; used for the retry pause and the per-attempt timeout. Must
    /// honour the cancellation token. Replaceable for tests; defaults to <c>Task.Delay</c>.
    /// </summary>
    public Func<TimeSpan, CancellationToken, Task>? Delay { get; set; }
}
