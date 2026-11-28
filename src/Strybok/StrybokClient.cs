using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Strybok;

/// <summary>
/// Entry point of the SDK.
/// </summary>
/// <example>
/// <code>
/// var strybok = new StrybokClient(
///     new StrybokOptions
///     {
///         ApiBaseUrl = new Uri("https://api.strybok.com"),
///         AllowedHosts = { "my-app.strybok.link" },
///     },
///     linkSource);
/// strybok.LinkReceived += (_, link) => Navigate(link.RoutePath);
/// await strybok.InitializeAsync();
///
/// // A "Share" button (needs StrybokOptions.ClientKey):
/// CreatedLink link = await strybok.CreateLinkAsync("/product/42", title: "Red shoes");
/// </code>
/// </example>
public sealed partial class StrybokClient : IAsyncDisposable, IDisposable
{
    /// <summary>Version reported with analytics events.</summary>
    public const string SdkVersion = "0.4.0";

    private const string ResolvePath = "/v1/public/resolve";
    private const string EventsPath = "/v1/public/events";
    private const string LinksPath = "/v1/public/links";
    private const string ClientKeyHeader = "X-Strybok-Key";
    private const int MaxRoutePathLength = 512;

    private static readonly Func<int, bool> ReadResolveBody = static status => status == 200;

    private static readonly Func<int, bool> ReadCreateLinkBody =
        static status => status == 200 || status == 201 || status == 400 || status == 413 || status == 422;

