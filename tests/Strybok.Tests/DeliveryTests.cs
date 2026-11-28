using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Strybok.Tests;

/// <summary>"Delivery" from the SDK contract: cold start.</summary>
public sealed class ColdStartTests
{
    [Fact]
    public async Task Subscribes_to_the_stream_before_reading_the_launch_url()
    {
        var source = new FakeLinkSource { InitialLink = Links.Invite };
        await using var h = new Harness(source);

        await h.Client.InitializeAsync();

        Assert.Equal(new[] { "subscribe", "getInitialLink" }, source.Calls.ToArray());
    }

    [Fact]
    public async Task Delivers_the_launch_link_once_even_if_the_listener_attaches_late()
    {
        await using var h = new Harness(new FakeLinkSource { InitialLink = Links.Invite });
        await h.Client.InitializeAsync();

        h.Listen();

        // Queued links are replayed while the handler is being attached.
        var link = Assert.Single(h.Received);
        Assert.Equal("/invite/abc123", link.RoutePath);
        Assert.Single(h.Api.Resolves);
        await Async.Settle();
        Assert.Single(h.Received);
    }

    [Fact]
    public async Task Initialize_completes_only_after_the_launch_link_was_resolved()
    {
        await using var h = new Harness(new FakeLinkSource { InitialLink = Links.Invite });
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Api.OnResolve = (_, _) => response.Task;

        var initialization = h.Client.InitializeAsync();
        await Async.Eventually(() => h.Api.Resolves.Count == 1, "the launch link to be looked up");
        await Async.Settle();
        Assert.False(initialization.IsCompleted);

        response.SetResult(Responses.Ok());
        await initialization;

        Assert.NotNull(await h.Client.GetInitialLinkAsync());
    }

    [Fact]
    public async Task De_duplicates_a_launch_link_that_is_replayed_on_the_stream()
    {
        var source = new FakeLinkSource { InitialLink = Links.Invite, ReplayInitialLinkOnStream = true };
        await using var h = new Harness(source);
        h.Listen();

        await h.Client.InitializeAsync();
        await h.ReceivedCount(1);
        await Async.Eventually(() => h.Api.Events.Count == 1, "the app_open event");
        await Async.Settle();

        Assert.Single(h.Received);
        Assert.Single(h.Api.Resolves);
        Assert.Single(h.Api.Events);
    }

    [Fact]
    public async Task GetInitialLink_consumes_the_launch_link_so_the_event_does_not_repeat_it()
    {
        await using var h = new Harness(new FakeLinkSource { InitialLink = Links.Invite });

        var initial = await h.Client.GetInitialLinkAsync();
        Assert.NotNull(initial);
        Assert.Equal("/invite/abc123", initial!.RoutePath);
        Assert.Null(await h.Client.GetInitialLinkAsync());

        h.Listen();
        await Async.Settle();
        Assert.Empty(h.Received);
    }

    [Fact]
    public async Task GetInitialLink_returns_null_once_the_event_already_delivered_it()
    {
        await using var h = new Harness(new FakeLinkSource { InitialLink = Links.Invite });
        h.Listen();

        await h.Client.InitializeAsync();

        Assert.Single(h.Received);
        Assert.Null(await h.Client.GetInitialLinkAsync());
    }

    [Fact]
    public async Task GetInitialLink_returns_null_once_a_late_listener_received_the_queued_link()
    {
        await using var h = new Harness(new FakeLinkSource { InitialLink = Links.Invite });
        await h.Client.InitializeAsync();
        h.Listen();

        Assert.Single(h.Received);
        Assert.Null(await h.Client.GetInitialLinkAsync());
    }

    [Fact]
    public async Task No_launch_link_foreign_launch_link_and_repeated_initialize_are_harmless()
    {
        var source = new FakeLinkSource { InitialLink = new Uri("https://other.example.com/x") };
        await using var h = new Harness(source);

        var first = h.Client.InitializeAsync();
        var second = h.Client.InitializeAsync();
        await Task.WhenAll(first, second);
        await h.Client.InitializeAsync();

        Assert.Same(first, second);
        Assert.Null(await h.Client.GetInitialLinkAsync());
        Assert.Empty(h.Api.Resolves);
        Assert.Empty(h.Errors);
        Assert.Equal(1, source.Calls.Count(call => call == "subscribe"));

        await using var none = new Harness();
        await none.Client.InitializeAsync();
        Assert.Null(await none.Client.GetInitialLinkAsync());
        Assert.Empty(none.Api.Resolves);
    }

