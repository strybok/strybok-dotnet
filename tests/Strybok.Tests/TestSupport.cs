using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Strybok.Tests;

internal static class Links
{
    public const string Host = "my-app.strybok.link";
    public const string InviteUrl = "https://my-app.strybok.link/invite777";

    public static readonly Uri Api = new Uri("https://api.strybok.com");

    public static Uri Invite => new Uri(InviteUrl);

    public static Uri On(string slug) => new Uri("https://" + Host + "/" + slug);
}

internal static class Responses
{
    public static HttpResponseMessage Ok(string route = "/invite/abc123", string linkId = "link_1") =>
        Json(
            HttpStatusCode.OK,
            "{\"linkId\":\"" + linkId + "\",\"appId\":\"app_1\",\"routePath\":\"" + route
                + "\",\"payload\":{\"invitationId\":\"abc123\"}}");

    public static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>The answer of the create-link endpoint.</summary>
    public const string CreatedBody =
        "{\"linkId\":\"link_9\",\"url\":\"https://my-app.strybok.link/Ab3xK9\",\"slug\":\"Ab3xK9\",\"routePath\":\"/product/42\"}";

    public static HttpResponseMessage Created(int status = 201) => Json((HttpStatusCode)status, CreatedBody);

    public static HttpResponseMessage Status(int status) =>
        new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(string.Empty) };
}

internal static class Async
{
    /// <summary>A task that only ends, as cancelled, when the token is cancelled.</summary>
    public static Task<T> Never<T>(CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.Register(() => completion.TrySetCanceled());
        return completion.Task;
    }

    /// <summary>Waits until the condition holds; fails the test after five seconds.</summary>
    public static async Task Eventually(Func<bool> condition, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) Assert.Fail("Timed out waiting for: " + what);
            await Task.Delay(5);
        }
    }

    /// <summary>Gives fire-and-forget work time to (wrongly) do something before a negative assertion.</summary>
    public static Task Settle() => Task.Delay(80);
}

internal sealed class RecordedRequest
{
    public RecordedRequest(HttpMethod method, Uri uri, string accept)
    {
        Method = method;
        Uri = uri;
        Accept = accept;
    }

    public HttpMethod Method { get; }

    public Uri Uri { get; }

    public string Accept { get; }

    /// <summary>The decoded <c>url</c> query parameter of a resolve call.</summary>
    public string LookupUrl
    {
        get
        {
            const string prefix = "?url=";
            var query = Uri.Query;
            Assert.StartsWith(prefix, query, StringComparison.Ordinal);
            var value = query.Substring(prefix.Length);
            Assert.DoesNotContain("&", value, StringComparison.Ordinal);
            return System.Uri.UnescapeDataString(value);
        }
    }
}

internal sealed class RecordedEvent
{
    public RecordedEvent(HttpMethod method, Uri uri, string? contentType, JsonElement body)
    {
        Method = method;
        Uri = uri;
        ContentType = contentType;
        Body = body;
    }

    public HttpMethod Method { get; }

    public Uri Uri { get; }

    public string? ContentType { get; }

    public JsonElement Body { get; }
}

/// <summary>One create-link call as the server would see it.</summary>
internal sealed class RecordedCreate
{
    public RecordedCreate(
        HttpMethod method,
        Uri uri,
        IReadOnlyDictionary<string, string> headers,
        string? contentType,
        string body)
    {
        Method = method;
        Uri = uri;
        Headers = headers;
        ContentType = contentType;
        Body = body;
    }

    public HttpMethod Method { get; }

    public Uri Uri { get; }

    /// <summary>Request headers (not content headers), names lower-cased, values joined with a comma.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    public string? ContentType { get; }

    /// <summary>The body exactly as sent.</summary>
    public string Body { get; }

    /// <summary>The body parsed; fails the test when it is not JSON.</summary>
    public JsonElement Json
    {
        get
        {
            using var document = JsonDocument.Parse(Body);
            return document.RootElement.Clone();
        }
    }
}

/// <summary>
/// Records requests and answers resolve calls with <see cref="OnResolve"/> and create-link calls
/// with <see cref="OnCreate"/>.
/// </summary>
internal sealed class FakeApi : HttpMessageHandler
{
    private readonly object _gate = new object();
    private readonly List<RecordedCreate> _creates = new List<RecordedCreate>();
    private readonly List<RecordedRequest> _resolves = new List<RecordedRequest>();
    private readonly List<RecordedEvent> _events = new List<RecordedEvent>();
    private readonly List<RecordedCreate> _deferred = new List<RecordedCreate>();

    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? OnResolve { get; set; }

