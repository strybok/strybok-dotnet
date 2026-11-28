using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Strybok;

// Deferred links (SDK contract, "Deferred links (optional, Android)" and "Deferred links on iOS
// (optional)"). Nothing in this file reports to OnError, throws into the app, or logs or stores
// a referrer, a pasted text or a click id.
public sealed partial class StrybokClient
{
    private const string RedeemPath = "/v1/public/deferred-links/redeem";
    private const string MatchPath = "/v1/public/deferred-links/match";
    private const string AttemptsKey = "strybok.deferred.attempts";
    private const string DoneKey = "strybok.deferred.done";
    private const int MaxDeferredAttempts = 3;

    /// <summary>How long one referrer source may take; then it counts as "unavailable for now".</summary>
    internal static readonly TimeSpan ReferrerSourceTimeout = TimeSpan.FromSeconds(10);

    /// <summary>What the server accepts as <c>locale</c>.</summary>
    private static readonly Regex LocalePattern = new Regex(
        @"\A[A-Za-z]{2,3}(?:[-_][A-Za-z0-9]{1,8}){0,4}\z",
        RegexOptions.CultureInvariant);

    // Guarded by _gate.
    private EventHandler<DeferredLinkStatus>? _statusHandlers;
    private DeferredLinkStatus _deferredStatus;
    private Task _pasteChain = Task.CompletedTask;

    /// <summary><see cref="StrybokOptions.DeferredLinks"/>, or <see cref="EnableDeferredLinks"/> was called.</summary>
    private bool _deferredRequested;
    private bool _startCompleted;
    private bool _deferredCheckStarted;

    /// <summary>In-memory mirror of the stored "done": set the moment a flow decides to store it.</summary>
    private bool _deferredDone;

    /// <summary>A link of this app arrived from the link source since the process started.</summary>
    private bool _normalLinkAccepted;
    private bool _awaitingPaste;

    private enum DeferredAnswer
    {
        Link,
        Unusable,
        NotFound,
        Transient,
        Disposed,
    }

    private enum DeferredDelivery
    {
        Delivered,
        Superseded,
        Disposed,
    }

    /// <summary>
    /// Where the deferred-link check stands. Readable at any time; see
    /// <see cref="DeferredLinkStatusChanged"/>.
    /// </summary>
    public DeferredLinkStatus DeferredLinkStatus
    {
        get
        {
            lock (_gate) return _deferredStatus;
        }
    }

    /// <summary>
    /// Raised when <see cref="DeferredLinkStatus"/> changes, on whatever thread the change
    /// happened. For diagnostics; a deferred link itself arrives through
    /// <see cref="LinkReceived"/>.
    /// </summary>
    public event EventHandler<DeferredLinkStatus> DeferredLinkStatusChanged
    {
        add
        {
            lock (_gate)
            {
                if (!_disposed) _statusHandlers += value;
            }
        }

        remove
        {
            lock (_gate) _statusHandlers -= value;
        }
    }

    /// <summary>
    /// Switches <see cref="StrybokOptions.DeferredLinks"/> on after the client was created, for
    /// example after a consent screen. When <see cref="InitializeAsync"/> has already completed
    /// the check runs now, once; otherwise it runs after it as usual. Calling it again does
    /// nothing. Without a <see cref="StrybokOptions.DeferredLinkStore"/> and the platform's
    /// identifier the status stays <see cref="Strybok.DeferredLinkStatus.Disabled"/>.
    /// </summary>
    public void EnableDeferredLinks()
    {
        var run = false;
        lock (_gate)
        {
            if (_disposed || _deferredRequested) return;
            _deferredRequested = true;
            if (!_deferredPossible) return;
            if (_startCompleted && !_deferredCheckStarted)
            {
                _deferredCheckStarted = true;
                run = true;
            }
        }

        SetDeferredStatus(DeferredLinkStatus.Pending);
        if (run) StartDeferredCheck();
    }