    [Fact]
    public async Task A_failing_launch_link_is_reported_through_OnError_and_initialize_completes()
    {
        var invite = Links.Invite;
        await using var h = new Harness(new FakeLinkSource { InitialLink = invite });
        h.Api.OnResolve = (_, _) => Task.FromResult(Responses.Status(404));

        await h.Client.InitializeAsync();

        var error = Assert.Single(h.Errors);
        Assert.IsType<LinkNotFoundException>(error);
        Assert.Same(invite, error.Uri);
        Assert.Null(await h.Client.GetInitialLinkAsync());
    }

    [Fact]
    public async Task A_platform_that_cannot_report_the_launch_link_is_treated_as_no_launch_link()
    {
        var source = new FakeLinkSource { FailInitialLink = true };
        await using var h = new Harness(source);
        h.Listen();

        await h.Client.InitializeAsync();
        source.Emit(Links.Invite);

        await h.ReceivedCount(1);
        Assert.Empty(h.Errors);
    }
}

/// <summary>"Delivery" from the SDK contract: links received while running.</summary>
public sealed class StreamTests
{
    [Fact]
    public async Task Delivers_links_received_while_running()
    {
        var source = new FakeLinkSource();
        await using var h = new Harness(source);
        var routes = new[] { "/p/first", "/p/second" };
        var next = -1;
        h.Api.OnResolve = (_, _) => Task.FromResult(Responses.Ok(routes[Interlocked.Increment(ref next)]));
        h.Listen();
        await h.Client.InitializeAsync();

        source.Emit(Links.On("first"));
        await h.ReceivedCount(1);
        source.Emit(Links.On("second"));
        await h.ReceivedCount(2);

        Assert.Equal(routes, h.Received.Select(link => link.RoutePath).ToArray());
        Assert.Equal(2, h.Api.Resolves.Count);
    }

    [Fact]
    public async Task Drops_an_immediate_duplicate_but_honours_the_same_link_tapped_again_later()
    {
        var source = new FakeLinkSource();
        await using var h = new Harness(source);
        h.Listen();
        await h.Client.InitializeAsync();

        source.Emit(Links.Invite);
        h.Now += TimeSpan.FromMilliseconds(1999);
        source.Emit(Links.Invite);
        await h.ReceivedCount(1);
        await Async.Settle();
        Assert.Single(h.Received);
        Assert.Single(h.Api.Resolves);

        // App paused, user taps the same link again a minute later (resume).
        h.Now += TimeSpan.FromMinutes(1);
        source.Emit(Links.Invite);
        await h.ReceivedCount(2);
        Assert.Equal(2, h.Api.Resolves.Count);
    }

    [Fact]
    public async Task The_dedupe_window_ends_exactly_at_its_length()
    {
        var source = new FakeLinkSource();
        await using var h = new Harness(source);
        h.Listen();
        await h.Client.InitializeAsync();

        source.Emit(Links.Invite);
        await h.ReceivedCount(1);
        h.Now += TimeSpan.FromSeconds(2);
        source.Emit(Links.Invite);

        await h.ReceivedCount(2);
    }

    [Fact]
    public async Task Only_the_identical_url_string_is_a_duplicate()
    {
        var source = new FakeLinkSource();
        await using var h = new Harness(source);
        h.Listen();
        await h.Client.InitializeAsync();

        source.Emit(new Uri(Links.InviteUrl));
        source.Emit(new Uri(Links.InviteUrl + "?utm=1"));

        await h.ReceivedCount(2);
        Assert.Equal(2, h.Api.Resolves.Count);
    }

