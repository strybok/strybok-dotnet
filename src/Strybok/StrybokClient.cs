using System;
using System.Collections.Generic;
using System.Diagnostics;
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
/// </code>
/// </example>
public sealed class StrybokClient : IAsyncDisposable, IDisposable
{
    /// <summary>Version reported with analytics events.</summary>
    public const string SdkVersion = "0.1.0";

    private const string ResolvePath = "/v1/public/resolve";
    private const string EventsPath = "/v1/public/events";

    /// <summary>Keeps the doubling retry pause from overflowing with a large <c>MaxRetries</c>.</summary>
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);

    private static readonly JsonElement EmptyPayload = CreateEmptyPayload();

    private readonly object _gate = new object();
    private readonly ILinkSource _source;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly string _apiBase;
    private readonly HashSet<string> _allowedHosts;
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
    /// path that is exactly one slug. The check is made on <see cref="Uri.OriginalString"/>.
    /// </summary>
    public bool IsStrybokUri(Uri? uri) => uri is not null && IsStrybokUri(uri.OriginalString);

    /// <summary>Same as <see cref="IsStrybokUri(Uri)"/> for a URL string exactly as received.</summary>
    public bool IsStrybokUri(string? url) => LinkUrl.TryParse(url, _allowedHosts, out _);

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
        if (!LinkUrl.TryParse(uri.OriginalString, _allowedHosts, out var lookup))
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
            if (!LinkUrl.TryParse(original, _allowedHosts, out var lookup)) return;

            lock (_gate)
            {
                if (_disposed || IsDuplicate(original)) return;
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
        // Query and fragment of the link are not part of the lookup: they may hold private data.
        var endpoint = new Uri(_apiBase + ResolvePath + "?url=" + Uri.EscapeDataString(lookup));

        StrybokException? lastError = null;
        for (var attempt = 0; attempt <= _maxRetries; attempt++)
        {
            if (attempt > 0) await _delay(BackoffBefore(attempt), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            HttpResult result;
            try
            {
                result = await SendWithTimeoutAsync(() => CreateResolveRequest(endpoint), true, cancellationToken)
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
        bool readBody,
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

    private async Task<HttpResult> SendAsync(
        Func<HttpRequestMessage> createRequest,
        bool readBody,
        CancellationToken cancellationToken)
    {
        using var request = createRequest();
        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        var statusCode = (int)response.StatusCode;
        var body = string.Empty;
        HttpContent? content = response.Content;
        if (readBody && statusCode == 200 && content is not null)
        {
            body = await content.ReadAsStringAsync().ConfigureAwait(false);
        }
        return new HttpResult(statusCode, body);
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
                return new ResolvedDeepLink(uri, linkId, appId, routePath, payload);
            }
        }
        catch (JsonException)
        {
            // Fall through to the error below.
        }

        throw new StrybokServerException("Malformed resolve response", 200, uri);
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
                    writer.WriteEndObject();
                }
                body = buffer.ToArray();
            }

            var endpoint = new Uri(_apiBase + EventsPath);
            await SendWithTimeoutAsync(() => CreateEventRequest(endpoint, body), false, _disposeToken)
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
        public HttpResult(int statusCode, string body)
        {
            StatusCode = statusCode;
            Body = body;
        }

        public int StatusCode { get; }

        public string Body { get; }
    }
}