    /// <summary>
    /// iOS: redeems the link a person copied on the page before the App Store and pasted in the
    /// app. Pass the pasted text as it is; the SDK never reads the pasteboard itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Text that is not a copied link of this app is dropped without a request
    /// (<see cref="PastedLinkResult.NotOurs"/>) — the person may have had anything on their
    /// clipboard. Otherwise only the single-use id found in the link is sent, with
    /// <see cref="StrybokOptions.IosBundleId"/> and the SDK version. On success the link is
    /// raised through <see cref="LinkReceived"/> with <see cref="ResolvedDeepLink.IsDeferred"/>
    /// set, even when a normal link was delivered earlier in this launch.
    /// </para>
    /// <para>Never throws and never calls <see cref="StrybokOptions.OnError"/>. Calls are handled one after another.</para>
    /// </remarks>
    public Task<PastedLinkResult> RedeemPastedLinkAsync(string? pastedText)
    {
        lock (_gate)
        {
            var run = RedeemPastedAfterAsync(_pasteChain, pastedText);
            _pasteChain = run;
            return run;
        }
    }

    /// <summary>
    /// iOS with probabilistic matching: tells the SDK that the person moved on without pasting,
    /// so matching may start. Does nothing unless the status is
    /// <see cref="Strybok.DeferredLinkStatus.AwaitingPaste"/>.
    /// </summary>
    public void SkipPastedLink() => ContinueMatchingIfAwaiting();

    // ---------------------------------------------------------------------------------------

    /// <summary>Called once <see cref="InitializeAsync"/> has completed everything else.</summary>
    private void OnStartCompleted()
    {
        lock (_gate)
        {
            _startCompleted = true;
            if (_disposed || !_deferredRequested || !_deferredPossible || _deferredCheckStarted) return;
            _deferredCheckStarted = true;
        }

        StartDeferredCheck();
    }

    /// <summary>Off the caller's thread: nothing waits for the check, and sources may block.</summary>
    private void StartDeferredCheck()
    {
        _ = Task.Run(RunDeferredCheckAsync);
    }