    [Fact]
    public async Task Multiple_listeners_each_see_a_link_once()
    {
        var source = new FakeLinkSource();
        await using var h = new Harness(source);
        var a = 0;
        var b = 0;
        h.Client.LinkReceived += (sender, link) => Interlocked.Increment(ref a);
        h.Client.LinkReceived += (sender, link) => Interlocked.Increment(ref b);
        await h.Client.InitializeAsync();

        source.Emit(Links.Invite);

        await Async.Eventually(() => Volatile.Read(ref a) == 1 && Volatile.Read(ref b) == 1, "both listeners");
        await Async.Settle();
        Assert.Equal(1, Volatile.Read(ref a));
        Assert.Equal(1, Volatile.Read(ref b));
    }

    [Fact]
    public async Task Queued_links_are_replayed_to_the_first_subscriber_only()
    {
        var source = new FakeLinkSource { InitialLink = Links.On("launch") };
        await using var h = new Harness(source);
        await h.Client.InitializeAsync();
        source.Emit(Links.On("later"));
        await Async.Eventually(() => h.Api.Resolves.Count == 2, "both links to be looked up");
        await Async.Settle();

        var first = new List<string>();
        var second = new List<string>();
        h.Client.LinkReceived += (sender, link) => first.Add(link.Uri.AbsolutePath);
        h.Client.LinkReceived += (sender, link) => second.Add(link.Uri.AbsolutePath);

        Assert.Equal(new[] { "/launch", "/later" }, first.ToArray());
        Assert.Empty(second);
    }

    [Fact]
    public async Task Links_resolved_after_the_last_listener_left_wait_for_the_next_one()
    {
        var source = new FakeLinkSource();
        await using var h = new Harness(source);
        h.Listen();
        await h.Client.InitializeAsync();
        h.StopListening();

        source.Emit(Links.Invite);
        await Async.Eventually(() => h.Api.Resolves.Count == 1, "the link to be looked up");
        await Async.Settle();
        Assert.Empty(h.Received);

        h.Listen();
        Assert.Single(h.Received);
    }

    [Fact]
    public async Task Errors_go_to_OnError_and_later_links_keep_working()
    {
        var source = new FakeLinkSource();
        await using var h = new Harness(source, options => options.MaxRetries = 0);
        var fail = true;
        h.Api.OnResolve = (_, _) =>
        {
            if (Volatile.Read(ref fail)) throw new HttpRequestException("offline");
            return Task.FromResult(Responses.Ok());
        };
        h.Listen();
        await h.Client.InitializeAsync();

        var offline = Links.On("offline1");
        source.Emit(offline);
        await Async.Eventually(() => h.Errors.Count == 1, "the error");
        var error = Assert.Single(h.Errors);
        Assert.IsType<StrybokNetworkException>(error);
        Assert.Same(offline, error.Uri);
        Assert.Empty(h.Received);

        Volatile.Write(ref fail, false);
        source.Emit(Links.On("online1"));
        await h.ReceivedCount(1);
        Assert.Single(h.Errors);
    }

    [Fact]
    public async Task A_throwing_handler_does_not_break_other_handlers_or_later_links()
    {
        var source = new FakeLinkSource();
        await using var h = new Harness(source);
        h.Client.LinkReceived += (sender, link) => throw new InvalidOperationException("app bug");
        h.Listen();
        await h.Client.InitializeAsync();

        source.Emit(Links.On("first"));
        await h.ReceivedCount(1);
        source.Emit(Links.On("second"));
        await h.ReceivedCount(2);

        Assert.Empty(h.Errors);
    }

    [Fact]
    public async Task A_throwing_OnError_handler_does_not_break_later_links()
    {
        var source = new FakeLinkSource();
        var failures = 0;
        await using var h = new Harness(source, options =>
        {
            options.MaxRetries = 0;
            options.OnError = error =>
            {
                Interlocked.Increment(ref failures);
                throw new InvalidOperationException("app bug");
            };
        });
        var calls = 0;
        h.Api.OnResolve = (_, _) => Task.FromResult(
            Interlocked.Increment(ref calls) == 1 ? Responses.Status(404) : Responses.Ok());
        h.Listen();
        await h.Client.InitializeAsync();

        source.Emit(Links.On("gone"));
        await Async.Eventually(() => Volatile.Read(ref failures) == 1, "the error");
        source.Emit(Links.On("fine"));

        await h.ReceivedCount(1);
    }