    /// <summary>Keeps the doubling retry pause from overflowing with a large <c>MaxRetries</c>.</summary>
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);

    private static readonly JsonElement EmptyPayload = CreateEmptyPayload();

    private readonly object _gate = new object();
    private readonly ILinkSource _source;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly string _apiBase;
    private readonly string? _clientKey;
    private readonly HashSet<string> _allowedHosts;

    /// <summary>The keys of <see cref="StrybokOptions.ParameterLinks"/>; <c>null</c> without the option.</summary>
    private readonly string[]? _payloadKeys;
    private readonly TimeSpan _timeout;
    private readonly int _maxRetries;
    private readonly TimeSpan _retryDelay;
    private readonly TimeSpan _dedupeWindow;
    private readonly bool _reportAppOpens;
    private readonly string _platform;
    private readonly Action<StrybokException>? _onError;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly CancellationTokenSource _disposeCts = new CancellationTokenSource();
    private readonly CancellationToken _disposeToken;

    // Deferred links; the flow is in StrybokClient.Deferred.cs.
    private readonly IDeferredLinkStore? _deferredStore;
    private readonly InstallReferrerSource?[] _referrerSources;

    /// <summary>The package name on Android, the bundle id on iOS; <c>null</c> when not given.</summary>
    private readonly string? _deferredIdentifier;

    /// <summary>A store, an identifier and a platform with a flow: what the option needs to do anything.</summary>
    private readonly bool _deferredPossible;
    private readonly bool _allowMatching;
    private readonly Func<string?>? _deviceFamily;
    private readonly Func<string?>? _locale;
    private readonly Func<CancellationToken, Task<bool>>? _pasteboardMayContainLink;

    // Everything below is guarded by _gate.
    private EventHandler<ResolvedDeepLink>? _handlers;

    /// <summary>Links resolved while nobody was listening (typically the cold-start link).</summary>
    private readonly List<ResolvedDeepLink> _pending = new List<ResolvedDeepLink>();
    private ResolvedDeepLink? _initialLink;
    private Task? _initialization;
    private IDisposable? _subscription;
    private string? _lastUrl;
    private DateTimeOffset _lastUrlAt;
    private bool _disposed;

    /// <summary>Creates the client. Nothing happens until <see cref="InitializeAsync"/> is called.</summary>
    /// <param name="options">Configuration; copied, later changes have no effect.</param>
    /// <param name="linkSource">Where link URLs come from, e.g. a <see cref="ManualLinkSource"/>.</param>
    /// <param name="httpClient">
    /// Optional client to send requests with. It is not disposed by the SDK. When omitted the SDK
    /// creates its own, on top of <see cref="StrybokOptions.HttpMessageHandler"/> when set.
    /// </param>
    /// <exception cref="ArgumentException">An option is missing or out of range.</exception>
    public StrybokClient(StrybokOptions options, ILinkSource linkSource, HttpClient? httpClient = null)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        _source = linkSource ?? throw new ArgumentNullException(nameof(linkSource));

        var api = options.ApiBaseUrl;
        if (api is null) throw new ArgumentException("ApiBaseUrl is required.", nameof(options));
        if (!api.IsAbsoluteUri || (api.Scheme != Uri.UriSchemeHttps && api.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("ApiBaseUrl must be an absolute http(s) URL.", nameof(options));
        }
        if (!string.IsNullOrEmpty(api.Query) || !string.IsNullOrEmpty(api.Fragment))
        {
            throw new ArgumentException("ApiBaseUrl must not contain a query or fragment.", nameof(options));
        }
        _apiBase = api.GetLeftPart(UriPartial.Path).TrimEnd('/');
        _clientKey = NormalizeClientKey(options.ClientKey);

        _allowedHosts = new HashSet<string>(StringComparer.Ordinal);
        if (options.AllowedHosts is not null)
        {
            foreach (var configured in options.AllowedHosts)
            {
                var host = LinkUrl.NormalizeHost(configured);
                if (host is null)
                {
                    throw new ArgumentException(
                        "AllowedHosts must contain plain hostnames such as \"my-app.strybok.link\".",
                        nameof(options));
                }
                _allowedHosts.Add(host);
            }
        }
        if (_allowedHosts.Count == 0)
        {
            throw new ArgumentException("AllowedHosts must contain at least one hostname.", nameof(options));
        }
        if (options.ParameterLinks is not null)
        {
            var problem = ParameterLinkRules.Validate(options.ParameterLinks.PayloadKeys);
            if (problem is not null) throw new ArgumentException("ParameterLinks: " + problem + ".", nameof(options));
            _payloadKeys = new List<string>(options.ParameterLinks.PayloadKeys).ToArray();
        }

        if (options.Timeout <= TimeSpan.Zero) throw new ArgumentException("Timeout must be positive.", nameof(options));
        if (options.MaxRetries < 0) throw new ArgumentException("MaxRetries must not be negative.", nameof(options));
        if (options.RetryDelay < TimeSpan.Zero) throw new ArgumentException("RetryDelay must not be negative.", nameof(options));
        if (options.DedupeWindow < TimeSpan.Zero) throw new ArgumentException("DedupeWindow must not be negative.", nameof(options));

        _timeout = options.Timeout;
        _maxRetries = options.MaxRetries;
        _retryDelay = options.RetryDelay;
        _dedupeWindow = options.DedupeWindow;
        _reportAppOpens = options.ReportAppOpens;
        _onError = options.OnError;
        _clock = options.Clock ?? DefaultClock;
        _delay = options.Delay ?? DefaultDelay;
        _disposeToken = _disposeCts.Token;

        switch (options.Platform)
        {
            case StrybokPlatform.iOS:
                _platform = "ios";
                break;
            case StrybokPlatform.Android:
                _platform = "android";
                break;
            default:
                _platform = "unknown";
                break;
        }

        _deferredStore = options.DeferredLinkStore;
        var sources = options.InstallReferrerSources;
        _referrerSources = sources is null ? new InstallReferrerSource?[0] : new List<InstallReferrerSource?>(sources).ToArray();
        _deferredIdentifier = _platform == "android"
            ? NormalizeIdentifier(options.AndroidPackageName)
            : _platform == "ios" ? NormalizeIdentifier(options.IosBundleId) : null;
        _deferredPossible = _deferredStore is not null && _deferredIdentifier is not null;
        _allowMatching = options.AllowProbabilisticMatching;
        _deviceFamily = options.DeviceFamily;
        _locale = options.Locale;
        _pasteboardMayContainLink = options.PasteboardMayContainLink;
        _deferredRequested = options.DeferredLinks;
        _deferredStatus = _deferredRequested && _deferredPossible
            ? DeferredLinkStatus.Pending
            : DeferredLinkStatus.Disabled;

        if (httpClient is not null)
        {
            _http = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            _http = options.HttpMessageHandler is not null
                ? new HttpClient(options.HttpMessageHandler, disposeHandler: false)
                : new HttpClient();
            // The SDK applies its own per-attempt timeout.
            _http.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
            _ownsHttpClient = true;
        }
    }

    /// <summary>
    /// Resolved links, each delivered exactly once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Links resolved before the first handler is attached (for example the one that cold-started
    /// the app) are kept and replayed to that first handler, synchronously, while it is being
    /// attached. Later links are raised on whatever thread finished the network request, so
    /// switch to the UI thread before navigating.
    /// </para>
    /// <para>An exception thrown by a handler is caught and does not affect other handlers.</para>
    /// </remarks>
    public event EventHandler<ResolvedDeepLink> LinkReceived
    {
        add
        {
            if (value is null) return;
            ResolvedDeepLink[] queued;
            lock (_gate)
            {
                if (_disposed) return;
                _handlers += value;
                if (_pending.Count == 0) return;
                // Taking the queue under the lock means a link is either queued here or raised
                // to the handlers by Deliver, never both.
                queued = _pending.ToArray();
                _pending.Clear();
                _initialLink = null;
            }

            foreach (var link in queued) Raise(value, link);
        }

        remove
        {
            if (value is null) return;
            lock (_gate)
            {
                _handlers -= value;
            }
        }
    }

    /// <summary>
    /// Starts listening for links and handles the one that launched the app.
    /// </summary>
    /// <remarks>
    /// Completes once the launch link, if any, has been resolved (or has failed), so it is safe
    /// to await before showing the first page. Calling it again returns the same task.
    /// </remarks>
    public Task InitializeAsync()
    {
        TaskCompletionSource<bool> completion;
        lock (_gate)
        {
            if (_disposed) return Task.FromException(NewDisposedException());
            if (_initialization is not null) return _initialization;
            completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _initialization = completion.Task;
        }

        // Started outside the lock: the link source may call back synchronously.
        _ = RunInitializationAsync(completion);
        return completion.Task;
    }

    /// <summary>
    /// The link that launched the app, if it has not been delivered through
    /// <see cref="LinkReceived"/> yet. Returns it at most once and removes it from the queue, so
    /// using both APIs never navigates twice.
    /// </summary>
    public async Task<ResolvedDeepLink?> GetInitialLinkAsync()
    {
        await InitializeAsync().ConfigureAwait(false);
        lock (_gate)
        {
            var link = _initialLink;
            _initialLink = null;
            return link is not null && _pending.Remove(link) ? link : null;
        }
    }

    /// <summary>
    /// Whether <paramref name="uri"/> is a Strybok link of this app: <c>https</c>, a host from
    /// <see cref="StrybokOptions.AllowedHosts"/>, no credentials, no port other than 443 and a
    /// path that is exactly one slug or, with <see cref="StrybokOptions.ParameterLinks"/>, a route
    /// path or a Firebase long link. The check is made on <see cref="Uri.OriginalString"/>.
    /// </summary>
    public bool IsStrybokUri(Uri? uri) => uri is not null && IsStrybokUri(uri.OriginalString);

    /// <summary>Same as <see cref="IsStrybokUri(Uri)"/> for a URL string exactly as received.</summary>
    public bool IsStrybokUri(string? url) => LinkUrl.TryParse(url, _allowedHosts, _payloadKeys, out _);

    /// <summary>
    /// Resolves <paramref name="uri"/> through the API without raising <see cref="LinkReceived"/>
    /// and without reporting an app open (QR scanners, tests).
    /// </summary>
    /// <exception cref="UnsupportedLinkException">The URL is not ours; no network call is made.</exception>
    /// <exception cref="LinkNotFoundException">Unknown, disabled or expired link.</exception>
    /// <exception cref="StrybokNetworkException">The request failed, after all retries.</exception>
    /// <exception cref="StrybokTimeoutException">No answer in time, after all retries.</exception>
    /// <exception cref="StrybokServerException">Unexpected status or body.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="ObjectDisposedException">The client was disposed.</exception>
    public async Task<ResolvedDeepLink> ResolveAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        if (uri is null) throw new ArgumentNullException(nameof(uri));
        ThrowIfDisposed();
        if (!LinkUrl.TryParse(uri.OriginalString, _allowedHosts, _payloadKeys, out var lookup))
        {
            throw new UnsupportedLinkException("Not a link for this app", uri);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeToken);
        try
        {
            return await ResolveCoreAsync(uri, lookup, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && _disposeToken.IsCancellationRequested)
        {
            throw NewDisposedException();
        }
    }

    /// <summary>
    /// Asks the service for a short link to a screen of this app, for a "Share" button. Hand
    /// <see cref="CreatedLink.Url"/> to the share sheet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Needs <see cref="StrybokOptions.ClientKey"/>, the publishable key (<c>pk_…</c>) from the
    /// dashboard, where creating links from the app must be switched on and the allowed route
    /// patterns listed. It only runs when you call it: it works before and without
    /// <see cref="InitializeAsync"/>, never raises <see cref="LinkReceived"/> and never calls
    /// <see cref="StrybokOptions.OnError"/>.
    /// </para>
    /// <para>
    /// Asking again for the same content returns the same link, so the SDK safely retries
    /// timeouts, network errors and 5xx with <see cref="StrybokOptions.MaxRetries"/> and
    /// <see cref="StrybokOptions.RetryDelay"/>. 4xx answers, including 429, are never retried.
    /// Rate limits, network errors, timeouts and server errors are temporary: let the user try
    /// again later. The other errors need a change in the app or in the dashboard.
    /// </para>
    /// <para>
    /// Errors never contain the client key or the payload. Do not put personal data in the route
    /// or the payload: whoever receives the link can read them.
    /// </para>
    /// </remarks>
    /// <param name="routePath">In-app route the link opens: starts with one <c>/</c>, at most 512 characters.</param>
    /// <param name="payload">
    /// Optional JSON object delivered with the link, e.g.
    /// <c>JsonSerializer.SerializeToElement(new { productId = 42 })</c>.
    /// </param>
    /// <param name="title">Optional title of the link preview.</param>
    /// <param name="description">Optional description of the link preview.</param>
    /// <param name="imageUrl">Optional image of the link preview; a public <c>https</c> URL.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="StrybokUnauthorizedException">No client key configured (no network call), or the key was refused (401).</exception>
    /// <exception cref="StrybokValidationException">Bad route or payload (no network call), or rejected by the service (400, 413, 422).</exception>
    /// <exception cref="RouteNotAllowedException">The route is not covered by the application's route patterns (403).</exception>
    /// <exception cref="StrybokRateLimitedException">Too many requests or the daily limit is reached (429).</exception>
    /// <exception cref="StrybokNetworkException">The request failed, after all retries.</exception>
    /// <exception cref="StrybokTimeoutException">No answer in time, after all retries.</exception>
    /// <exception cref="StrybokServerException">5xx after all retries, another status, or an unreadable answer.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="ObjectDisposedException">The client was disposed.</exception>
    public async Task<CreatedLink> CreateLinkAsync(
        string routePath,
        JsonElement? payload = null,
        string? title = null,
        string? description = null,
        string? imageUrl = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        // The checks of the contract, in its order, before any network call.
        var clientKey = _clientKey;
        if (clientKey is null)
        {
            throw new StrybokUnauthorizedException("No client key is configured (StrybokOptions.ClientKey)");
        }
        if (routePath is null || !routePath.StartsWith("/", StringComparison.Ordinal))
        {
            throw new StrybokValidationException("routePath must start with \"/\"");
        }
        if (routePath.StartsWith("//", StringComparison.Ordinal))
        {
            throw new StrybokValidationException("routePath must not start with \"//\"");
        }
        if (routePath.Length > MaxRoutePathLength)
        {
            throw new StrybokValidationException("routePath must be at most 512 characters");
        }

        // Built once: every retry sends exactly the same bytes.
        var body = BuildCreateLinkBody(routePath, payload, title, description, imageUrl);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeToken);
        try
        {
            return await CreateLinkCoreAsync(routePath, clientKey, body, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && _disposeToken.IsCancellationRequested)
        {
            throw NewDisposedException();
        }
    }

    /// <summary>Stops listening and releases resources. The instance cannot be reused.</summary>
    public void Dispose()
    {
        IDisposable? subscription;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _pending.Clear();
            _initialLink = null;
            _handlers = null;
            _statusHandlers = null;
            subscription = _subscription;
            _subscription = null;
        }

        try
        {
            subscription?.Dispose();
        }
        catch (Exception error)
        {
            Debug.WriteLine("Strybok: the link source failed to unsubscribe: " + error);
        }

        try
        {
            // Aborts requests that are still in flight. The source is intentionally not disposed
            // afterwards: its token is still read by those requests while they unwind.
            _disposeCts.Cancel();
        }
        catch (AggregateException error)
        {
            Debug.WriteLine("Strybok: a cancellation callback failed: " + error);
        }

        if (_ownsHttpClient) _http.Dispose();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return default;
    }

    // ---------------------------------------------------------------------------------------

    private async Task RunInitializationAsync(TaskCompletionSource<bool> completion)
    {
        try
        {
            // Listen first so a link arriving during start-up is not missed.
            var subscription = _source.Subscribe(OnSourceLink);
            var keep = false;
            lock (_gate)
            {
                if (!_disposed)
                {
                    _subscription = subscription;
                    keep = true;
                }
            }
            if (!keep)
            {
                subscription?.Dispose();
                completion.TrySetResult(true);
                return;
            }

            Uri? initial;
            try
            {
                initial = await _source.GetInitialLinkAsync(_disposeToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                initial = null; // The platform could not tell us; treat as "no launch link".
            }

            if (initial is not null) await HandleAsync(initial, isInitial: true).ConfigureAwait(false);
            completion.TrySetResult(true);
            // After everything else, and never awaited: the deferred-link check.
            OnStartCompleted();
        }
        catch (Exception error)
        {
            // Only a link source that fails to subscribe gets here.
            completion.TrySetException(error);
        }
    }

    private void OnSourceLink(Uri uri)
    {
        _ = HandleAsync(uri, isInitial: false);
    }

    /// <summary>The automatic flow. Never throws.</summary>
    private async Task HandleAsync(Uri uri, bool isInitial)
    {
        try
        {
            if (uri is null) return;
            var original = uri.OriginalString;
            // Not ours: dropped silently, without an error or a network call.
            if (!LinkUrl.TryParse(original, _allowedHosts, _payloadKeys, out var lookup)) return;

            lock (_gate)
            {
                if (_disposed) return;
                // From here on a deferred link would be a second navigation: a normal link wins.
                _normalLinkAccepted = true;
                if (IsDuplicate(original)) return;
            }

            ResolvedDeepLink link;
            try
            {
                link = await ResolveCoreAsync(uri, lookup, _disposeToken).ConfigureAwait(false);
            }
            catch (StrybokException error)
            {
                ReportError(error);
                return;
            }

            if (Deliver(link, isInitial) && _reportAppOpens)
            {
                _ = ReportAppOpenAsync(link);
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed mid-flight; nothing to deliver.
        }
        catch (Exception error)
        {
            Debug.WriteLine("Strybok: unexpected failure while handling a link: " + error);
        }
    }

    /// <summary>
    /// The OS (or the glue code) can hand over the same tap twice, e.g. as the launch link and
    /// again on the stream. Must be called with <see cref="_gate"/> held.
    /// </summary>
    private bool IsDuplicate(string url)
    {
        var now = _clock();
        var duplicate = false;
        if (_lastUrl is not null && string.Equals(_lastUrl, url, StringComparison.Ordinal))
        {
            var elapsed = now - _lastUrlAt;
            // A negative value means the clock was set back; do not swallow the link then.
            duplicate = elapsed >= TimeSpan.Zero && elapsed < _dedupeWindow;
        }
        _lastUrl = url;
        _lastUrlAt = now;
        return duplicate;
    }

    /// <summary>Raises or queues the link. Returns <c>false</c> when the client is disposed.</summary>
    private bool Deliver(ResolvedDeepLink link, bool isInitial)
    {
        EventHandler<ResolvedDeepLink>? handlers;
        lock (_gate)
        {
            if (_disposed) return false;
            handlers = _handlers;
            if (handlers is null)
            {
                _pending.Add(link);
                if (isInitial) _initialLink = link;
                return true;
            }
        }

        Raise(handlers, link);
        return true;
    }

    private void Raise(EventHandler<ResolvedDeepLink> handlers, ResolvedDeepLink link)
    {
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<ResolvedDeepLink>)handler)(this, link);
            }
            catch (Exception error)
            {
                // One failing handler must not hide the link from the others or stop the SDK.
                Debug.WriteLine("Strybok: a LinkReceived handler threw: " + error);
            }
        }
    }

    private void ReportError(StrybokException error)
    {
        lock (_gate)
        {
            if (_disposed) return;
        }

        try
        {
            _onError?.Invoke(error);
        }
        catch (Exception handlerError)
        {
            Debug.WriteLine("Strybok: the OnError handler threw: " + handlerError);
        }
    }

    private async Task<ResolvedDeepLink> ResolveCoreAsync(Uri uri, string lookup, CancellationToken cancellationToken)
    {
        // Of the link's query only the campaign parameters (and, with ParameterLinks, the payload
        // keys) are part of the lookup; the rest and the fragment may hold private data.
        var endpoint = new Uri(_apiBase + ResolvePath + "?url=" + Uri.EscapeDataString(lookup));

        StrybokException? lastError = null;
        for (var attempt = 0; attempt <= _maxRetries; attempt++)
        {
            if (attempt > 0) await _delay(BackoffBefore(attempt), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            HttpResult result;
            try
            {
                result = await SendWithTimeoutAsync(() => CreateResolveRequest(endpoint), ReadResolveBody, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TimeoutException)
            {
                lastError = new StrybokTimeoutException(uri);
                continue;
            }
            catch (OperationCanceledException)
            {
                // Cancelled by the transport itself, e.g. the Timeout of a supplied HttpClient.
                lastError = new StrybokTimeoutException(uri);
                continue;
            }
            catch (Exception error)
            {
                lastError = new StrybokNetworkException("Network error", uri, error);
                continue;
            }

            if (result.StatusCode == 200) return ParseResolveResponse(uri, result.Body);
            if (result.StatusCode == 404) throw new LinkNotFoundException(uri);

            var serverError = new StrybokServerException("Unexpected status " + result.StatusCode, result.StatusCode, uri);
            if (result.StatusCode < 500) throw serverError; // Client errors will not improve.
            lastError = serverError;
        }

        throw lastError!;
    }

    private TimeSpan BackoffBefore(int attempt)
    {
        var ticks = _retryDelay.Ticks * Math.Pow(2, attempt - 1);
        return ticks >= MaxBackoff.Ticks ? MaxBackoff : TimeSpan.FromTicks((long)ticks);
    }

    private static HttpRequestMessage CreateResolveRequest(Uri endpoint)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    /// <summary>
    /// Sends one request and gives up after <see cref="_timeout"/> with a
    /// <see cref="TimeoutException"/>, even when the transport ignores cancellation.
    /// </summary>
    private async Task<HttpResult> SendWithTimeoutAsync(
        Func<HttpRequestMessage> createRequest,
        Func<int, bool>? readBody,
        CancellationToken cancellationToken)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var send = SendAsync(createRequest, readBody, attempt.Token);
            if (!send.IsCompleted)
            {
                var timer = _delay(_timeout, attempt.Token);
                var winner = await Task.WhenAny(send, timer).ConfigureAwait(false);
                if (!ReferenceEquals(winner, send))
                {
                    Observe(send);
                    // The timer also ends when the caller cancels or the client is disposed.
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new TimeoutException();
                }
                Observe(timer);
            }

            return await send.ConfigureAwait(false);
        }
        finally
        {
            // Stops the timer, or aborts the request that was given up on.
            attempt.Cancel();
        }
    }

    /// <param name="createRequest">Makes a fresh request message; one is needed per attempt.</param>
    /// <param name="readBody">For which status codes the body is read; <c>null</c> for none.</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    private async Task<HttpResult> SendAsync(
        Func<HttpRequestMessage> createRequest,
        Func<int, bool>? readBody,
        CancellationToken cancellationToken)
    {
        using var request = createRequest();
        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        var statusCode = (int)response.StatusCode;
        var body = string.Empty;
        HttpContent? content = response.Content;
        if (readBody is not null && readBody(statusCode) && content is not null)
        {
            body = await content.ReadAsStringAsync().ConfigureAwait(false);
        }

        string? retryAfter = null;
        if (statusCode == 429 && response.Headers.TryGetValues("Retry-After", out var values) && values is not null)
        {
            foreach (var value in values)
            {
                retryAfter = value;
                break;
            }
        }
        return new HttpResult(statusCode, body, retryAfter);
    }

    /// <summary>Marks the outcome of an abandoned task as seen so it is not reported as unobserved.</summary>
    private static void Observe(Task task)
    {
        _ = task.ContinueWith(
            static completed => { _ = completed.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static ResolvedDeepLink ParseResolveResponse(Uri uri, string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && TryGetString(root, "linkId", out var linkId)
                && TryGetString(root, "appId", out var appId)
                && TryGetString(root, "routePath", out var routePath)
                && routePath.StartsWith("/", StringComparison.Ordinal)
                && !routePath.StartsWith("//", StringComparison.Ordinal))
            {
                // Clone: the element must outlive the document.
                var payload = root.TryGetProperty("payload", out var value) && value.ValueKind == JsonValueKind.Object
                    ? value.Clone()
                    : EmptyPayload;
                var campaign = root.TryGetProperty("campaign", out var campaignValue)
                    ? ParseCampaign(campaignValue)
                    : LinkCampaign.Empty;
                return new ResolvedDeepLink(uri, linkId, appId, routePath, payload, campaign);
            }
        }
        catch (JsonException)
        {
            // Fall through to the error below.
        }

        throw new StrybokServerException("Malformed resolve response", 200, uri);
    }

    /// <summary>Keeps the five string fields; anything else, or no object at all, is not an error.</summary>
    private static LinkCampaign ParseCampaign(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return LinkCampaign.Empty;
        var campaign = new LinkCampaign(
            GetOptionalString(element, "source"),
            GetOptionalString(element, "medium"),
            GetOptionalString(element, "campaign"),
            GetOptionalString(element, "term"),
            GetOptionalString(element, "content"));
        return campaign.IsEmpty ? LinkCampaign.Empty : campaign;
    }

    private static string? GetOptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    /// <summary>What resolve returned, unchanged; nothing when there are no values.</summary>
    private static void WriteCampaign(Utf8JsonWriter writer, LinkCampaign campaign)
    {
        if (campaign.IsEmpty) return;
        writer.WriteStartObject("campaign");
        if (campaign.Source is not null) writer.WriteString("source", campaign.Source);
        if (campaign.Medium is not null) writer.WriteString("medium", campaign.Medium);
        if (campaign.Campaign is not null) writer.WriteString("campaign", campaign.Campaign);
        if (campaign.Term is not null) writer.WriteString("term", campaign.Term);
        if (campaign.Content is not null) writer.WriteString("content", campaign.Content);
        writer.WriteEndObject();
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString() ?? string.Empty;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static JsonElement CreateEmptyPayload()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Best effort and never awaited by delivery. This is a self-reported metric (an app can be
    /// modified), separate from server-side fallback visits.
    /// </summary>
    private async Task ReportAppOpenAsync(ResolvedDeepLink link)
    {
        try
        {
            byte[] body;
            using (var buffer = new MemoryStream())
            {
                using (var writer = new Utf8JsonWriter(buffer))
                {
                    writer.WriteStartObject();
                    writer.WriteString("eventId", Guid.NewGuid().ToString("D"));
                    writer.WriteString("linkId", link.LinkId);
                    writer.WriteString("type", "app_open");
                    writer.WriteString("platform", _platform);
                    writer.WriteString("sdkVersion", SdkVersion);
                    WriteCampaign(writer, link.Campaign);
                    writer.WriteEndObject();
                }
                body = buffer.ToArray();
            }

            var endpoint = new Uri(_apiBase + EventsPath);
            await SendWithTimeoutAsync(() => CreateEventRequest(endpoint, body), null, _disposeToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Analytics must never affect the app.
        }
    }

    private static HttpRequestMessage CreateEventRequest(Uri endpoint, byte[] body)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
    }

    // ---------------------------------------------------------------------------------------
    // Creating links
    // ---------------------------------------------------------------------------------------

    private async Task<CreatedLink> CreateLinkCoreAsync(
        string routePath,
        string clientKey,
        byte[] body,
        CancellationToken cancellationToken)
    {
        var endpoint = new Uri(_apiBase + LinksPath);

        // Creation is idempotent on the service (same content, same link), so a retry after a
        // lost answer cannot make a second link.
        StrybokException? lastError = null;
        for (var attempt = 0; attempt <= _maxRetries; attempt++)
        {
            if (attempt > 0) await _delay(BackoffBefore(attempt), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            HttpResult result;
            try
            {
                result = await SendWithTimeoutAsync(
                        () => CreateLinkRequest(endpoint, clientKey, body),
                        ReadCreateLinkBody,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TimeoutException)
            {
                lastError = new StrybokTimeoutException("The create-link request timed out", null);
                continue;
            }
            catch (OperationCanceledException)
            {
                // Cancelled by the transport itself, e.g. the Timeout of a supplied HttpClient.
                lastError = new StrybokTimeoutException("The create-link request timed out", null);
                continue;
            }
            catch (Exception error)
            {
                lastError = new StrybokNetworkException("Network error", null, error);
                continue;
            }

            var status = result.StatusCode;
            // 200: an identical link already existed. 201: a new one was made.
            if (status == 200 || status == 201) return ParseCreateLinkResponse(status, result.Body);
            if (status == 401)
            {
                throw new StrybokUnauthorizedException(
                    "The client key was not accepted, or creating links from the app is switched off");
            }
            if (status == 403) throw new RouteNotAllowedException(routePath);
            if (status == 429) throw new StrybokRateLimitedException(ParseRetryAfter(result.RetryAfter));
            if (status == 400 || status == 413 || status == 422) throw ParseValidationResponse(status, result.Body);

            var serverError = new StrybokServerException("Unexpected status " + status, status);
            if (status < 500) throw serverError; // Client errors will not improve.
            lastError = serverError;
        }

        throw lastError!;
    }

    /// <summary>
    /// The request body. Arguments that were not given are left out (the service treats
    /// <c>null</c> like "omitted", but the SDK never sends it).
    /// </summary>
    private static byte[] BuildCreateLinkBody(
        string routePath,
        JsonElement? payload,
        string? title,
        string? description,
        string? imageUrl)
    {
        try
        {
            // Checked before anything is written. An element of a disposed JsonDocument throws
            // here and is reported by the catch below.
            if (payload.HasValue && payload.GetValueOrDefault().ValueKind != JsonValueKind.Object)
            {
                throw new StrybokValidationException("payload must be a JSON object");
            }

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("routePath", routePath);
                if (payload.HasValue)
                {
                    writer.WritePropertyName("payload");
                    payload.GetValueOrDefault().WriteTo(writer);
                }
                if (title is not null) writer.WriteString("title", title);
                if (description is not null) writer.WriteString("description", description);
                if (imageUrl is not null) writer.WriteString("imageUrl", imageUrl);
                writer.WriteEndObject();
            }
            return buffer.ToArray();
        }
        catch (StrybokValidationException)
        {
            throw;
        }
        catch (Exception)
        {
            // A payload whose JsonDocument was disposed, or text that is not valid UTF-16. The
            // cause is not attached: it could quote parts of the payload.
            throw new StrybokValidationException("The link cannot be written as JSON");
        }
    }

    private static HttpRequestMessage CreateLinkRequest(Uri endpoint, string clientKey, byte[] body)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        // Not "Authorization": the key is publishable and must never be taken for a bearer secret.
        request.Headers.TryAddWithoutValidation(ClientKeyHeader, clientKey);
        return request;
    }

    private static CreatedLink ParseCreateLinkResponse(int statusCode, string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && TryGetString(root, "linkId", out var linkId)
                && TryGetString(root, "slug", out var slug)
                && TryGetString(root, "routePath", out var routePath)
                && TryGetString(root, "url", out var url)
                && url.StartsWith("https://", StringComparison.Ordinal)
                && Uri.TryCreate(url, UriKind.Absolute, out var parsed)
                && parsed is not null)
            {
                return new CreatedLink(parsed, slug, routePath, linkId);
            }
        }
        catch (JsonException)
        {
            // Fall through to the error below.
        }

        throw new StrybokServerException("Malformed create-link response", statusCode);
    }

    /// <summary>The service's <c>error.message</c> and <c>error.details.issues</c>, when the body has them.</summary>
    private static StrybokValidationException ParseValidationResponse(int statusCode, string body)
    {
        string? message = null;
        List<StrybokValidationIssue>? issues = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object)
            {
                if (TryGetString(error, "message", out var text) && text.Length > 0) message = text;

                if (error.TryGetProperty("details", out var details)
                    && details.ValueKind == JsonValueKind.Object
                    && details.TryGetProperty("issues", out var list)
                    && list.ValueKind == JsonValueKind.Array)
                {
                    issues = new List<StrybokValidationIssue>();
                    foreach (var item in list.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object) continue;
                        TryGetString(item, "path", out var path);
                        TryGetString(item, "message", out var issueMessage);
                        if (path.Length == 0 && issueMessage.Length == 0) continue;
                        issues.Add(new StrybokValidationIssue(path, issueMessage));
                    }
                }
            }
        }
        catch (JsonException)
        {
            // No readable details; the status alone says it was a validation error.
        }

        message ??= "The server rejected the link (status " + statusCode.ToString(CultureInfo.InvariantCulture) + ")";
        return new StrybokValidationException(message, statusCode, issues);
    }

    /// <summary>
    /// Whole seconds from a <c>Retry-After</c> header; <c>null</c> for anything else (an HTTP
    /// date, for example).
    /// </summary>
    private static TimeSpan? ParseRetryAfter(string? header)
    {
        if (header is null) return null;
        var text = header.Trim();
        if (text.Length == 0) return null;

        long seconds = 0;
        foreach (var c in text)
        {
            if (c < '0' || c > '9') return null;
            if (seconds <= int.MaxValue) seconds = (seconds * 10) + (c - '0');
        }
        return TimeSpan.FromSeconds(Math.Min(seconds, int.MaxValue));
    }

    /// <summary>
    /// The key without surrounding whitespace; <c>null</c> when it is missing or cannot be sent as
    /// a header value (spaces, control or non-ASCII characters): such a key is never valid.
    /// </summary>
    private static string? NormalizeClientKey(string? clientKey)
    {
        if (clientKey is null) return null;
        var key = clientKey.Trim();
        if (key.Length == 0) return null;
        foreach (var c in key)
        {
            if (c <= 0x20 || c >= 0x7F) return null;
        }
        return key;
    }

    private void ThrowIfDisposed()
    {
        lock (_gate)
        {
            if (_disposed) throw NewDisposedException();
        }
    }

    private static ObjectDisposedException NewDisposedException() =>
        new ObjectDisposedException(nameof(StrybokClient));

    private static DateTimeOffset DefaultClock() => DateTimeOffset.UtcNow;

    private static Task DefaultDelay(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);

    private readonly struct HttpResult
    {
        public HttpResult(int statusCode, string body, string? retryAfter)
        {
            StatusCode = statusCode;
            Body = body;
            RetryAfter = retryAfter;
        }

        public int StatusCode { get; }

        public string Body { get; }

        /// <summary>The <c>Retry-After</c> header of a 429 answer, when there was one.</summary>
        public string? RetryAfter { get; }
    }
}
