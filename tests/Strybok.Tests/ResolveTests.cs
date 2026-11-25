using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Strybok.Tests;

/// <summary>"Resolving" from the SDK contract.</summary>
public sealed class ResolveTests
{
    [Fact]
    public async Task Returns_route_and_payload_and_sends_only_host_and_slug_to_the_api()
    {
        await using var h = new Harness();
        var uri = new Uri("https://MY-APP.Strybok.Link/Invite777?token=secret#f");

        var link = await h.Client.ResolveAsync(uri);

        Assert.Equal("/invite/abc123", link.RoutePath);
        Assert.Equal("link_1", link.LinkId);
        Assert.Equal("app_1", link.AppId);
        Assert.Equal("abc123", link.GetPayloadString("invitationId"));
        Assert.Null(link.GetPayloadString("missing"));
        Assert.Same(uri, link.Uri);
        Assert.Contains("token=secret", link.Uri.OriginalString, StringComparison.Ordinal);

        var request = Assert.Single(h.Api.Resolves);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("api.strybok.com", request.Uri.Host);
        Assert.Equal("/v1/public/resolve", request.Uri.AbsolutePath);
        Assert.Equal("application/json", request.Accept);
        // Lower-cased host, slug as written, no query, no fragment.
        Assert.Equal("https://my-app.strybok.link/Invite777", request.LookupUrl);
        Assert.DoesNotContain("secret", request.Uri.AbsoluteUri, StringComparison.Ordinal);

        await Async.Settle();
        Assert.Empty(h.Api.Events); // Explicit resolution is not an app open.
        Assert.Empty(h.Received);
    }

    [Fact]
    public async Task The_default_port_is_not_part_of_the_lookup()
    {
        await using var h = new Harness();
        await h.Client.ResolveAsync(new Uri("https://my-app.strybok.link:443/invite777"));
        Assert.Equal(Links.InviteUrl, Assert.Single(h.Api.Resolves).LookupUrl);
    }

    [Fact]
    public async Task Keeps_a_path_prefix_of_the_api_base_url()
    {
        await using var h = new Harness(
            configure: options => options.ApiBaseUrl = new Uri("https://example.com/strybok/"));

        await h.Client.ResolveAsync(Links.Invite);

        var request = Assert.Single(h.Api.Resolves);
        Assert.Equal("example.com", request.Uri.Host);
        Assert.Equal("/strybok/v1/public/resolve", request.Uri.AbsolutePath);
    }

    [Fact]
    public async Task Status_404_becomes_LinkNotFound_and_is_not_retried()
    {
        await using var h = new Harness();
        h.Api.OnResolve = (_, _) =>
            Task.FromResult(Responses.Json(HttpStatusCode.NotFound, "{\"error\":{\"code\":\"LINK_NOT_FOUND\"}}"));
        var uri = Links.Invite;

        var error = await Assert.ThrowsAsync<LinkNotFoundException>(() => h.Client.ResolveAsync(uri));

        Assert.Same(uri, error.Uri);
        Assert.Single(h.Api.Resolves);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(422)]
    [InlineData(429)]
    public async Task Other_client_errors_are_not_retried(int status)
    {
        await using var h = new Harness();
        h.Api.OnResolve = (_, _) => Task.FromResult(Responses.Status(status));
        var uri = Links.Invite;

        var error = await Assert.ThrowsAsync<StrybokServerException>(() => h.Client.ResolveAsync(uri));

        Assert.Equal(status, error.StatusCode);
        Assert.Same(uri, error.Uri);
        Assert.Single(h.Api.Resolves);
    }

    [Fact]
    public async Task Retries_network_errors_and_then_succeeds_with_a_doubling_pause()
    {
        await using var h = new Harness();
        var calls = 0;
        h.Api.OnResolve = (_, _) =>
        {
            if (Interlocked.Increment(ref calls) < 3) throw new HttpRequestException("connection reset");
            return Task.FromResult(Responses.Ok());
        };

        var link = await h.Client.ResolveAsync(Links.Invite);

        Assert.Equal("/invite/abc123", link.RoutePath);
        Assert.Equal(3, calls);
        Assert.Equal(
            new[] { TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(800) },
            h.Delay.RetryPauses.ToArray());
    }

    [Fact]
    public async Task Gives_up_with_a_network_exception_after_max_retries()
    {
        await using var h = new Harness(configure: options => options.MaxRetries = 1);
        h.Api.OnResolve = (_, _) => throw new HttpRequestException("offline");
        var uri = Links.Invite;

        var error = await Assert.ThrowsAsync<StrybokNetworkException>(() => h.Client.ResolveAsync(uri));

        Assert.Same(uri, error.Uri);
        Assert.IsType<HttpRequestException>(error.InnerException);
        Assert.Equal(2, h.Api.Resolves.Count);
    }