    [Fact]
    public async Task Concurrent_links_and_a_concurrent_first_subscriber_still_mean_exactly_once()
    {
        const int count = 200;
        var source = new FakeLinkSource();
        await using var h = new Harness(source);
        await h.Client.InitializeAsync();

        var seen = new ConcurrentDictionary<string, int>();
        void OnLink(object? sender, ResolvedDeepLink link) =>
            seen.AddOrUpdate(link.Uri.AbsolutePath, 1, (key, times) => times + 1);

        var emitting = Task.Run(() => Parallel.For(0, count, i => source.Emit(Links.On("slug" + i))));
        var subscribing = Task.Run(() =>
        {
            Thread.Yield();
            h.Client.LinkReceived += OnLink;
        });
        await Task.WhenAll(emitting, subscribing);

        await Async.Eventually(() => seen.Count == count, "all links to be delivered");
        await Async.Settle();
        Assert.Equal(count, seen.Count);
        Assert.All(seen.Values, times => Assert.Equal(1, times));
        Assert.Equal(count, h.Api.Resolves.Count);
    }
}

/// <summary>"app_open event" from the SDK contract.</summary>
public sealed class AppOpenEventTests
{
    private static readonly Regex UuidV4 = new Regex(
        "^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\\z",
        RegexOptions.CultureInvariant);