    private async Task RunDeferredCheckAsync()
    {
        try
        {
            if (_platform == "android")
            {
                await CheckAndroidAsync().ConfigureAwait(false);
            }
            else
            {
                await CheckIosAsync().ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Never into the app. The attempt was counted; a later launch tries again.
            SetDeferredStatus(DeferredLinkStatus.Pending, onlyFrom: DeferredLinkStatus.Checking);
        }
    }

    private async Task CheckAndroidAsync()
    {
        SetDeferredStatus(DeferredLinkStatus.Checking);
        if (!TryReadDeferredState(out var done, out var attempts))
        {
            SetDeferredStatus(DeferredLinkStatus.Pending);
            return;
        }
        if (done)
        {
            SetDeferredStatus(DeferredLinkStatus.NotFirstLaunch);
            return;
        }
        if (attempts >= MaxDeferredAttempts)
        {
            FinishDeferred(DeferredLinkStatus.Failed);
            return;
        }
        // Before any source is asked: a launch that dies from here on has used up an attempt.
        if (!TryWriteDeferred(AttemptsKey, (attempts + 1).ToString(CultureInfo.InvariantCulture)))
        {
            SetDeferredStatus(DeferredLinkStatus.Pending);
            return;
        }

        string? clickId = null;
        var unavailableForNow = false;
        var anyReferrer = false;
        foreach (var source in _referrerSources)
        {
            var answer = await AskSourceAsync(source).ConfigureAwait(false);
            if (IsDisposed()) return;
            if (answer.Kind == InstallReferrerAnswerKind.Referrer)
            {
                anyReferrer = true;
                clickId = DeferredLinkParsing.FindClickIdInReferrer(answer.ReferrerValue);
                if (clickId is not null) break;
            }
            else if (answer.Kind == InstallReferrerAnswerKind.UnavailableForNow)
            {
                unavailableForNow = true;
            }
        }

        if (clickId is null)
        {
            if (unavailableForNow)
            {
                SetDeferredStatus(DeferredLinkStatus.Pending);
                return;
            }
            FinishDeferred(anyReferrer ? DeferredLinkStatus.NoReferrer : DeferredLinkStatus.Unavailable);
            return;
        }

        if (NormalLinkAccepted())
        {
            FinishDeferred(DeferredLinkStatus.Superseded);
            return;
        }

        var id = clickId;
        var body = BuildDeferredBody(writer =>
        {
            writer.WriteString("platform", "android");
            writer.WriteString("clickId", id);
            writer.WriteString("packageName", _deferredIdentifier);
            writer.WriteString("sdkVersion", SdkVersion);
        });
        var response = await PostDeferredAsync(RedeemPath, body, probabilistic: false).ConfigureAwait(false);
        FinishAutomatic(response);
    }

    private async Task CheckIosAsync()
    {
        if (!TryReadDeferredState(out var done, out _))
        {
            SetDeferredStatus(DeferredLinkStatus.Pending);
            return;
        }
        if (done)
        {
            SetDeferredStatus(DeferredLinkStatus.NotFirstLaunch);
            return;
        }
        if (!_allowMatching)
        {
            // Only the pasted-link flow exists; nothing is stored, so pasting keeps working.
            SetDeferredStatus(DeferredLinkStatus.Unavailable);
            return;
        }

        var mayContainLink = _pasteboardMayContainLink;
        if (mayContainLink is not null)
        {
            var waitForPaste = false;
            try
            {
                var asked = mayContainLink(_disposeToken);
                if (asked is not null) waitForPaste = await asked.ConfigureAwait(false);
            }
            catch (Exception)
            {
                waitForPaste = false;
            }

            if (waitForPaste)
            {
                lock (_gate)
                {
                    if (_disposed || _deferredDone) return; // A paste was handled meanwhile.
                    _awaitingPaste = true;
                }
                SetDeferredStatus(DeferredLinkStatus.AwaitingPaste);
                return;
            }
        }

        await RunMatchingAsync().ConfigureAwait(false);
    }

    /// <summary>Flow B from the attempt counter on.</summary>
    private async Task RunMatchingAsync()
    {
        if (!TryReadDeferredState(out var done, out var attempts))
        {
            SetDeferredStatus(DeferredLinkStatus.Pending);
            return;
        }
        if (done) return; // The pasted link was handled meanwhile; its status stands.

        SetDeferredStatus(DeferredLinkStatus.Checking);
        if (attempts >= MaxDeferredAttempts)
        {
            FinishDeferred(DeferredLinkStatus.Failed);
            return;
        }
        if (!TryWriteDeferred(AttemptsKey, (attempts + 1).ToString(CultureInfo.InvariantCulture)))
        {
            SetDeferredStatus(DeferredLinkStatus.Pending);
            return;
        }
        if (NormalLinkAccepted())
        {
            FinishDeferred(DeferredLinkStatus.Superseded);
            return;
        }

        var family = Ask(_deviceFamily);
        var locale = Ask(_locale);
        if ((family != "iphone" && family != "ipad" && family != "ipod")
            || locale is null
            || !LocalePattern.IsMatch(locale))
        {
            FinishDeferred(DeferredLinkStatus.Unavailable);
            return;
        }

        var body = BuildDeferredBody(writer =>
        {
            writer.WriteString("platform", "ios");
            writer.WriteString("bundleId", _deferredIdentifier);
            writer.WriteString("deviceFamily", family);
            writer.WriteString("locale", locale);
            writer.WriteString("sdkVersion", SdkVersion);
        });
        var response = await PostDeferredAsync(MatchPath, body, probabilistic: true).ConfigureAwait(false);
        FinishAutomatic(response);
    }

    private async Task RunMatchingSafelyAsync()
    {
        try
        {
            await RunMatchingAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            SetDeferredStatus(DeferredLinkStatus.Pending, onlyFrom: DeferredLinkStatus.Checking);
        }
    }

    private void ContinueMatchingIfAwaiting()
    {
        lock (_gate)
        {
            if (_disposed || !_awaitingPaste) return;
            _awaitingPaste = false;
        }

        _ = Task.Run(RunMatchingSafelyAsync);
    }

    /// <summary>The end of both automatic flows, where a normal link wins.</summary>
    private void FinishAutomatic(DeferredResponse response)
    {
        switch (response.Answer)
        {
            case DeferredAnswer.Disposed:
                return;
            case DeferredAnswer.Transient:
                SetDeferredStatus(DeferredLinkStatus.Pending);
                return;
            case DeferredAnswer.NotFound:
                FinishDeferred(DeferredLinkStatus.NotFound);
                return;
            case DeferredAnswer.Unusable:
                FinishDeferred(DeferredLinkStatus.Failed);
                return;
        }

        if (!TryMarkDeferredDone()) return; // The other flow delivered meanwhile; its status stands.
        // "Done" is stored before the link is handed over: a crash after delivery cannot deliver
        // again. If it cannot be stored, the link is dropped rather than risk a second delivery.
        if (!TryWriteDeferred(DoneKey, "1"))
        {
            SetDeferredStatus(DeferredLinkStatus.Failed);
            return;
        }

        switch (DeliverDeferred(response.Link!, normalLinkWins: true))
        {
            case DeferredDelivery.Delivered:
                SetDeferredStatus(DeferredLinkStatus.Delivered);
                break;
            case DeferredDelivery.Superseded:
                SetDeferredStatus(DeferredLinkStatus.Superseded);
                break;
        }
    }

    private async Task<PastedLinkResult> RedeemPastedAfterAsync(Task previous, string? pastedText)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The earlier call never throws; nothing to do if it somehow did.
        }