    [Fact]
    public async Task Zero_retries_means_a_single_attempt()
    {
        await using var h = new Harness(configure: options => options.MaxRetries = 0);
        h.Api.OnResolve = (_, _) => throw new HttpRequestException("offline");

        await Assert.ThrowsAsync<StrybokNetworkException>(() => h.Client.ResolveAsync(Links.Invite));

        Assert.Single(h.Api.Resolves);
        Assert.Empty(h.Delay.RetryPauses);
    }

    [Fact]
    public async Task Retries_5xx_responses()
    {
        await using var h = new Harness();
        var calls = 0;
        h.Api.OnResolve = (_, _) =>
            Task.FromResult(Interlocked.Increment(ref calls) == 1 ? Responses.Status(503) : Responses.Ok());

        await h.Client.ResolveAsync(Links.Invite);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Reports_the_last_5xx_after_max_retries()
    {
        await using var h = new Harness();
        h.Api.OnResolve = (_, _) => Task.FromResult(Responses.Status(503));

        var error = await Assert.ThrowsAsync<StrybokServerException>(() => h.Client.ResolveAsync(Links.Invite));

        Assert.Equal(503, error.StatusCode);
        Assert.Equal(3, h.Api.Resolves.Count);
    }

    [Fact]
    public async Task Times_out_with_a_typed_exception_even_when_the_transport_ignores_cancellation()
    {
        await using var h = new Harness(configure: options => options.MaxRetries = 1);
        h.Delay.FireTimeouts = true;
        var never = new TaskCompletionSource<HttpResponseMessage>();
        h.Api.OnResolve = (_, _) => never.Task;
        var uri = Links.Invite;

        var error = await Assert.ThrowsAsync<StrybokTimeoutException>(() => h.Client.ResolveAsync(uri));

        Assert.Same(uri, error.Uri);
        Assert.Equal(2, h.Api.Resolves.Count);
    }

    [Fact]
    public async Task A_timed_out_request_is_cancelled()
    {
        await using var h = new Harness(configure: options => options.MaxRetries = 0);
        h.Delay.FireTimeouts = true;
        var cancelled = 0;
        h.Api.OnResolve = (_, token) =>
        {
            token.Register(() => Interlocked.Increment(ref cancelled));
            return Async.Never<HttpResponseMessage>(token);
        };

        await Assert.ThrowsAsync<StrybokTimeoutException>(() => h.Client.ResolveAsync(Links.Invite));

        await Async.Eventually(() => Volatile.Read(ref cancelled) == 1, "the abandoned request to be cancelled");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"/invite/abc123\"")]
    [InlineData("{\"linkId\":\"l\",\"appId\":\"a\"}")]
    [InlineData("{\"linkId\":\"l\",\"routePath\":\"/x\"}")]
    [InlineData("{\"appId\":\"a\",\"routePath\":\"/x\"}")]
    [InlineData("{\"linkId\":\"l\",\"appId\":\"a\",\"routePath\":\"https://evil.example.com\"}")]
    [InlineData("{\"linkId\":\"l\",\"appId\":\"a\",\"routePath\":\"//evil.example.com\"}")]
    [InlineData("{\"linkId\":\"l\",\"appId\":\"a\",\"routePath\":\"invite\"}")]
    [InlineData("{\"linkId\":\"l\",\"appId\":\"a\",\"routePath\":\"\"}")]
    [InlineData("{\"linkId\":1,\"appId\":\"a\",\"routePath\":\"/x\"}")]
    [InlineData("{\"linkId\":\"l\",\"appId\":null,\"routePath\":\"/x\"}")]
    [InlineData("{\"linkId\":\"l\",\"appId\":\"a\",\"routePath\":[\"/x\"]}")]
    public async Task Rejects_malformed_or_unsafe_responses_without_retrying(string body)
    {
        await using var h = new Harness();
        h.Api.OnResolve = (_, _) => Task.FromResult(Responses.Json(HttpStatusCode.OK, body));
        var uri = Links.Invite;

        var error = await Assert.ThrowsAsync<StrybokServerException>(() => h.Client.ResolveAsync(uri));

        Assert.Equal(200, error.StatusCode);
        Assert.Same(uri, error.Uri);
        Assert.Single(h.Api.Resolves);
    }

