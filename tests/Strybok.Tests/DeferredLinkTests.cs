using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Strybok.Tests;

/// <summary>A persistent store as the app would supply it; survives a "restart" by being reused.</summary>
internal sealed class MemoryStore : IDeferredLinkStore
{
    public const string Attempts = "strybok.deferred.attempts";
    public const string Done = "strybok.deferred.done";

    private readonly object _gate = new object();
    private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.Ordinal);
    private readonly List<string> _writes = new List<string>();

    public bool FailReads { get; set; }

    /// <summary>Writes of this key throw.</summary>
    public string? FailWritesOf { get; set; }

    /// <summary>Every write as <c>key=value</c>, in order.</summary>
    public IReadOnlyList<string> Writes
    {
        get
        {
            lock (_gate) return _writes.ToArray();
        }
    }

    public IReadOnlyDictionary<string, string> Values
    {
        get
        {
            lock (_gate) return new Dictionary<string, string>(_values, StringComparer.Ordinal);
        }
    }

    public string? Get(string key)
    {
        lock (_gate) return _values.TryGetValue(key, out var value) ? value : null;
    }

    public MemoryStore With(string key, string value)
    {
        lock (_gate) _values[key] = value;
        return this;
    }

    public string? Read(string key)
    {
        if (FailReads) throw new InvalidOperationException("storage is broken");
        return Get(key);
    }

    public void Write(string key, string value)
    {
        if (FailWritesOf == key) throw new InvalidOperationException("storage is full");
        lock (_gate)
        {
            _values[key] = value;
            _writes.Add(key + "=" + value);
        }
    }
}

public sealed class DeferredLinkTests
{
    private const string ClickId = "dc_0123456789abcdefghijABCDEFGHIJ01";
    private const string OtherClickId = "dc_ZZZZ456789abcdefghijABCDEFGHIJ99";
    private const string Package = "com.example.app";
    private const string Bundle = "com.example.App";
    private const string RedeemUrl = "https://api.strybok.com/v1/public/deferred-links/redeem";
    private const string MatchUrl = "https://api.strybok.com/v1/public/deferred-links/match";
    private const string PastedUrl = Links.InviteUrl + "?strybok_click=" + ClickId;

    private const string LinkBody =
        "{\"linkId\":\"link_d\",\"appId\":\"app_1\",\"routePath\":\"/invite/deferred\","
        + "\"payload\":{\"invitationId\":\"abc123\"},\"campaign\":{\"source\":\"instagram\",\"other\":\"x\"},"
        + "\"url\":\"https://my-app.strybok.link/invite777\",\"deferred\":true,\"somethingNew\":1}";

    private const string MatchBody =
        "{\"linkId\":\"link_m\",\"appId\":\"app_1\",\"routePath\":\"/promo\","
        + "\"url\":\"https://my-app.strybok.link/promo\",\"deferred\":true,\"matched\":\"probabilistic\"}";

    private static InstallReferrerSource Source(InstallReferrerAnswer answer, List<string>? calls = null, string name = "a") =>
        _ =>
        {
            if (calls is not null)
            {
                lock (calls) calls.Add(name);
            }
            return Task.FromResult(answer);
        };

    private static InstallReferrerSource Referrer(string referrer = "utm_source=x&strybok_click=" + ClickId) =>
        Source(InstallReferrerAnswer.Referrer(referrer));

    private static Harness Android(MemoryStore? store, params InstallReferrerSource[] sources) =>
        Android(store, null, sources);

    private static Harness Android(MemoryStore? store, FakeLinkSource? linkSource, params InstallReferrerSource[] sources)
    {
        var harness = new Harness(linkSource, options =>
        {
            options.Platform = StrybokPlatform.Android;
            options.DeferredLinks = true;
            options.AndroidPackageName = Package;
            options.DeferredLinkStore = store;
            foreach (var source in sources) options.InstallReferrerSources.Add(source);
        });
        harness.Api.OnDeferred = (_, _) => Task.FromResult(Responses.Json(HttpStatusCode.OK, LinkBody));
        return harness;
    }

    private static Harness Ios(MemoryStore? store, Action<StrybokOptions>? configure = null, FakeLinkSource? linkSource = null)
    {
        var harness = new Harness(linkSource, options =>
        {
            options.Platform = StrybokPlatform.iOS;
            options.DeferredLinks = true;
            options.IosBundleId = Bundle;
            options.DeferredLinkStore = store;
            configure?.Invoke(options);
        });
        harness.Api.OnDeferred = (request, _) => Task.FromResult(
            Responses.Json(HttpStatusCode.OK, request.Uri.AbsolutePath.EndsWith("/match", StringComparison.Ordinal) ? MatchBody : LinkBody));
        return harness;
    }

    private static Action<StrybokOptions> Matching(Action<StrybokOptions>? more = null) =>
        options =>
        {
            options.AllowProbabilisticMatching = true;
            options.DeviceFamily = () => "iphone";
            options.Locale = () => "en-GB";
            more?.Invoke(options);
        };

    private static Task Status(Harness harness, DeferredLinkStatus status) =>
        Async.Eventually(() => harness.Client.DeferredLinkStatus == status, "status " + status + ", is " + harness.Client.DeferredLinkStatus);

    /// <summary>Starts the client and waits until the check has reached <paramref name="status"/>.</summary>
    private static async Task<Harness> Started(Harness harness, DeferredLinkStatus status)
    {
        // "Pending" is also the status before the check starts: wait until it has run.
        var ran = 0;
        harness.Client.DeferredLinkStatusChanged += (_, changed) =>
        {
            if (changed == DeferredLinkStatus.Checking) Interlocked.Exchange(ref ran, 1);
        };
        await harness.Client.InitializeAsync();
        if (status == DeferredLinkStatus.Pending)
        {
            await Async.Eventually(() => Volatile.Read(ref ran) == 1, "the check to start");
        }
        await Status(harness, status);
        return harness;
    }

    // ------------------------------------------------------------------ switched off

    [Fact]
    public async Task Off_by_default_nothing_is_asked_sent_or_stored()
    {
        var store = new MemoryStore();
        var calls = new List<string>();
        await using var harness = new Harness(null, options =>
        {
            options.Platform = StrybokPlatform.Android;
            options.AndroidPackageName = Package;
            options.IosBundleId = Bundle;
            options.DeferredLinkStore = store;
            options.InstallReferrerSources.Add(Source(InstallReferrerAnswer.Referrer("strybok_click=" + ClickId), calls));
        });
        harness.Listen();

        Assert.Equal(DeferredLinkStatus.Disabled, harness.Client.DeferredLinkStatus);
        await harness.Client.InitializeAsync();
        await Async.Settle();

        Assert.Equal(DeferredLinkStatus.Disabled, harness.Client.DeferredLinkStatus);
        Assert.Empty(calls);
        Assert.Empty(harness.Api.Deferred);
        Assert.Empty(store.Writes);
        Assert.Empty(harness.Received);
        Assert.Equal(PastedLinkResult.Disabled, await harness.Client.RedeemPastedLinkAsync(PastedUrl));
        Assert.Empty(harness.Api.Deferred);
    }

    [Fact]
    public async Task Without_a_store_it_stays_disabled()
    {
        var calls = new List<string>();
        await using var harness = Android(null, Source(InstallReferrerAnswer.Referrer("strybok_click=" + ClickId), calls));
        await harness.Client.InitializeAsync();
        harness.Client.EnableDeferredLinks();
        await Async.Settle();

        Assert.Equal(DeferredLinkStatus.Disabled, harness.Client.DeferredLinkStatus);
        Assert.Empty(calls);
        Assert.Empty(harness.Api.Deferred);

        await using var ios = Ios(null);
        await ios.Client.InitializeAsync();
        Assert.Equal(PastedLinkResult.Disabled, await ios.Client.RedeemPastedLinkAsync(PastedUrl));
        Assert.Equal(DeferredLinkStatus.Disabled, ios.Client.DeferredLinkStatus);
        Assert.Empty(ios.Api.Deferred);
    }