    [Fact]
    public async Task Sends_one_minimal_best_effort_event_per_delivered_link()
    {
        var source = new FakeLinkSource();
        await using var h = new Harness(source);
        h.Listen();
        await h.Client.InitializeAsync();

        source.Emit(Links.Invite);
        await h.ReceivedCount(1);
        await Async.Eventually(() => h.Api.Events.Count == 1, "the app_open event");
        await Async.Settle();

        var recorded = Assert.Single(h.Api.Events);
        Assert.Equal(HttpMethod.Post, recorded.Method);
        Assert.Equal("api.strybok.com", recorded.Uri.Host);
        Assert.Equal("/v1/public/events", recorded.Uri.AbsolutePath);
        Assert.Equal(string.Empty, recorded.Uri.Query);
        Assert.Equal("application/json", recorded.ContentType);

        var body = recorded.Body;
        Assert.Equal(System.Text.Json.JsonValueKind.Object, body.ValueKind);
        Assert.Equal(
            new[] { "eventId", "linkId", "platform", "sdkVersion", "type" },
            body.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.Equal("app_open", body.GetProperty("type").GetString());
        Assert.Equal("link_1", body.GetProperty("linkId").GetString());
        Assert.Equal("unknown", body.GetProperty("platform").GetString());
        Assert.Equal("0.4.0", body.GetProperty("sdkVersion").GetString());
        Assert.Matches(UuidV4, body.GetProperty("eventId").GetString());
    }

    [Theory]
    [InlineData(StrybokPlatform.iOS, "ios")]
    [InlineData(StrybokPlatform.Android, "android")]
    [InlineData(StrybokPlatform.Unknown, "unknown")]
    public async Task Reports_the_configured_platform(StrybokPlatform platform, string expected)
    {
        await using var h = new Harness(
            new FakeLinkSource { InitialLink = Links.Invite },
            options => options.Platform = platform);

        await h.Client.InitializeAsync();
        await Async.Eventually(() => h.Api.Events.Count == 1, "the app_open event");

        Assert.Equal(expected, h.Api.Events[0].Body.GetProperty("platform").GetString());
    }

    [Fact]
    public async Task A_queued_link_is_reported_once_and_event_ids_differ()
    {
        var source = new FakeLinkSource { InitialLink = Links.On("launch") };
        await using var h = new Harness(source);
        await h.Client.InitializeAsync();
        await Async.Eventually(() => h.Api.Events.Count == 1, "the event of the queued link");

        h.Listen();
        source.Emit(Links.On("later"));
        await Async.Eventually(() => h.Api.Events.Count == 2, "the event of the second link");
        await Async.Settle();

        var ids = h.Api.Events.Select(recorded => recorded.Body.GetProperty("eventId").GetString()).ToArray();
        Assert.Equal(2, ids.Length);
        Assert.NotEqual(ids[0], ids[1]);
    }

    [Fact]
    public async Task Keeps_the_path_prefix_of_the_api_base_url()
    {
        await using var h = new Harness(
            new FakeLinkSource { InitialLink = Links.Invite },
            options => options.ApiBaseUrl = new Uri("https://example.com/strybok/"));

        await h.Client.InitializeAsync();
        await Async.Eventually(() => h.Api.Events.Count == 1, "the app_open event");

        Assert.Equal("/strybok/v1/public/events", h.Api.Events[0].Uri.AbsolutePath);
    }

    [Theory]
    [InlineData("hangs")]
    [InlineData("throws")]
    [InlineData("500")]
    public async Task A_slow_or_failing_events_endpoint_never_delays_or_breaks_delivery(string behaviour)
    {
        var source = new FakeLinkSource();
        await using var h = new Harness(source);
        h.Api.OnEvent = token => behaviour switch
        {
            "hangs" => Async.Never<HttpResponseMessage>(token),
            "throws" => throw new HttpRequestException("down"),
            _ => Task.FromResult(Responses.Status(500)),
        };
        h.Listen();
        await h.Client.InitializeAsync();

        source.Emit(Links.On("first"));
        await h.ReceivedCount(1);
        source.Emit(Links.On("second"));
        await h.ReceivedCount(2);
        await Async.Eventually(() => h.Api.Events.Count == 2, "both events to be attempted");
        await Async.Settle();

        Assert.Empty(h.Errors);
        Assert.Equal(2, h.Received.Count);
    }

    [Fact]
    public async Task Can_be_turned_off()
    {
        await using var h = new Harness(
            new FakeLinkSource { InitialLink = Links.Invite },
            options => options.ReportAppOpens = false);

        await h.Client.InitializeAsync();
        await Async.Settle();

        Assert.NotNull(await h.Client.GetInitialLinkAsync());
        Assert.Empty(h.Api.Events);
    }

    [Fact]
    public async Task A_failed_link_is_not_reported()
    {
        await using var h = new Harness(new FakeLinkSource { InitialLink = Links.Invite });
        h.Api.OnResolve = (_, _) => Task.FromResult(Responses.Status(404));

        await h.Client.InitializeAsync();
        await Async.Settle();

        Assert.Single(h.Errors);
        Assert.Empty(h.Api.Events);
    }
}

/// <summary>"After dispose/stop nothing is delivered" from the SDK contract.</summary>
public sealed class DisposeTests
{
    [Fact]
    public async Task Stops_delivery_unsubscribes_and_rejects_further_use()
    {
        var source = new FakeLinkSource();
        var h = new Harness(source);
        h.Listen();
        await h.Client.InitializeAsync();
        Assert.True(source.HasListener);

        await h.Client.DisposeAsync();
        await h.Client.DisposeAsync(); // Idempotent.
        h.Client.Dispose();
        Assert.False(source.HasListener);

        source.Emit(Links.Invite);
        await Async.Settle();
        Assert.Empty(h.Received);
        Assert.Empty(h.Api.Resolves);

        await Assert.ThrowsAsync<ObjectDisposedException>(() => h.Client.ResolveAsync(Links.Invite));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => h.Client.InitializeAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => h.Client.GetInitialLinkAsync());
        // Still a pure check, and subscribing is a harmless no-op.
        Assert.True(h.Client.IsStrybokUri(Links.Invite));
        h.Listen();
    }

    [Fact]
    public async Task A_link_still_resolving_when_disposed_is_not_delivered()
    {
        var source = new FakeLinkSource();
        var h = new Harness(source);
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Api.OnResolve = (_, _) => response.Task; // Ignores cancellation on purpose.
        h.Listen();
        await h.Client.InitializeAsync();
        source.Emit(Links.Invite);
        await Async.Eventually(() => h.Api.Resolves.Count == 1, "the request to start");

        await h.Client.DisposeAsync();
        response.SetResult(Responses.Ok());
        await Async.Settle();

        Assert.Empty(h.Received);
        Assert.Empty(h.Errors);
        Assert.Empty(h.Api.Events);
    }

    [Fact]
    public async Task A_queued_link_is_dropped_on_dispose()
    {
        var h = new Harness(new FakeLinkSource { InitialLink = Links.Invite });
        await h.Client.InitializeAsync();

        await h.Client.DisposeAsync();
        h.Listen();

        Assert.Empty(h.Received);
    }