    public Func<CancellationToken, Task<HttpResponseMessage>>? OnEvent { get; set; }

    /// <summary>Answers create-link calls; 201 with <see cref="Responses.CreatedBody"/> when unset.</summary>
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? OnCreate { get; set; }

    /// <summary>Answers deferred-link calls (redeem and match); 404 when unset.</summary>
    public Func<RecordedCreate, CancellationToken, Task<HttpResponseMessage>>? OnDeferred { get; set; }

    /// <summary>The redeem and match calls, in order.</summary>
    public IReadOnlyList<RecordedCreate> Deferred
    {
        get
        {
            lock (_gate) return _deferred.ToArray();
        }
    }

    public IReadOnlyList<RecordedCreate> Creates
    {
        get
        {
            lock (_gate) return _creates.ToArray();
        }
    }

    public IReadOnlyList<RecordedRequest> Resolves
    {
        get
        {
            lock (_gate) return _resolves.ToArray();
        }
    }

    public IReadOnlyList<RecordedEvent> Events
    {
        get
        {
            lock (_gate) return _events.ToArray();
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        if (uri.AbsolutePath.EndsWith("/v1/public/events", StringComparison.Ordinal))
        {
            var text = request.Content is null ? "null" : await request.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(text);
            var recorded = new RecordedEvent(
                request.Method,
                uri,
                request.Content?.Headers.ContentType?.ToString(),
                document.RootElement.Clone());
            lock (_gate) _events.Add(recorded);

            var onEvent = OnEvent;
            return onEvent is null ? new HttpResponseMessage(HttpStatusCode.Accepted) : await onEvent(cancellationToken);
        }

        if (uri.AbsolutePath.Contains("/v1/public/deferred-links/"))
        {
            var text = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync();
            var headers = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var header in request.Headers)
            {
                headers[header.Key.ToLowerInvariant()] = string.Join(",", header.Value);
            }
            var recorded = new RecordedCreate(
                request.Method,
                uri,
                headers,
                request.Content?.Headers.ContentType?.ToString(),
                text);
            lock (_gate) _deferred.Add(recorded);

            var onDeferred = OnDeferred;
            return onDeferred is null ? Responses.Status(404) : await onDeferred(recorded, cancellationToken);
        }

        if (uri.AbsolutePath.EndsWith("/v1/public/links", StringComparison.Ordinal))
        {
            var text = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync();
            var headers = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var header in request.Headers)
            {
                headers[header.Key.ToLowerInvariant()] = string.Join(",", header.Value);
            }
            var recorded = new RecordedCreate(
                request.Method,
                uri,
                headers,
                request.Content?.Headers.ContentType?.ToString(),
                text);
            lock (_gate) _creates.Add(recorded);

            var onCreate = OnCreate;
            return onCreate is null ? Responses.Created() : await onCreate(request, cancellationToken);
        }

        var accept = string.Join(",", request.Headers.Accept.Select(value => value.ToString()));
        lock (_gate) _resolves.Add(new RecordedRequest(request.Method, uri, accept));

        var onResolve = OnResolve;
        return onResolve is null ? Responses.Ok() : await onResolve(request, cancellationToken);
    }
}

/// <summary>A link source driven by the test.</summary>
internal sealed class FakeLinkSource : ILinkSource
{
    private readonly object _gate = new object();
    private readonly List<Action<Uri>> _handlers = new List<Action<Uri>>();
    private readonly List<string> _calls = new List<string>();

    public Uri? InitialLink { get; set; }

    /// <summary>Also report the launch link on the stream, like many platforms do.</summary>
    public bool ReplayInitialLinkOnStream { get; set; }

    public bool FailInitialLink { get; set; }

    public bool HasListener
    {
        get
        {
            lock (_gate) return _handlers.Count > 0;
        }
    }