    [Theory]
    [InlineData(StrybokPlatform.Android, null)]
    [InlineData(StrybokPlatform.Android, "  ")]
    [InlineData(StrybokPlatform.iOS, null)]
    [InlineData(StrybokPlatform.Unknown, "com.example.app")]
    public async Task Without_the_identifier_or_on_another_platform_it_stays_disabled(StrybokPlatform platform, string? identifier)
    {
        var store = new MemoryStore();
        var calls = new List<string>();
        await using var harness = new Harness(null, options =>
        {
            options.Platform = platform;
            options.DeferredLinks = true;
            options.AllowProbabilisticMatching = true;
            options.AndroidPackageName = identifier;
            options.IosBundleId = identifier;
            options.DeferredLinkStore = store;
            options.InstallReferrerSources.Add(Source(InstallReferrerAnswer.Referrer("strybok_click=" + ClickId), calls));
        });
        await harness.Client.InitializeAsync();
        await Async.Settle();

        Assert.Equal(DeferredLinkStatus.Disabled, harness.Client.DeferredLinkStatus);
        Assert.Equal(PastedLinkResult.Disabled, await harness.Client.RedeemPastedLinkAsync(PastedUrl));
        Assert.Empty(calls);
        Assert.Empty(harness.Api.Deferred);
        Assert.Empty(store.Writes);
    }

    // ------------------------------------------------------------------ Android: the flow

    [Fact]
    public async Task Android_redeems_the_click_id_and_delivers_the_link_once()
    {
        var store = new MemoryStore();
        await using var harness = Android(store, Referrer());
        var statuses = new List<DeferredLinkStatus>();
        harness.Client.DeferredLinkStatusChanged += (sender, status) =>
        {
            Assert.Same(harness.Client, sender);
            lock (statuses) statuses.Add(status);
        };
        harness.Listen();

        Assert.Equal(DeferredLinkStatus.Pending, harness.Client.DeferredLinkStatus);
        await Started(harness, DeferredLinkStatus.Delivered);

        var request = Assert.Single(harness.Api.Deferred);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(RedeemUrl, request.Uri.AbsoluteUri);
        Assert.Equal("application/json", request.ContentType);
        Assert.Equal("application/json", request.Headers["accept"]);
        Assert.Equal(
            "{\"platform\":\"android\",\"clickId\":\"" + ClickId + "\",\"packageName\":\"com.example.app\",\"sdkVersion\":\""
                + StrybokClient.SdkVersion + "\"}",
            request.Body);

        var link = Assert.Single(harness.Received);
        Assert.True(link.IsDeferred);
        Assert.False(link.IsProbabilisticMatch);
        Assert.Equal("https://my-app.strybok.link/invite777", link.Uri.OriginalString);
        Assert.Equal("link_d", link.LinkId);
        Assert.Equal("app_1", link.AppId);
        Assert.Equal("/invite/deferred", link.RoutePath);
        Assert.Equal("abc123", link.GetPayloadString("invitationId"));
        Assert.Equal("instagram", link.Campaign.Source);
        Assert.Null(link.Campaign.Medium);

        Assert.Equal(new[] { "strybok.deferred.attempts=1", "strybok.deferred.done=1" }, store.Writes);
        lock (statuses) Assert.Equal(new[] { DeferredLinkStatus.Checking, DeferredLinkStatus.Delivered }, statuses);

        await Async.Settle();
        Assert.Empty(harness.Api.Events); // No app_open for a deferred link.
        Assert.Empty(harness.Api.Resolves);
        Assert.Empty(harness.Errors);
        Assert.Single(harness.Received);
    }

    [Fact]
    public async Task Normal_links_are_not_deferred()
    {
        await using var harness = new Harness();
        harness.Source.InitialLink = Links.Invite;
        harness.Listen();
        await harness.Client.InitializeAsync();

        var link = Assert.Single(harness.Received);
        Assert.False(link.IsDeferred);
        Assert.False(link.IsProbabilisticMatch);
        var resolved = await harness.Client.ResolveAsync(Links.Invite);
        Assert.False(resolved.IsDeferred);
        Assert.False(resolved.IsProbabilisticMatch);
    }

    [Fact]
    public async Task Start_completes_while_a_source_blocks_and_the_attempt_is_stored_first()
    {
        var store = new MemoryStore();
        string? attemptsWhenAsked = null;
        var asked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<InstallReferrerAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = Android(store, _ =>
        {
            attemptsWhenAsked = store.Get(MemoryStore.Attempts);
            asked.TrySetResult(true);
            return release.Task;
        });
        harness.Source.InitialLink = null;

        await harness.Client.InitializeAsync(); // Does not wait for the source.
        Assert.Null(await harness.Client.GetInitialLinkAsync());
        await asked.Task;

        Assert.Equal("1", attemptsWhenAsked);
        Assert.Equal(DeferredLinkStatus.Checking, harness.Client.DeferredLinkStatus);
        Assert.Null(store.Get(MemoryStore.Done));
        Assert.Empty(harness.Api.Deferred);

        release.SetResult(InstallReferrerAnswer.Referrer("strybok_click=" + ClickId));
        await Status(harness, DeferredLinkStatus.Delivered);
    }

    [Fact]
    public async Task Three_attempts_then_failed()
    {
        var store = new MemoryStore();
        var calls = new List<string>();
        for (var launch = 1; launch <= 3; launch++)
        {
            await using var harness = Android(store, Source(InstallReferrerAnswer.UnavailableForNow, calls));
            await Started(harness, DeferredLinkStatus.Pending);
            Assert.Equal(launch.ToString(), store.Get(MemoryStore.Attempts));
            Assert.Null(store.Get(MemoryStore.Done));
        }

        await using var fourth = Android(store, Source(InstallReferrerAnswer.UnavailableForNow, calls));
        await Started(fourth, DeferredLinkStatus.Failed);
        Assert.Equal(3, calls.Count); // Not asked a fourth time.
        Assert.Equal("1", store.Get(MemoryStore.Done));
        Assert.Equal("3", store.Get(MemoryStore.Attempts));

        await using var fifth = Android(store, Source(InstallReferrerAnswer.UnavailableForNow, calls));
        await Started(fifth, DeferredLinkStatus.NotFirstLaunch);
        Assert.Equal(3, calls.Count);
        Assert.Empty(fifth.Api.Deferred);
    }

    [Fact]
    public async Task A_counter_that_is_not_ours_ends_the_check()
    {
        var store = new MemoryStore().With(MemoryStore.Attempts, "many");
        var calls = new List<string>();
        await using var harness = Android(store, Source(InstallReferrerAnswer.UnavailableForNow, calls));
        await Started(harness, DeferredLinkStatus.Failed);
        Assert.Empty(calls);
        Assert.Equal("1", store.Get(MemoryStore.Done));
    }

    [Fact]
    public async Task Done_in_an_earlier_launch_is_not_first_launch()
    {
        var store = new MemoryStore().With(MemoryStore.Done, "1");
        var calls = new List<string>();
        await using var harness = Android(store, Source(InstallReferrerAnswer.Referrer("strybok_click=" + ClickId), calls));
        await Started(harness, DeferredLinkStatus.NotFirstLaunch);
        await Async.Settle();
        Assert.Empty(calls);
        Assert.Empty(harness.Api.Deferred);
        Assert.Empty(store.Writes);
    }

    [Fact]
    public async Task No_sources_is_unavailable()
    {
        var store = new MemoryStore();
        await using var harness = Android(store);
        await Started(harness, DeferredLinkStatus.Unavailable);
        Assert.Equal("1", store.Get(MemoryStore.Done));
        Assert.Empty(harness.Api.Deferred);
    }

    [Fact]
    public async Task Sources_that_can_never_answer_are_unavailable()
    {
        var store = new MemoryStore();
        await using var harness = Android(store, Source(InstallReferrerAnswer.Unavailable), Source(InstallReferrerAnswer.Unavailable));
        await Started(harness, DeferredLinkStatus.Unavailable);
        Assert.Equal("1", store.Get(MemoryStore.Done));
    }