    [Fact]
    public async Task Disposing_while_initialize_is_resolving_the_launch_link_completes_it_quietly()
    {
        var h = new Harness(new FakeLinkSource { InitialLink = Links.Invite });
        h.Api.OnResolve = (_, token) => Async.Never<HttpResponseMessage>(token);
        h.Listen();

        var initialization = h.Client.InitializeAsync();
        await Async.Eventually(() => h.Api.Resolves.Count == 1, "the request to start");
        await h.Client.DisposeAsync();
        await initialization;

        Assert.Empty(h.Received);
        Assert.Empty(h.Errors);
    }

    [Fact]
    public async Task An_explicit_resolve_in_flight_fails_with_ObjectDisposedException()
    {
        var h = new Harness();
        h.Api.OnResolve = (_, token) => Async.Never<HttpResponseMessage>(token);

        var pending = h.Client.ResolveAsync(Links.Invite);
        await Async.Eventually(() => h.Api.Resolves.Count == 1, "the request to start");
        await h.Client.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => pending);
    }
}

/// <summary>The link source apps feed by hand (used by the .NET MAUI glue).</summary>
public sealed class ManualLinkSourceTests
{
    [Fact]
    public async Task A_url_pushed_before_start_is_the_launch_link()
    {
        var source = new ManualLinkSource();
        source.Push(Links.Invite);
        await using var h = new Harness(source);

        var initial = await h.Client.GetInitialLinkAsync();

        Assert.NotNull(initial);
        Assert.Equal("/invite/abc123", initial!.RoutePath);
        Assert.Single(h.Api.Resolves);
    }

    [Fact]
    public async Task Urls_pushed_before_start_are_all_delivered_once()
    {
        var source = new ManualLinkSource();
        source.Push(Links.On("launch"));
        source.Push(Links.On("second"));
        await using var h = new Harness(source);
        h.Listen();

        await h.Client.InitializeAsync();
        await h.ReceivedCount(2);
        await Async.Settle();

        Assert.Equal(
            new[] { "/launch", "/second" },
            h.Received.Select(link => link.Uri.AbsolutePath).OrderBy(path => path, StringComparer.Ordinal).ToArray());
        Assert.Equal(2, h.Api.Resolves.Count);
    }

    [Fact]
    public async Task A_url_pushed_while_running_is_delivered()
    {
        var source = new ManualLinkSource();
        await using var h = new Harness(source);
        h.Listen();
        await h.Client.InitializeAsync();

        source.Push(Links.Invite);

        await h.ReceivedCount(1);
        Assert.Null(await h.Client.GetInitialLinkAsync());
    }

    [Fact]
    public async Task The_same_tap_reported_twice_by_the_platform_is_delivered_once()
    {
        var source = new ManualLinkSource();
        source.Push(Links.Invite);
        await using var h = new Harness(source);
        h.Listen();
        await h.Client.InitializeAsync();

        source.Push(Links.Invite);
        await Async.Settle();

        Assert.Single(h.Received);
        Assert.Single(h.Api.Resolves);
    }

    [Fact]
    public async Task Pushing_after_the_client_was_disposed_is_harmless()
    {
        var source = new ManualLinkSource();
        var h = new Harness(source);
        h.Listen();
        await h.Client.InitializeAsync();
        await h.Client.DisposeAsync();

        source.Push(Links.Invite);
        await Async.Settle();

        Assert.Empty(h.Received);
        Assert.Empty(h.Api.Resolves);
        Assert.Throws<ArgumentNullException>(() => source.Push(null!));
    }

    [Fact]
    public async Task Without_a_client_the_buffer_is_bounded_and_keeps_the_newest_urls()
    {
        var source = new ManualLinkSource();
        for (var i = 0; i < 100; i++) source.Push(Links.On("slug" + i));

        var received = new List<Uri>();
        using var subscription = source.Subscribe(received.Add);
        var initial = await source.GetInitialLinkAsync();

        Assert.NotNull(initial);
        Assert.NotEmpty(received);
        Assert.True(received.Count < 100);
        Assert.Equal("/slug99", received[received.Count - 1].AbsolutePath);
    }
}