        try
        {
            return await RedeemPastedCoreAsync(pastedText).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return PastedLinkResult.Failed;
        }
    }

    private async Task<PastedLinkResult> RedeemPastedCoreAsync(string? pastedText)
    {
        lock (_gate)
        {
            if (_disposed || !_deferredRequested || !_deferredPossible || _platform != "ios")
            {
                return PastedLinkResult.Disabled;
            }
        }

        if (!TryReadDeferredState(out var done, out _)) return PastedLinkResult.Failed;
        if (done) return PastedLinkResult.AlreadyHandled;

        var clickId = DeferredLinkParsing.FindClickIdInPastedLink(pastedText, _allowedHosts);
        if (clickId is null)
        {
            // Nothing to paste after all: probabilistic matching, if it was waiting, may start.
            ContinueMatchingIfAwaiting();
            return PastedLinkResult.NotOurs;
        }

        var body = BuildDeferredBody(writer =>
        {
            writer.WriteString("platform", "ios");
            writer.WriteString("clickId", clickId);
            writer.WriteString("bundleId", _deferredIdentifier);
            writer.WriteString("sdkVersion", SdkVersion);
        });
        var response = await PostDeferredAsync(RedeemPath, body, probabilistic: false).ConfigureAwait(false);

        switch (response.Answer)
        {
            case DeferredAnswer.Disposed:
            case DeferredAnswer.Transient:
                return PastedLinkResult.Failed; // "Done" is not stored: the person can paste again.

            case DeferredAnswer.NotFound:
                if (!TryMarkDeferredDone()) return PastedLinkResult.AlreadyHandled;
                TryWriteDeferred(DoneKey, "1");
                SetDeferredStatus(DeferredLinkStatus.NotFound);
                return PastedLinkResult.NotFound;

            case DeferredAnswer.Unusable:
                if (!TryMarkDeferredDone()) return PastedLinkResult.AlreadyHandled;
                TryWriteDeferred(DoneKey, "1");
                SetDeferredStatus(DeferredLinkStatus.Failed);
                return PastedLinkResult.Failed;
        }

        if (!TryMarkDeferredDone()) return PastedLinkResult.AlreadyHandled;
        if (!TryWriteDeferred(DoneKey, "1"))
        {
            SetDeferredStatus(DeferredLinkStatus.Failed);
            return PastedLinkResult.Failed;
        }
        // Pasting was the person's own action: delivered even after a normal link.
        if (DeliverDeferred(response.Link!, normalLinkWins: false) != DeferredDelivery.Delivered)
        {
            return PastedLinkResult.Failed;
        }
        SetDeferredStatus(DeferredLinkStatus.Delivered);
        return PastedLinkResult.Delivered;
    }

    /// <summary>
    /// Asks one source, for at most <see cref="ReferrerSourceTimeout"/>. A source that throws,
    /// returns no task or takes too long is "unavailable for now".
    /// </summary>
    private async Task<InstallReferrerAnswer> AskSourceAsync(InstallReferrerSource? source)
    {
        if (source is null) return InstallReferrerAnswer.Unavailable;

        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(_disposeToken);
        try
        {
            var ask = source(attempt.Token);
            if (ask is null) return InstallReferrerAnswer.UnavailableForNow;
            if (!ask.IsCompleted)
            {
                var timer = _delay(ReferrerSourceTimeout, attempt.Token);
                var winner = await Task.WhenAny(ask, timer).ConfigureAwait(false);
                if (!ReferenceEquals(winner, ask))
                {
                    Observe(ask);
                    return InstallReferrerAnswer.UnavailableForNow;
                }
                Observe(timer);
            }

            return await ask.ConfigureAwait(false);
        }
        catch (Exception)
        {
            return InstallReferrerAnswer.UnavailableForNow;
        }
        finally
        {
            try
            {
                // Stops the timer, or tells the abandoned source to give up.
                attempt.Cancel();
            }
            catch (Exception)
            {
                // A cancellation callback of the source threw; the call is over either way.
            }
        }
    }

    /// <summary>
    /// One redeem or match call with the retries of resolving: timeouts, network errors and 5xx
    /// are retried with the identical body, 429 and other answers are not.
    /// </summary>
    private async Task<DeferredResponse> PostDeferredAsync(string path, byte[] body, bool probabilistic)
    {
        var endpoint = new Uri(_apiBase + path);
        for (var attempt = 0; attempt <= _maxRetries; attempt++)
        {
            HttpResult result;
            try
            {
                if (attempt > 0) await _delay(BackoffBefore(attempt), _disposeToken).ConfigureAwait(false);
                if (_disposeToken.IsCancellationRequested) return new DeferredResponse(DeferredAnswer.Disposed);
                result = await SendWithTimeoutAsync(
                        () => CreateDeferredRequest(endpoint, body),
                        ReadResolveBody,
                        _disposeToken)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                if (_disposeToken.IsCancellationRequested) return new DeferredResponse(DeferredAnswer.Disposed);
                continue; // Timeout or network failure.
            }

            var status = result.StatusCode;
            if (status == 200)
            {
                var link = ParseDeferredResponse(result.Body, probabilistic);
                return link is null ? new DeferredResponse(DeferredAnswer.Unusable) : new DeferredResponse(link);
            }
            if (status == 429) return new DeferredResponse(DeferredAnswer.Transient);
            if (status >= 500) continue;
            // 404 and every other 4xx are the server's refusal; anything else is not an answer
            // of this endpoint at all.
            return new DeferredResponse(status >= 400 ? DeferredAnswer.NotFound : DeferredAnswer.Unusable);
        }

        return new DeferredResponse(DeferredAnswer.Transient);
    }

    private static HttpRequestMessage CreateDeferredRequest(Uri endpoint, byte[] body)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static byte[] BuildDeferredBody(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// A usable answer: the fields of resolving, <c>deferred</c> = true, a <c>url</c> that is a
    /// link of this app and, for a match, <c>matched</c> = "probabilistic". Otherwise <c>null</c>.
    /// </summary>
    private ResolvedDeepLink? ParseDeferredResponse(string body, bool probabilistic)
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
                && !routePath.StartsWith("//", StringComparison.Ordinal)
                && root.TryGetProperty("deferred", out var deferred)
                && deferred.ValueKind == JsonValueKind.True
                && TryGetString(root, "url", out var url)
                // Deferred links are offered only for stored links: the slug rule, whatever
                // ParameterLinks says.
                && LinkUrl.TryParse(url, _allowedHosts, out _)
                && Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && uri is not null
                && (!probabilistic
                    || (TryGetString(root, "matched", out var matched)
                        && string.Equals(matched, "probabilistic", StringComparison.Ordinal))))
            {
                var payload = root.TryGetProperty("payload", out var value) && value.ValueKind == JsonValueKind.Object
                    ? value.Clone()
                    : EmptyPayload;
                var campaign = root.TryGetProperty("campaign", out var campaignValue)
                    ? ParseCampaign(campaignValue)
                    : LinkCampaign.Empty;
                return new ResolvedDeepLink(uri, linkId, appId, routePath, payload, campaign, true, probabilistic);
            }
        }
        catch (JsonException)
        {
            // Not JSON: unusable.
        }

        return null;
    }

    /// <summary>
    /// Hands a deferred link to the normal pipeline: raised, or queued until a handler attaches.
    /// It is never the launch link and no <c>app_open</c> is reported for it.
    /// </summary>
    private DeferredDelivery DeliverDeferred(ResolvedDeepLink link, bool normalLinkWins)
    {
        EventHandler<ResolvedDeepLink>? handlers;
        lock (_gate)
        {
            if (_disposed) return DeferredDelivery.Disposed;
            if (normalLinkWins && _normalLinkAccepted) return DeferredDelivery.Superseded;
            handlers = _handlers;
            if (handlers is null)
            {
                _pending.Add(link);
                return DeferredDelivery.Delivered;
            }
        }

        Raise(handlers, link);
        return DeferredDelivery.Delivered;
    }

    /// <summary>
    /// Reads "done" and the attempt counter. <c>false</c> when the store failed: the caller then
    /// stops, because without the state nothing may be asked or delivered.
    /// </summary>
    private bool TryReadDeferredState(out bool done, out int attempts)
    {
        done = false;
        attempts = 0;
        lock (_gate)
        {
            if (_deferredDone)
            {
                done = true;
                return true;
            }
        }

        try
        {
            var store = _deferredStore!;
            if (!string.IsNullOrEmpty(store.Read(DoneKey)))
            {
                lock (_gate) _deferredDone = true;
                done = true;
                return true;
            }

            var text = store.Read(AttemptsKey);
            if (!string.IsNullOrEmpty(text)
                && !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out attempts))
            {
                attempts = MaxDeferredAttempts; // Not a counter of ours: stop rather than loop.
            }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool TryWriteDeferred(string key, string value)
    {
        try
        {
            _deferredStore!.Write(key, value);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary><c>false</c> when another flow already decided to store "done".</summary>
    private bool TryMarkDeferredDone()
    {
        lock (_gate)
        {
            if (_deferredDone) return false;
            _deferredDone = true;
            _awaitingPaste = false;
            return true;
        }
    }

    /// <summary>Stores "done" and sets a final status, unless another flow finished first.</summary>
    private void FinishDeferred(DeferredLinkStatus status)
    {
        if (!TryMarkDeferredDone()) return;
        TryWriteDeferred(DoneKey, "1");
        SetDeferredStatus(status);
    }

    private void SetDeferredStatus(DeferredLinkStatus status, DeferredLinkStatus? onlyFrom = null)
    {
        EventHandler<DeferredLinkStatus>? handlers;
        lock (_gate)
        {
            if (_deferredStatus == status) return;
            if (onlyFrom.HasValue && _deferredStatus != onlyFrom.GetValueOrDefault()) return;
            _deferredStatus = status;
            if (_disposed) return;
            handlers = _statusHandlers;
        }

        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<DeferredLinkStatus>)handler)(this, status);
            }
            catch (Exception error)
            {
                Debug.WriteLine("Strybok: a DeferredLinkStatusChanged handler threw: " + error);
            }
        }
    }

    private bool NormalLinkAccepted()
    {
        lock (_gate) return _normalLinkAccepted;
    }

    private bool IsDisposed()
    {
        lock (_gate) return _disposed;
    }

    private static string? Ask(Func<string?>? callback)
    {
        if (callback is null) return null;
        try
        {
            return callback();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? NormalizeIdentifier(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private readonly struct DeferredResponse
    {
        public DeferredResponse(DeferredAnswer answer)
        {
            Answer = answer;
            Link = null;
        }

        public DeferredResponse(ResolvedDeepLink link)
        {
            Answer = DeferredAnswer.Link;
            Link = link;
        }

        public DeferredAnswer Answer { get; }

        public ResolvedDeepLink? Link { get; }
    }
}