    [Fact]
    public async Task A_referrer_without_our_id_is_no_referrer()
    {
        var store = new MemoryStore();
        await using var harness = Android(
            store,
            Source(InstallReferrerAnswer.Unavailable),
            Source(InstallReferrerAnswer.Referrer("utm_source=google-play&utm_medium=organic")));
        await Started(harness, DeferredLinkStatus.NoReferrer);
        Assert.Equal("1", store.Get(MemoryStore.Done));
        Assert.Empty(harness.Api.Deferred);
    }

    [Fact]
    public async Task Unavailable_for_now_keeps_the_check_pending_even_next_to_a_referrer_without_id()
    {
        var store = new MemoryStore();
        await using var harness = Android(
            store,
            Source(InstallReferrerAnswer.Referrer(string.Empty)),
            Source(InstallReferrerAnswer.UnavailableForNow),
            Source(InstallReferrerAnswer.Unavailable));
        await Started(harness, DeferredLinkStatus.Pending);
        Assert.Null(store.Get(MemoryStore.Done));
        Assert.Empty(harness.Api.Deferred);
    }

    [Fact]
    public async Task Sources_are_asked_in_order_and_the_first_id_wins()
    {
        var calls = new List<string>();
        await using var harness = Android(
            new MemoryStore(),
            Source(InstallReferrerAnswer.Unavailable, calls, "play"),
            Source(InstallReferrerAnswer.Referrer("utm_source=x"), calls, "galaxy"),
            Source(InstallReferrerAnswer.Referrer("strybok_click=" + ClickId), calls, "appgallery"),
            Source(InstallReferrerAnswer.Referrer("strybok_click=" + OtherClickId), calls, "fourth"));
        await Started(harness, DeferredLinkStatus.Delivered);

        Assert.Equal(new[] { "play", "galaxy", "appgallery" }, calls);
        Assert.Contains(ClickId, Assert.Single(harness.Api.Deferred).Body);
    }

    [Fact]
    public async Task A_source_slower_than_ten_seconds_is_unavailable_for_now_and_is_abandoned()
    {
        var store = new MemoryStore();
        var token = default(CancellationToken);
        var asked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = Android(store, cancellationToken =>
        {
            token = cancellationToken;
            asked.TrySetResult(true);
            return new TaskCompletionSource<InstallReferrerAnswer>().Task;
        });
        harness.Delay.FireSourceTimeouts = true;

        await Started(harness, DeferredLinkStatus.Pending);
        await asked.Task;
        await Async.Eventually(() => token.IsCancellationRequested, "the source is told to give up");
        Assert.Null(store.Get(MemoryStore.Done));
        Assert.Empty(harness.Api.Deferred);
    }

    [Fact]
    public async Task A_source_that_throws_is_unavailable_for_now()
    {
        var store = new MemoryStore();
        await using var harness = Android(
            store,
            _ => throw new InvalidOperationException("boom"),
            _ => Task.FromException<InstallReferrerAnswer>(new InvalidOperationException("boom")),
            _ => null!);
        await Started(harness, DeferredLinkStatus.Pending);
        Assert.Null(store.Get(MemoryStore.Done));
        Assert.Empty(harness.Errors);
    }

    // ------------------------------------------------------------------ Android: the referrer

    [Theory]
    [InlineData("strybok_click=dc_0123456789abcdefghijABCDEFGHIJ01")]
    [InlineData("utm_source=x&strybok_click=dc_0123456789abcdefghijABCDEFGHIJ01&utm_medium=y")]
    [InlineData("&&strybok_click=dc_0123456789abcdefghijABCDEFGHIJ01&strybok_click=dc_ZZZZ456789abcdefghijABCDEFGHIJ99")]
    [InlineData("xstrybok_click=dc_ZZZZ456789abcdefghijABCDEFGHIJ99&strybok_click=dc_0123456789abcdefghijABCDEFGHIJ01")]
    [InlineData("strybok_click%3Ddc_0123456789abcdefghijABCDEFGHIJ01%26utm_source%3Dx")]
    [InlineData("strybok_click%3ddc_0123456789abcdefghijABCDEFGHIJ01")]
    [InlineData("utm_source%3Dx%26strybok_click%3Ddc_0123456789abcdefghijABCDEFGHIJ01")]
    public async Task The_click_id_is_found(string referrer)
    {
        await using var harness = Android(new MemoryStore(), Referrer(referrer));
        await Started(harness, DeferredLinkStatus.Delivered);
        Assert.Contains("\"clickId\":\"" + ClickId + "\"", Assert.Single(harness.Api.Deferred).Body);
    }

    [Theory]
    [InlineData("")]
    [InlineData("strybok_click")]
    [InlineData("strybok_click=")]
    [InlineData("strybok_click=dc_short")]
    [InlineData("strybok_click=dc_0123456789abcdefghijABCDEFGHIJ0!")]
    [InlineData("strybok_click=dc_0123456789abcdefghijABCDEFGHIJ012")]
    [InlineData("strybok_click=DC_0123456789abcdefghijABCDEFGHIJ01")]
    [InlineData("Strybok_Click=dc_0123456789abcdefghijABCDEFGHIJ01")]
    [InlineData("xstrybok_click=dc_0123456789abcdefghijABCDEFGHIJ01")]
    [InlineData("strybok_click_x=dc_0123456789abcdefghijABCDEFGHIJ01")]
    // The first part with the key decides, even when a later one is valid.
    [InlineData("strybok_click=nope&strybok_click=dc_0123456789abcdefghijABCDEFGHIJ01")]
    // A part with the key exists, so the still-encoded form later in the string is not tried.
    [InlineData("strybok_click=nope&x=strybok_click%3Ddc_0123456789abcdefghijABCDEFGHIJ01")]
    // Still encoded, with the key in another letter case: decoded, and then not our key.
    [InlineData("STRYBOK_CLICK%3Ddc_0123456789abcdefghijABCDEFGHIJ01")]
    // Still encoded, but malformed.
    [InlineData("strybok_click%3Ddc_0123456789abcdefghijABCDEFGHIJ01%2")]
    [InlineData("strybok_click%3Ddc_0123456789abcdefghijABCDEFGHIJ01%zz")]
    // Encoded twice: decoded once only.
    [InlineData("strybok_click%253Ddc_0123456789abcdefghijABCDEFGHIJ01")]
    [InlineData("strybok_click%3Ddc_short")]
    public async Task No_click_id_is_no_referrer_and_never_an_error(string referrer)
    {
        var store = new MemoryStore();
        await using var harness = Android(store, Referrer(referrer));
        await Started(harness, DeferredLinkStatus.NoReferrer);
        Assert.Empty(harness.Api.Deferred);
        Assert.Empty(harness.Errors);
        Assert.Equal("1", store.Get(MemoryStore.Done));
    }

    // ------------------------------------------------------------------ Android: the request

    [Fact]
    public async Task Server_errors_and_network_failures_are_retried_with_the_identical_body()
    {
        var store = new MemoryStore();
        await using var harness = Android(store, Referrer());
        var calls = 0;
        harness.Api.OnDeferred = (_, _) =>
        {
            var call = Interlocked.Increment(ref calls);
            if (call == 1) return Task.FromResult(Responses.Status(503));
            if (call == 2) throw new HttpRequestException("offline");
            return Task.FromResult(Responses.Json(HttpStatusCode.OK, LinkBody));
        };
        harness.Listen();
        await Started(harness, DeferredLinkStatus.Delivered);

        var requests = harness.Api.Deferred;
        Assert.Equal(3, requests.Count);
        Assert.Equal(requests[0].Body, requests[1].Body);
        Assert.Equal(requests[0].Body, requests[2].Body);
        Assert.Equal(new[] { TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(800) }, harness.Delay.RetryPauses);
        Assert.Single(harness.Received);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(503)]
    public async Task Server_errors_after_all_retries_leave_the_check_pending(int status)
    {
        var store = new MemoryStore();
        await using var harness = Android(store, Referrer());
        harness.Api.OnDeferred = (_, _) => Task.FromResult(Responses.Status(status));
        await Started(harness, DeferredLinkStatus.Pending);

        Assert.Equal(3, harness.Api.Deferred.Count);
        Assert.Null(store.Get(MemoryStore.Done));
        Assert.Equal("1", store.Get(MemoryStore.Attempts));
        Assert.Empty(harness.Errors);

        // The next launch tries again with the same id.
        await using var next = Android(store, Referrer());
        next.Listen();
        await Started(next, DeferredLinkStatus.Delivered);
        Assert.Equal("2", store.Get(MemoryStore.Attempts));
    }

