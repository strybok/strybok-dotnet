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

    /// <summary>Time allowed for one resolve attempt. Default: 5 seconds.</summary>
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
    /// Optional HTTP transport (certificate pinning, proxies, tests). The SDK wraps it in its own
    /// <see cref="HttpClient"/> and does not dispose the handler. Ignored when an
    /// <see cref="HttpClient"/> is passed to the <see cref="StrybokClient"/> constructor.
    /// </summary>
    public HttpMessageHandler? HttpMessageHandler { get; set; }

    /// <summary>Clock used for duplicate detection. Replaceable for tests.</summary>
    public Func<DateTimeOffset>? Clock { get; set; }

    /// <summary>
    /// Waits for the given time; used for the retry pause and the per-attempt timeout. Must
    /// honour the cancellation token. Replaceable for tests; defaults to <c>Task.Delay</c>.
    /// </summary>
    public Func<TimeSpan, CancellationToken, Task>? Delay { get; set; }
}