    [Theory]
    [InlineData("{\"linkId\":\"l\",\"appId\":\"a\",\"routePath\":\"/x\"}")]
    [InlineData("{\"linkId\":\"l\",\"appId\":\"a\",\"routePath\":\"/x\",\"payload\":null}")]
    [InlineData("{\"linkId\":\"l\",\"appId\":\"a\",\"routePath\":\"/x\",\"payload\":\"text\"}")]
    [InlineData("{\"linkId\":\"l\",\"appId\":\"a\",\"routePath\":\"/x\",\"payload\":[1,2]}")]
    public async Task Payload_is_an_empty_object_when_missing_or_not_an_object(string body)
    {
        await using var h = new Harness();
        h.Api.OnResolve = (_, _) => Task.FromResult(Responses.Json(HttpStatusCode.OK, body));

        var link = await h.Client.ResolveAsync(Links.Invite);

        Assert.Equal("/x", link.RoutePath);
        Assert.Equal(System.Text.Json.JsonValueKind.Object, link.Payload.ValueKind);
        Assert.Equal("{}", link.Payload.GetRawText());
    }

    [Fact]
    public async Task Payload_keeps_nested_json_and_outlives_the_response()
    {
        await using var h = new Harness();
        h.Api.OnResolve = (_, _) => Task.FromResult(Responses.Json(
            HttpStatusCode.OK,
            "{\"linkId\":\"l\",\"appId\":\"a\",\"routePath\":\"/x\",\"payload\":{\"n\":7,\"tags\":[\"a\",\"b\"]}}"));

        var link = await h.Client.ResolveAsync(Links.Invite);
        GC.Collect();

        Assert.Equal(7, link.Payload.GetProperty("n").GetInt32());
        Assert.Equal(2, link.Payload.GetProperty("tags").GetArrayLength());
        Assert.Null(link.GetPayloadString("n")); // Not a string.
    }

    [Fact]
    public async Task The_callers_cancellation_token_stops_the_request()
    {
        await using var h = new Harness();
        h.Api.OnResolve = (_, token) => Async.Never<HttpResponseMessage>(token);
        using var cancellation = new CancellationTokenSource();

        var pending = h.Client.ResolveAsync(Links.Invite, cancellation.Token);
        await Async.Eventually(() => h.Api.Resolves.Count == 1, "the request to start");
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Single(h.Api.Resolves);
    }

    [Fact]
    public async Task Uses_a_supplied_HttpClient_and_does_not_dispose_it()
    {
        var api = new FakeApi();
        using var http = new HttpClient(api);
        var client = new StrybokClient(
            new StrybokOptions { ApiBaseUrl = Links.Api, AllowedHosts = { Links.Host } },
            new FakeLinkSource(),
            http);

        await client.ResolveAsync(Links.Invite);
        await client.DisposeAsync();

        Assert.Single(api.Resolves);
        // Still usable: the SDK does not own it.
        using var response = await http.GetAsync(new Uri("https://api.strybok.com/ping"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void Rejects_invalid_options()
    {
        static StrybokOptions Valid() =>
            new StrybokOptions { ApiBaseUrl = Links.Api, AllowedHosts = { Links.Host } };

        static void Throws(Action<StrybokOptions> change)
        {
            var options = Valid();
            change(options);
            Assert.Throws<ArgumentException>(() => new StrybokClient(options, new FakeLinkSource()));
        }

        new StrybokClient(Valid(), new FakeLinkSource()).Dispose();

        Throws(options => options.ApiBaseUrl = null);
        Throws(options => options.ApiBaseUrl = new Uri("/api", UriKind.Relative));
        Throws(options => options.ApiBaseUrl = new Uri("ftp://api.strybok.com"));
        Throws(options => options.ApiBaseUrl = new Uri("https://api.strybok.com/?key=1"));
        Throws(options => options.AllowedHosts.Clear());
        Throws(options => options.AllowedHosts.Add("https://my-app.strybok.link"));
        Throws(options => options.AllowedHosts.Add("my-app.strybok.link/slug"));
        Throws(options => options.AllowedHosts.Add(" "));
        Throws(options => options.Timeout = TimeSpan.Zero);
        Throws(options => options.MaxRetries = -1);
        Throws(options => options.RetryDelay = TimeSpan.FromMilliseconds(-1));
        Throws(options => options.DedupeWindow = TimeSpan.FromMilliseconds(-1));

        Assert.Throws<ArgumentNullException>(() => new StrybokClient(null!, new FakeLinkSource()));
        Assert.Throws<ArgumentNullException>(() => new StrybokClient(Valid(), null!));
    }

    [Fact]
    public void Sdk_version_fits_the_event_schema()
    {
        Assert.Equal("0.3.0", StrybokClient.SdkVersion);
        Assert.True(StrybokClient.SdkVersion.Length <= 32);
    }
}