    [Fact]
    public async Task Timeouts_are_retried_and_leave_the_check_pending()
    {
        var store = new MemoryStore();
        await using var harness = Android(store, Referrer());
        harness.Api.OnDeferred = (_, cancellationToken) => Async.Never<HttpResponseMessage>(cancellationToken);
        harness.Delay.FireTimeouts = true;
        await Started(harness, DeferredLinkStatus.Pending);
        Assert.Equal(3, harness.Api.Deferred.Count);
        Assert.Null(store.Get(MemoryStore.Done));
    }

    [Fact]
    public async Task Rate_limited_is_pending_without_a_retry()
    {
        var store = new MemoryStore();
        await using var harness = Android(store, Referrer());
        harness.Api.OnDeferred = (_, _) => Task.FromResult(Responses.Status(429));
        await Started(harness, DeferredLinkStatus.Pending);
        await Async.Settle();
        Assert.Single(harness.Api.Deferred);
        Assert.Null(store.Get(MemoryStore.Done));
    }

    [Theory]
    [InlineData(404)]
    [InlineData(400)]
    [InlineData(410)]
    [InlineData(422)]
    public async Task A_refusal_is_not_found_and_final(int status)
    {
        var store = new MemoryStore();
        await using var harness = Android(store, Referrer());
        harness.Api.OnDeferred = (_, _) => Task.FromResult(Responses.Status(status));
        harness.Listen();
        await Started(harness, DeferredLinkStatus.NotFound);
        Assert.Single(harness.Api.Deferred);
        Assert.Equal("1", store.Get(MemoryStore.Done));
        Assert.Empty(harness.Received);
        Assert.Empty(harness.Errors);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    // deferred missing, false, or not a boolean
    [InlineData("{\"linkId\":\"l\",\"appId\":\"a\",\"routePath\":\"/r\",\"url\":\"https://my-app.strybok.link/invite777\"}")]
    [InlineData("{\"linkId\":\"l\",\"appId\":\"a\",\"routePath\":\"/r\",\"url\":\"https://my-app.strybok.link/invite777\",\"deferred\":false}")]
    [InlineData("{\"linkId\":\"l\",\"appId\":\"a\",\"routePath\":\"/r\",\"url\":\"https://my-app.strybok.link/invite777\",\"deferred\":\"true\"}")]
    // url missing, on a foreign host, or not a link of ours
    [InlineData("{\"linkId\":\"l\",\"appId\":\"a\",\"routePath\":\"/r\",\"deferred\":true}")]
    [InlineData("{\"linkId\":\"l\",\"appId\":\"a\",\"routePath\":\"/r\",\"url\":\"https://evil.example/invite777\",\"deferred\":true}")]
    [InlineData("{\"linkId\":\"l\",\"appId\":\"a\",\"routePath\":\"/r\",\"url\":\"http://my-app.strybok.link/invite777\",\"deferred\":true}")]
    [InlineData("{\"linkId\":\"l\",\"appId\":\"a\",\"routePath\":\"/r\",\"url\":\"https://my-app.strybok.link/a/b\",\"deferred\":true}")]
    // the fields of resolving
    [InlineData("{\"appId\":\"a\",\"routePath\":\"/r\",\"url\":\"https://my-app.strybok.link/invite777\",\"deferred\":true}")]
    [InlineData("{\"linkId\":\"l\",\"routePath\":\"/r\",\"url\":\"https://my-app.strybok.link/invite777\",\"deferred\":true}")]
    [InlineData("{\"linkId\":\"l\",\"appId\":\"a\",\"routePath\":\"r\",\"url\":\"https://my-app.strybok.link/invite777\",\"deferred\":true}")]
    [InlineData("{\"linkId\":\"l\",\"appId\":\"a\",\"routePath\":\"//evil.example\",\"url\":\"https://my-app.strybok.link/invite777\",\"deferred\":true}")]
    public async Task An_unusable_answer_is_failed_and_nothing_is_delivered(string body)
    {
        var store = new MemoryStore();
        await using var harness = Android(store, Referrer());
        harness.Api.OnDeferred = (_, _) => Task.FromResult(Responses.Json(HttpStatusCode.OK, body));
        harness.Listen();
        await Started(harness, DeferredLinkStatus.Failed);
        await Async.Settle();
        Assert.Single(harness.Api.Deferred);
        Assert.Equal("1", store.Get(MemoryStore.Done));
        Assert.Empty(harness.Received);
        Assert.Empty(harness.Errors);
    }

    // ------------------------------------------------------------------ a normal link wins

    [Fact]
    public async Task The_launch_link_wins_and_no_request_is_made()
    {
        var store = new MemoryStore();
        var source = new FakeLinkSource { InitialLink = Links.Invite };
        await using var harness = Android(store, source, Referrer());
        harness.Listen();
        await Started(harness, DeferredLinkStatus.Superseded);
        await Async.Settle();

        Assert.Empty(harness.Api.Deferred);
        Assert.Equal("1", store.Get(MemoryStore.Done));
        var link = Assert.Single(harness.Received);
        Assert.False(link.IsDeferred);
    }

    [Fact]
    public async Task A_launch_link_that_failed_to_resolve_still_wins()
    {
        var store = new MemoryStore();
        var source = new FakeLinkSource { InitialLink = Links.Invite };
        await using var harness = Android(store, source, Referrer());
        harness.Api.OnResolve = (_, _) => Task.FromResult(Responses.Status(404));
        await Started(harness, DeferredLinkStatus.Superseded);
        Assert.Empty(harness.Api.Deferred);
        Assert.Single(harness.Errors); // The normal link's own error, nothing from the check.
    }

    [Fact]
    public async Task A_link_that_is_not_ours_does_not_supersede()
    {
        var source = new FakeLinkSource { InitialLink = new Uri("https://elsewhere.example/invite777") };
        await using var harness = Android(new MemoryStore(), source, Referrer());
        harness.Listen();
        await Started(harness, DeferredLinkStatus.Delivered);
        Assert.True(Assert.Single(harness.Received).IsDeferred);
    }

    [Fact]
    public async Task A_link_arriving_while_the_request_is_in_flight_wins()
    {
        var store = new MemoryStore();
        await using var harness = Android(store, Referrer());
        var answer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Api.OnDeferred = (_, _) => answer.Task;
        harness.Listen();
        await harness.Client.InitializeAsync();
        await Async.Eventually(() => harness.Api.Deferred.Count == 1, "the redeem request");

        harness.Source.Emit(Links.Invite);
        await harness.ReceivedCount(1);
        answer.SetResult(Responses.Json(HttpStatusCode.OK, LinkBody));

        await Status(harness, DeferredLinkStatus.Superseded);
        await Async.Settle();
        Assert.False(Assert.Single(harness.Received).IsDeferred); // Dropped, not queued behind it.
        Assert.Equal("1", store.Get(MemoryStore.Done));
    }

    // ------------------------------------------------------------------ delivery

    [Fact]
    public async Task Done_is_stored_before_the_link_is_delivered()
    {
        var store = new MemoryStore();
        await using var harness = Android(store, Referrer());
        string? doneAtDelivery = null;
        var statusAtDelivery = DeferredLinkStatus.Disabled;
        var delivered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Client.LinkReceived += (_, _) =>
        {
            doneAtDelivery = store.Get(MemoryStore.Done);
            statusAtDelivery = harness.Client.DeferredLinkStatus;
            delivered.TrySetResult(true);
        };
        await harness.Client.InitializeAsync();
        await delivered.Task;

        Assert.Equal("1", doneAtDelivery);
        Assert.Equal(DeferredLinkStatus.Checking, statusAtDelivery);
        await Status(harness, DeferredLinkStatus.Delivered);
    }

    [Fact]
    public async Task It_is_queued_until_a_handler_attaches_and_is_never_the_initial_link()
    {
        await using var harness = Android(new MemoryStore(), Referrer());
        await Started(harness, DeferredLinkStatus.Delivered);

        Assert.Null(await harness.Client.GetInitialLinkAsync());
        Assert.Null(await harness.Client.GetInitialLinkAsync());
        Assert.Empty(harness.Received);

        harness.Listen();
        Assert.True(Assert.Single(harness.Received).IsDeferred);
        await Async.Settle();
        Assert.Empty(harness.Api.Events);
    }

    [Fact]
    public async Task After_delivery_a_restart_does_nothing()
    {
        var store = new MemoryStore();
        await using (var first = Android(store, Referrer()))
        {
            first.Listen();
            await Started(first, DeferredLinkStatus.Delivered);
        }

        var calls = new List<string>();
        await using var second = Android(store, Source(InstallReferrerAnswer.Referrer("strybok_click=" + ClickId), calls));
        second.Listen();
        await Started(second, DeferredLinkStatus.NotFirstLaunch);
        await Async.Settle();
        Assert.Empty(calls);
        Assert.Empty(second.Api.Deferred);
        Assert.Empty(second.Received);
    }

    [Fact]
    public async Task A_launch_killed_while_the_source_is_asked_has_used_one_attempt()
    {
        var store = new MemoryStore();
        await using (var first = Android(store, _ => new TaskCompletionSource<InstallReferrerAnswer>().Task))
        {
            await Started(first, DeferredLinkStatus.Checking);
            await Async.Eventually(() => store.Get(MemoryStore.Attempts) == "1", "the attempt");
        }

        Assert.Null(store.Get(MemoryStore.Done));
        await using var second = Android(store, Referrer());
        second.Listen();
        await Started(second, DeferredLinkStatus.Delivered);
        Assert.Equal("2", store.Get(MemoryStore.Attempts));
    }

    [Fact]
    public async Task A_launch_killed_during_the_request_sends_the_same_id_again()
    {
        var store = new MemoryStore();
        await using (var first = Android(store, Referrer()))
        {
            first.Api.OnDeferred = (_, cancellationToken) => Async.Never<HttpResponseMessage>(cancellationToken);
            first.Listen();
            await first.Client.InitializeAsync();
            await Async.Eventually(() => first.Api.Deferred.Count == 1, "the redeem request");
            await first.Client.DisposeAsync();
            await Async.Settle();
            Assert.Empty(first.Received);
            Assert.Null(store.Get(MemoryStore.Done));
        }

        await using var second = Android(store, Referrer());
        second.Listen();
        await Started(second, DeferredLinkStatus.Delivered);
        Assert.Contains(ClickId, Assert.Single(second.Api.Deferred).Body);
    }

    [Fact]
    public async Task Nothing_is_delivered_after_dispose()
    {
        var store = new MemoryStore();
        var harness = Android(store, Referrer());
        var answer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = 0;
        harness.Client.LinkReceived += (_, _) => Interlocked.Increment(ref received);
        // The transport ignores the abort and answers after the client was disposed.
        harness.Api.OnDeferred = (_, _) => answer.Task;
        await harness.Client.InitializeAsync();
        await Async.Eventually(() => harness.Api.Deferred.Count == 1, "the redeem request");

        await harness.DisposeAsync();
        answer.SetResult(Responses.Json(HttpStatusCode.OK, LinkBody));
        await Async.Settle();

        Assert.Equal(0, received);
    }

    // ------------------------------------------------------------------ switching on later

    [Fact]
    public async Task EnableDeferredLinks_after_start_runs_the_check_once()
    {
        var store = new MemoryStore();
        await using var harness = new Harness(null, options =>
        {
            options.Platform = StrybokPlatform.Android;
            options.AndroidPackageName = Package;
            options.DeferredLinkStore = store;
            options.InstallReferrerSources.Add(Referrer());
        });
        harness.Api.OnDeferred = (_, _) => Task.FromResult(Responses.Json(HttpStatusCode.OK, LinkBody));
        harness.Listen();
        await harness.Client.InitializeAsync();
        await Async.Settle();
        Assert.Equal(DeferredLinkStatus.Disabled, harness.Client.DeferredLinkStatus);
        Assert.Empty(store.Writes);

        harness.Client.EnableDeferredLinks();
        await Status(harness, DeferredLinkStatus.Delivered);
        harness.Client.EnableDeferredLinks();
        harness.Client.EnableDeferredLinks();
        await Async.Settle();

        Assert.Single(harness.Api.Deferred);
        Assert.Single(harness.Received);
    }

    [Fact]
    public async Task EnableDeferredLinks_before_start_waits_for_start()
    {
        var store = new MemoryStore();
        await using var harness = new Harness(null, options =>
        {
            options.Platform = StrybokPlatform.Android;
            options.AndroidPackageName = Package;
            options.DeferredLinkStore = store;
            options.InstallReferrerSources.Add(Referrer());
        });
        harness.Api.OnDeferred = (_, _) => Task.FromResult(Responses.Json(HttpStatusCode.OK, LinkBody));

        harness.Client.EnableDeferredLinks();
        await Async.Settle();
        Assert.Equal(DeferredLinkStatus.Pending, harness.Client.DeferredLinkStatus);
        Assert.Empty(store.Writes);

        await Started(harness, DeferredLinkStatus.Delivered);
        Assert.Single(harness.Api.Deferred);
    }

    // ------------------------------------------------------------------ a failing store

    [Fact]
    public async Task A_store_that_cannot_be_read_stops_the_check_for_this_launch()
    {
        var store = new MemoryStore { FailReads = true };
        var calls = new List<string>();
        await using var harness = Android(store, Source(InstallReferrerAnswer.Referrer("strybok_click=" + ClickId), calls));
        await Started(harness, DeferredLinkStatus.Pending);
        await Async.Settle();
        Assert.Empty(calls);
        Assert.Empty(harness.Api.Deferred);
        Assert.Empty(harness.Errors);
    }

    [Fact]
    public async Task An_attempt_that_cannot_be_stored_stops_the_check_for_this_launch()
    {
        var store = new MemoryStore { FailWritesOf = MemoryStore.Attempts };
        var calls = new List<string>();
        await using var harness = Android(store, Source(InstallReferrerAnswer.Referrer("strybok_click=" + ClickId), calls));
        await Started(harness, DeferredLinkStatus.Pending);
        await Async.Settle();
        Assert.Empty(calls);
        Assert.Empty(harness.Api.Deferred);
    }

    [Fact]
    public async Task A_link_is_not_delivered_when_done_cannot_be_stored()
    {
        var store = new MemoryStore { FailWritesOf = MemoryStore.Done };
        await using var harness = Android(store, Referrer());
        harness.Listen();
        await Started(harness, DeferredLinkStatus.Failed);
        await Async.Settle();
        Assert.Single(harness.Api.Deferred);
        Assert.Empty(harness.Received);
    }

    // ------------------------------------------------------------------ iOS: pasted link

    [Fact]
    public async Task Ios_without_matching_does_nothing_automatic()
    {
        var store = new MemoryStore();
        await using var harness = Ios(store);
        Assert.Equal(DeferredLinkStatus.Pending, harness.Client.DeferredLinkStatus);
        await Started(harness, DeferredLinkStatus.Unavailable);
        await Async.Settle();
        Assert.Empty(harness.Api.Deferred);
        Assert.Empty(store.Writes); // Not "done": pasting must keep working.
    }

    [Fact]
    public async Task A_pasted_link_is_redeemed_and_delivered_even_after_a_normal_link()
    {
        var store = new MemoryStore();
        var source = new FakeLinkSource { InitialLink = Links.On("first") };
        await using var harness = Ios(store, null, source);
        harness.Listen();
        await Started(harness, DeferredLinkStatus.Unavailable);
        await harness.ReceivedCount(1);
        await Async.Settle();
        var eventsBefore = harness.Api.Events.Count;

        var result = await harness.Client.RedeemPastedLinkAsync("  " + PastedUrl + "\n");

        Assert.Equal(PastedLinkResult.Delivered, result);
        Assert.Equal(DeferredLinkStatus.Delivered, harness.Client.DeferredLinkStatus);
        var request = Assert.Single(harness.Api.Deferred);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(RedeemUrl, request.Uri.AbsoluteUri);
        Assert.Equal("application/json", request.ContentType);
        Assert.Equal("application/json", request.Headers["accept"]);
        Assert.Equal(
            "{\"platform\":\"ios\",\"clickId\":\"" + ClickId + "\",\"bundleId\":\"com.example.App\",\"sdkVersion\":\""
                + StrybokClient.SdkVersion + "\"}",
            request.Body);

        Assert.Equal(2, harness.Received.Count);
        var link = harness.Received[1];
        Assert.True(link.IsDeferred);
        Assert.False(link.IsProbabilisticMatch);
        Assert.Equal("https://my-app.strybok.link/invite777", link.Uri.OriginalString); // Without the click id.
        Assert.Equal(new[] { "strybok.deferred.done=1" }, store.Writes);

        await Async.Settle();
        Assert.Equal(eventsBefore, harness.Api.Events.Count);
        Assert.Empty(harness.Errors);

        // Once only.
        Assert.Equal(PastedLinkResult.AlreadyHandled, await harness.Client.RedeemPastedLinkAsync(PastedUrl));
        Assert.Single(harness.Api.Deferred);
        Assert.Equal(2, harness.Received.Count);
    }

    [Fact]
    public async Task A_pasted_link_works_before_and_without_start_and_is_queued()
    {
        await using var harness = Ios(new MemoryStore());
        Assert.Equal(PastedLinkResult.Delivered, await harness.Client.RedeemPastedLinkAsync(PastedUrl + "&x=1#top"));
        Assert.Null(await harness.Client.GetInitialLinkAsync());
        harness.Listen();
        Assert.True(Assert.Single(harness.Received).IsDeferred);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("my grocery list")]
    [InlineData("https://my-app.strybok.link/invite777")]
    [InlineData("https://my-app.strybok.link/invite777?utm_source=x")]
    [InlineData("https://my-app.strybok.link/invite777#strybok_click=dc_0123456789abcdefghijABCDEFGHIJ01")]
    [InlineData("https://elsewhere.example/invite777?strybok_click=dc_0123456789abcdefghijABCDEFGHIJ01")]
    [InlineData("http://my-app.strybok.link/invite777?strybok_click=dc_0123456789abcdefghijABCDEFGHIJ01")]
    [InlineData("https://my-app.strybok.link/a/b?strybok_click=dc_0123456789abcdefghijABCDEFGHIJ01")]
    [InlineData("https://user@my-app.strybok.link/invite777?strybok_click=dc_0123456789abcdefghijABCDEFGHIJ01")]
    [InlineData("https://my-app.strybok.link/invite777?strybok_click=dc_short")]
    [InlineData("https://my-app.strybok.link/invite777?strybok_click=nope&strybok_click=dc_0123456789abcdefghijABCDEFGHIJ01")]
    [InlineData("https://my-app.strybok.link/invite777?xstrybok_click=dc_0123456789abcdefghijABCDEFGHIJ01")]
    [InlineData("see https://my-app.strybok.link/invite777?strybok_click=dc_0123456789abcdefghijABCDEFGHIJ01")]
    public async Task Text_that_is_not_a_copied_link_of_ours_is_dropped_without_a_request(string? pasted)
    {
        var store = new MemoryStore();
        await using var harness = Ios(store);
        harness.Listen();
        await harness.Client.InitializeAsync();

        Assert.Equal(PastedLinkResult.NotOurs, await harness.Client.RedeemPastedLinkAsync(pasted));
        await Async.Settle();
        Assert.Empty(harness.Api.Deferred);
        Assert.Empty(store.Writes);
        Assert.Empty(harness.Received);
        Assert.Empty(harness.Errors);
    }

    [Theory]
    [InlineData("https://MY-APP.strybok.link:443/invite777?utm_source=x&strybok_click=dc_0123456789abcdefghijABCDEFGHIJ01")]
    [InlineData("https://my-app.strybok.link/invite777?strybok%5Fclick=dc%5F0123456789abcdefghijABCDEFGHIJ01")]
    [InlineData("https://my-app.strybok.link/invite777?strybok_click=dc_0123456789abcdefghijABCDEFGHIJ01&strybok_click=nope")]
    public async Task The_click_id_is_read_like_a_query_parameter(string pasted)
    {
        await using var harness = Ios(new MemoryStore());
        Assert.Equal(PastedLinkResult.Delivered, await harness.Client.RedeemPastedLinkAsync(pasted));
        Assert.Contains("\"clickId\":\"" + ClickId + "\"", Assert.Single(harness.Api.Deferred).Body);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(400)]
    [InlineData(422)]
    public async Task A_refused_pasted_link_is_not_found_and_final(int status)
    {
        var store = new MemoryStore();
        await using var harness = Ios(store);
        harness.Api.OnDeferred = (_, _) => Task.FromResult(Responses.Status(status));
        harness.Listen();

        Assert.Equal(PastedLinkResult.NotFound, await harness.Client.RedeemPastedLinkAsync(PastedUrl));
        Assert.Equal(DeferredLinkStatus.NotFound, harness.Client.DeferredLinkStatus);
        Assert.Equal("1", store.Get(MemoryStore.Done));
        Assert.Equal(PastedLinkResult.AlreadyHandled, await harness.Client.RedeemPastedLinkAsync(PastedUrl));
        Assert.Single(harness.Api.Deferred);
        Assert.Empty(harness.Received);
    }

    [Theory]
    [InlineData("oops")]
    [InlineData("{\"linkId\":\"l\",\"appId\":\"a\",\"routePath\":\"/r\",\"url\":\"https://evil.example/invite777\",\"deferred\":true}")]
    public async Task An_unusable_answer_to_a_pasted_link_is_failed_and_final(string body)
    {
        var store = new MemoryStore();
        await using var harness = Ios(store);
        harness.Api.OnDeferred = (_, _) => Task.FromResult(Responses.Json(HttpStatusCode.OK, body));
        harness.Listen();

        Assert.Equal(PastedLinkResult.Failed, await harness.Client.RedeemPastedLinkAsync(PastedUrl));
        Assert.Equal(DeferredLinkStatus.Failed, harness.Client.DeferredLinkStatus);
        Assert.Equal("1", store.Get(MemoryStore.Done));
        Assert.Empty(harness.Received);
        Assert.Equal(PastedLinkResult.AlreadyHandled, await harness.Client.RedeemPastedLinkAsync(PastedUrl));
    }

    [Fact]
    public async Task After_a_network_failure_the_person_can_paste_again()
    {
        var store = new MemoryStore();
        await using var harness = Ios(store);
        harness.Api.OnDeferred = (_, _) => Task.FromResult(Responses.Status(503));
        harness.Listen();
        await Started(harness, DeferredLinkStatus.Unavailable);

        Assert.Equal(PastedLinkResult.Failed, await harness.Client.RedeemPastedLinkAsync(PastedUrl));
        Assert.Equal(3, harness.Api.Deferred.Count);
        Assert.Equal(harness.Api.Deferred[0].Body, harness.Api.Deferred[2].Body);
        Assert.Empty(store.Writes);
        Assert.Equal(DeferredLinkStatus.Unavailable, harness.Client.DeferredLinkStatus);

        harness.Api.OnDeferred = (_, _) => Task.FromResult(Responses.Status(429));
        Assert.Equal(PastedLinkResult.Failed, await harness.Client.RedeemPastedLinkAsync(PastedUrl));
        Assert.Equal(4, harness.Api.Deferred.Count); // 429 is not retried.
        Assert.Empty(store.Writes);

        harness.Api.OnDeferred = (_, _) => Task.FromResult(Responses.Json(HttpStatusCode.OK, LinkBody));
        Assert.Equal(PastedLinkResult.Delivered, await harness.Client.RedeemPastedLinkAsync(PastedUrl));
        Assert.Single(harness.Received);
        Assert.Empty(harness.Errors);
    }

    [Fact]
    public async Task Pasting_on_android_or_after_dispose_is_disabled()
    {
        await using var android = Android(new MemoryStore(), Referrer("x=1"));
        Assert.Equal(PastedLinkResult.Disabled, await android.Client.RedeemPastedLinkAsync(PastedUrl));
        Assert.Empty(android.Api.Deferred);

        var ios = Ios(new MemoryStore());
        await ios.DisposeAsync();
        Assert.Equal(PastedLinkResult.Disabled, await ios.Client.RedeemPastedLinkAsync(PastedUrl));
        Assert.Empty(ios.Api.Deferred);
    }

    [Fact]
    public async Task Paste_calls_are_handled_one_after_another()
    {
        await using var harness = Ios(new MemoryStore());
        var answer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Api.OnDeferred = (_, _) => answer.Task;
        harness.Listen();

        var first = harness.Client.RedeemPastedLinkAsync(PastedUrl);
        var second = harness.Client.RedeemPastedLinkAsync(PastedUrl);
        await Async.Eventually(() => harness.Api.Deferred.Count == 1, "the first request");
        await Async.Settle();
        Assert.False(second.IsCompleted);
        Assert.Single(harness.Api.Deferred);

        answer.SetResult(Responses.Json(HttpStatusCode.OK, LinkBody));
        Assert.Equal(PastedLinkResult.Delivered, await first);
        Assert.Equal(PastedLinkResult.AlreadyHandled, await second);
        Assert.Single(harness.Api.Deferred);
        Assert.Single(harness.Received);
    }

    [Fact]
    public async Task A_pasted_link_fails_without_a_request_when_the_store_cannot_be_read()
    {
        var store = new MemoryStore { FailReads = true };
        await using var harness = Ios(store);
        Assert.Equal(PastedLinkResult.Failed, await harness.Client.RedeemPastedLinkAsync(PastedUrl));
        Assert.Empty(harness.Api.Deferred);
    }

    [Fact]
    public async Task A_pasted_link_is_not_delivered_when_done_cannot_be_stored()
    {
        var store = new MemoryStore { FailWritesOf = MemoryStore.Done };
        await using var harness = Ios(store);
        harness.Listen();
        Assert.Equal(PastedLinkResult.Failed, await harness.Client.RedeemPastedLinkAsync(PastedUrl));
        Assert.Empty(harness.Received);
        Assert.Equal(DeferredLinkStatus.Failed, harness.Client.DeferredLinkStatus);
    }

    // ------------------------------------------------------------------ iOS: probabilistic matching

    [Fact]
    public async Task Matching_sends_only_the_five_fields_and_marks_the_link_as_a_guess()
    {
        var store = new MemoryStore();
        await using var harness = Ios(store, Matching());
        harness.Listen();
        await Started(harness, DeferredLinkStatus.Delivered);

        var request = Assert.Single(harness.Api.Deferred);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(MatchUrl, request.Uri.AbsoluteUri);
        Assert.Equal("application/json", request.ContentType);
        Assert.Equal("application/json", request.Headers["accept"]);
        Assert.Equal(
            "{\"platform\":\"ios\",\"bundleId\":\"com.example.App\",\"deviceFamily\":\"iphone\",\"locale\":\"en-GB\",\"sdkVersion\":\""
                + StrybokClient.SdkVersion + "\"}",
            request.Body);

        var link = Assert.Single(harness.Received);
        Assert.True(link.IsDeferred);
        Assert.True(link.IsProbabilisticMatch);
        Assert.Equal("/promo", link.RoutePath);
        Assert.Equal(new[] { "strybok.deferred.attempts=1", "strybok.deferred.done=1" }, store.Writes);
        await Async.Settle();
        Assert.Empty(harness.Api.Events);

        Assert.Equal(PastedLinkResult.AlreadyHandled, await harness.Client.RedeemPastedLinkAsync(PastedUrl));
    }

    [Theory]
    [InlineData("ipad", "de")]
    [InlineData("ipod", "zh_Hans_CN")]
    [InlineData("iphone", "sr-Latn-RS")]
    public async Task Matching_accepts_the_three_families_and_language_tags(string family, string locale)
    {
        await using var harness = Ios(new MemoryStore(), Matching(options =>
        {
            options.DeviceFamily = () => family;
            options.Locale = () => locale;
        }));
        await Started(harness, DeferredLinkStatus.Delivered);
        var body = Assert.Single(harness.Api.Deferred).Body;
        Assert.Contains("\"deviceFamily\":\"" + family + "\"", body);
        Assert.Contains("\"locale\":\"" + locale + "\"", body);
    }

    [Theory]
    [InlineData("mac", "en-GB")]
    [InlineData("iPhone", "en-GB")]
    [InlineData("", "en-GB")]
    [InlineData(null, "en-GB")]
    [InlineData("iphone", null)]
    [InlineData("iphone", "")]
    [InlineData("iphone", "e")]
    [InlineData("iphone", "en GB")]
    [InlineData("iphone", "en-GB\n")]
    [InlineData("iphone", "english")]
    public async Task Without_a_usable_family_or_locale_matching_is_unavailable(string? family, string? locale)
    {
        var store = new MemoryStore();
        await using var harness = Ios(store, Matching(options =>
        {
            options.DeviceFamily = () => family;
            options.Locale = () => locale;
        }));
        await Started(harness, DeferredLinkStatus.Unavailable);
        await Async.Settle();
        Assert.Empty(harness.Api.Deferred);
        Assert.Equal("1", store.Get(MemoryStore.Done));
    }

    [Fact]
    public async Task Missing_or_throwing_callbacks_make_matching_unavailable()
    {
        var store = new MemoryStore();
        await using var missing = Ios(store, options => options.AllowProbabilisticMatching = true);
        await Started(missing, DeferredLinkStatus.Unavailable);
        Assert.Equal("1", store.Get(MemoryStore.Done));
        Assert.Empty(missing.Api.Deferred);

        var second = new MemoryStore();
        await using var throwing = Ios(second, Matching(options => options.Locale = () => throw new InvalidOperationException("boom")));
        await Started(throwing, DeferredLinkStatus.Unavailable);
        Assert.Equal("1", second.Get(MemoryStore.Done));
        Assert.Empty(throwing.Api.Deferred);
    }

    [Theory]
    // matched missing or different
    [InlineData("{\"linkId\":\"l\",\"appId\":\"a\",\"routePath\":\"/r\",\"url\":\"https://my-app.strybok.link/promo\",\"deferred\":true}")]
    [InlineData("{\"linkId\":\"l\",\"appId\":\"a\",\"routePath\":\"/r\",\"url\":\"https://my-app.strybok.link/promo\",\"deferred\":true,\"matched\":\"exact\"}")]
    [InlineData("{\"linkId\":\"l\",\"appId\":\"a\",\"routePath\":\"/r\",\"url\":\"https://my-app.strybok.link/promo\",\"deferred\":true,\"matched\":true}")]
    [InlineData("{\"linkId\":\"l\",\"appId\":\"a\",\"routePath\":\"/r\",\"url\":\"https://evil.example/promo\",\"deferred\":true,\"matched\":\"probabilistic\"}")]
    [InlineData("nope")]
    public async Task An_unusable_match_is_failed(string body)
    {
        var store = new MemoryStore();
        await using var harness = Ios(store, Matching());
        harness.Api.OnDeferred = (_, _) => Task.FromResult(Responses.Json(HttpStatusCode.OK, body));
        harness.Listen();
        await Started(harness, DeferredLinkStatus.Failed);
        Assert.Equal("1", store.Get(MemoryStore.Done));
        Assert.Empty(harness.Received);
    }

    [Fact]
    public async Task No_match_is_not_found()
    {
        var store = new MemoryStore();
        await using var harness = Ios(store, Matching());
        harness.Api.OnDeferred = (_, _) => Task.FromResult(Responses.Status(404));
        await Started(harness, DeferredLinkStatus.NotFound);
        Assert.Single(harness.Api.Deferred);
        Assert.Equal("1", store.Get(MemoryStore.Done));
        Assert.Empty(harness.Errors);
    }

    [Theory]
    [InlineData(429, 1)]
    [InlineData(500, 3)]
    public async Task A_match_that_could_not_be_asked_is_pending_until_three_attempts(int status, int requests)
    {
        var store = new MemoryStore();
        for (var launch = 1; launch <= 3; launch++)
        {
            await using var harness = Ios(store, Matching());
            harness.Api.OnDeferred = (_, _) => Task.FromResult(Responses.Status(status));
            await Started(harness, DeferredLinkStatus.Pending);
            Assert.Equal(requests, harness.Api.Deferred.Count);
            Assert.Equal(launch.ToString(), store.Get(MemoryStore.Attempts));
            Assert.Null(store.Get(MemoryStore.Done));
        }

        await using var fourth = Ios(store, Matching());
        await Started(fourth, DeferredLinkStatus.Failed);
        Assert.Empty(fourth.Api.Deferred);
        Assert.Equal("1", store.Get(MemoryStore.Done));
    }

    [Fact]
    public async Task A_normal_link_wins_over_a_match_before_and_during_the_request()
    {
        var store = new MemoryStore();
        var source = new FakeLinkSource { InitialLink = Links.Invite };
        await using (var before = Ios(store, Matching(), source))
        {
            before.Listen();
            await Started(before, DeferredLinkStatus.Superseded);
            Assert.Empty(before.Api.Deferred);
            Assert.Equal("1", store.Get(MemoryStore.Done));
            Assert.False(Assert.Single(before.Received).IsDeferred);
        }

        var second = new MemoryStore();
        await using var during = Ios(second, Matching());
        var answer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        during.Api.OnDeferred = (_, _) => answer.Task;
        during.Listen();
        await during.Client.InitializeAsync();
        await Async.Eventually(() => during.Api.Deferred.Count == 1, "the match request");
        during.Source.Emit(Links.Invite);
        await during.ReceivedCount(1);
        answer.SetResult(Responses.Json(HttpStatusCode.OK, MatchBody));
        await Status(during, DeferredLinkStatus.Superseded);
        await Async.Settle();
        Assert.Single(during.Received);
        Assert.Equal("1", second.Get(MemoryStore.Done));
    }

    [Fact]
    public async Task Matching_waits_for_the_paste_and_starts_when_the_text_is_not_ours()
    {
        var store = new MemoryStore();
        await using var harness = Ios(store, Matching(options => options.PasteboardMayContainLink = _ => Task.FromResult(true)));
        harness.Listen();
        await Started(harness, DeferredLinkStatus.AwaitingPaste);
        await Async.Settle();
        Assert.Empty(harness.Api.Deferred);
        Assert.Empty(store.Writes);

        Assert.Equal(PastedLinkResult.NotOurs, await harness.Client.RedeemPastedLinkAsync("https://example.com/"));
        await Status(harness, DeferredLinkStatus.Delivered);
        Assert.Equal(MatchUrl, Assert.Single(harness.Api.Deferred).Uri.AbsoluteUri);
        Assert.True(Assert.Single(harness.Received).IsProbabilisticMatch);
    }

    [Fact]
    public async Task Matching_starts_when_the_paste_is_skipped_and_only_once()
    {
        await using var harness = Ios(new MemoryStore(), Matching(options => options.PasteboardMayContainLink = _ => Task.FromResult(true)));
        harness.Listen();
        await Started(harness, DeferredLinkStatus.AwaitingPaste);

        harness.Client.SkipPastedLink();
        harness.Client.SkipPastedLink();
        await Status(harness, DeferredLinkStatus.Delivered);
        harness.Client.SkipPastedLink();
        await Async.Settle();
        Assert.Single(harness.Api.Deferred);
        Assert.Single(harness.Received);
    }

    [Fact]
    public async Task A_pasted_link_of_ours_means_matching_never_runs()
    {
        var store = new MemoryStore();
        await using var harness = Ios(store, Matching(options => options.PasteboardMayContainLink = _ => Task.FromResult(true)));
        harness.Listen();
        await Started(harness, DeferredLinkStatus.AwaitingPaste);

        Assert.Equal(PastedLinkResult.Delivered, await harness.Client.RedeemPastedLinkAsync(PastedUrl));
        harness.Client.SkipPastedLink();
        await Async.Settle();

        Assert.Equal(RedeemUrl, Assert.Single(harness.Api.Deferred).Uri.AbsoluteUri);
        Assert.Equal(DeferredLinkStatus.Delivered, harness.Client.DeferredLinkStatus);
        var link = Assert.Single(harness.Received);
        Assert.False(link.IsProbabilisticMatch);
        Assert.Null(store.Get(MemoryStore.Attempts));
    }

    [Fact]
    public async Task A_failed_paste_keeps_waiting_and_skip_is_a_no_op_when_not_waiting()
    {
        await using var harness = Ios(new MemoryStore(), Matching(options => options.PasteboardMayContainLink = _ => Task.FromResult(true)));
        harness.Api.OnDeferred = (_, _) => Task.FromResult(Responses.Status(429));
        await Started(harness, DeferredLinkStatus.AwaitingPaste);
        Assert.Equal(PastedLinkResult.Failed, await harness.Client.RedeemPastedLinkAsync(PastedUrl));
        await Async.Settle();
        Assert.Equal(DeferredLinkStatus.AwaitingPaste, harness.Client.DeferredLinkStatus);
        Assert.Single(harness.Api.Deferred);

        await using var plain = Ios(new MemoryStore());
        await Started(plain, DeferredLinkStatus.Unavailable);
        plain.Client.SkipPastedLink();
        await Async.Settle();
        Assert.Empty(plain.Api.Deferred);
        Assert.Equal(DeferredLinkStatus.Unavailable, plain.Client.DeferredLinkStatus);
    }

    [Fact]
    public async Task A_pasteboard_check_that_answers_no_or_throws_does_not_delay_matching()
    {
        await using var no = Ios(new MemoryStore(), Matching(options => options.PasteboardMayContainLink = _ => Task.FromResult(false)));
        await Started(no, DeferredLinkStatus.Delivered);
        Assert.Equal(MatchUrl, Assert.Single(no.Api.Deferred).Uri.AbsoluteUri);

        await using var throwing = Ios(new MemoryStore(), Matching(options => options.PasteboardMayContainLink = _ => throw new InvalidOperationException("boom")));
        await Started(throwing, DeferredLinkStatus.Delivered);
        Assert.Single(throwing.Api.Deferred);
    }

    [Fact]
    public async Task Matching_does_not_run_on_a_later_launch()
    {
        var store = new MemoryStore().With(MemoryStore.Done, "1");
        await using var harness = Ios(store, Matching());
        await Started(harness, DeferredLinkStatus.NotFirstLaunch);
        await Async.Settle();
        Assert.Empty(harness.Api.Deferred);
        Assert.Equal(PastedLinkResult.AlreadyHandled, await harness.Client.RedeemPastedLinkAsync(PastedUrl));
        Assert.Empty(harness.Api.Deferred);
    }

    [Fact]
    public async Task Requests_never_carry_anything_else()
    {
        await using var android = Android(new MemoryStore(), Referrer("utm_source=secret-campaign&strybok_click=" + ClickId + "&gclid=private"));
        await Started(android, DeferredLinkStatus.Delivered);
        var body = Assert.Single(android.Api.Deferred).Body;
        Assert.DoesNotContain("secret-campaign", body);
        Assert.DoesNotContain("gclid", body);
        Assert.Equal(4, body.Count(c => c == ':'));

        await using var ios = Ios(new MemoryStore());
        await ios.Client.RedeemPastedLinkAsync(Links.InviteUrl + "?email=someone%40example.com&strybok_click=" + ClickId);
        var pasted = Assert.Single(ios.Api.Deferred).Body;
        Assert.DoesNotContain("someone", pasted);
        Assert.DoesNotContain("invite777", pasted);
    }
}