    /// <summary>The order in which the SDK used the source.</summary>
    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_gate) return _calls.ToArray();
        }
    }

    public Task<Uri?> GetInitialLinkAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate) _calls.Add("getInitialLink");
        if (FailInitialLink) throw new InvalidOperationException("platform failure");
        return Task.FromResult(InitialLink);
    }

    public IDisposable Subscribe(Action<Uri> onLink)
    {
        lock (_gate)
        {
            _calls.Add("subscribe");
            _handlers.Add(onLink);
        }

        if (ReplayInitialLinkOnStream && InitialLink is not null) onLink(InitialLink);
        return new Subscription(this, onLink);
    }

    public void Emit(Uri uri)
    {
        Action<Uri>[] handlers;
        lock (_gate) handlers = _handlers.ToArray();
        foreach (var handler in handlers) handler(uri);
    }

    private sealed class Subscription : IDisposable
    {
        private readonly FakeLinkSource _owner;
        private readonly Action<Uri> _handler;

        public Subscription(FakeLinkSource owner, Action<Uri> handler)
        {
            _owner = owner;
            _handler = handler;
        }

        public void Dispose()
        {
            lock (_owner._gate) _owner._handlers.Remove(_handler);
        }
    }
}

/// <summary>
/// Replaces real waiting. Retry pauses are recorded and end immediately. The per-attempt timeout
/// (recognised by its length) never fires unless <see cref="FireTimeouts"/> is set, in which case
/// it fires immediately; the same goes for the 10-second limit of an install referrer source and
/// <see cref="FireSourceTimeouts"/>. Either way no test waits for wall-clock time.
/// </summary>
internal sealed class FakeDelay
{
    private readonly object _gate = new object();
    private readonly List<TimeSpan> _retryPauses = new List<TimeSpan>();

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>What the SDK allows one install referrer source.</summary>
    public static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(10);

    public bool FireTimeouts { get; set; }

    public bool FireSourceTimeouts { get; set; }

    public IReadOnlyList<TimeSpan> RetryPauses
    {
        get
        {
            lock (_gate) return _retryPauses.ToArray();
        }
    }

    public Task Wait(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay == Timeout)
        {
            return FireTimeouts ? Task.CompletedTask : Async.Never<bool>(cancellationToken);
        }
        if (delay == SourceTimeout)
        {
            return FireSourceTimeouts ? Task.CompletedTask : Async.Never<bool>(cancellationToken);
        }

        lock (_gate) _retryPauses.Add(delay);
        return Task.CompletedTask;
    }
}

/// <summary>A client wired to fakes, plus everything the tests observe.</summary>
internal sealed class Harness : IAsyncDisposable
{
    private readonly object _gate = new object();
    private readonly List<ResolvedDeepLink> _received = new List<ResolvedDeepLink>();
    private readonly List<StrybokException> _errors = new List<StrybokException>();

    public Harness(ILinkSource? source = null, Action<StrybokOptions>? configure = null)
    {
        Source = source as FakeLinkSource ?? new FakeLinkSource();
        var options = new StrybokOptions
        {
            ApiBaseUrl = Links.Api,
            AllowedHosts = { Links.Host },
            Timeout = FakeDelay.Timeout,
            HttpMessageHandler = Api,
            OnError = error =>
            {
                lock (_gate) _errors.Add(error);
            },
            Clock = () => Now,
            Delay = Delay.Wait,
        };
        configure?.Invoke(options);
        Client = new StrybokClient(options, source ?? Source);
    }

    public FakeApi Api { get; } = new FakeApi();

    public FakeDelay Delay { get; } = new FakeDelay();

    /// <summary>The fake source in use; unused when another kind of source was supplied.</summary>
    public FakeLinkSource Source { get; }

    public StrybokClient Client { get; }

    /// <summary>The time seen by the SDK; only moves when a test moves it.</summary>
    public DateTimeOffset Now { get; set; } = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public IReadOnlyList<ResolvedDeepLink> Received
    {
        get
        {
            lock (_gate) return _received.ToArray();
        }
    }

    public IReadOnlyList<StrybokException> Errors
    {
        get
        {
            lock (_gate) return _errors.ToArray();
        }
    }

    /// <summary>Attaches a handler that records into <see cref="Received"/>.</summary>
    public void Listen() => Client.LinkReceived += Record;

    public void StopListening() => Client.LinkReceived -= Record;

    public Task ReceivedCount(int count) =>
        Async.Eventually(() => Received.Count >= count, count + " delivered link(s)");

    public ValueTask DisposeAsync() => Client.DisposeAsync();

    private void Record(object? sender, ResolvedDeepLink link)
    {
        Assert.Same(Client, sender);
        lock (_gate) _received.Add(link);
    }
}
