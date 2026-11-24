using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Strybok.Tests;

/// <summary>"Creating links" from the SDK contract.</summary>
public sealed class CreateLinkTests
{
    private const string ClientKey = "pk_0123456789abcdefghijABCDEFGHIJ01";
    private const string SecretMarker = "SECRET-PAYLOAD-MARKER";

    private static Harness WithKey(ILinkSource? source = null, Action<StrybokOptions>? configure = null) =>
        new Harness(source, options =>
        {
            options.ClientKey = ClientKey;
            configure?.Invoke(options);
        });

    private static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static JsonElement Payload() => Element("{\"productId\":42,\"note\":\"" + SecretMarker + "\"}");

    private static Task<CreatedLink> CreateFull(StrybokClient client) =>
        client.CreateLinkAsync(
            "/product/42",
            Payload(),
            "Red shoes",
            "Look at these",
            "https://cdn.example.com/shoes.png");

    private static HttpResponseMessage RateLimited(string? retryAfter)
    {
        var response = Responses.Json(
            (HttpStatusCode)429,
            "{\"error\":{\"code\":\"RATE_LIMITED\",\"message\":\"Too many\",\"details\":{\"period\":\"day\"}}}");
        if (retryAfter is not null) response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        return response;
    }

    // ---------------------------------------------------------------------------------------
    // Success and the request
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Status_201_returns_the_created_link_and_sends_the_contract_request()
    {
        await using var h = WithKey();

        var link = await CreateFull(h.Client);

        Assert.Equal("https://my-app.strybok.link/Ab3xK9", link.Url.OriginalString);
        Assert.Equal(Links.Host, link.Url.Host);
        Assert.Equal("Ab3xK9", link.Slug);
        Assert.Equal("/product/42", link.RoutePath);
        Assert.Equal("link_9", link.LinkId);

        var request = Assert.Single(h.Api.Creates);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.strybok.com/v1/public/links", request.Uri.AbsoluteUri);
        Assert.Equal("application/json", request.ContentType);
        Assert.Equal("application/json", request.Headers["accept"]);
        Assert.Equal(ClientKey, request.Headers["x-strybok-key"]);
        Assert.False(request.Headers.ContainsKey("authorization"));
        Assert.DoesNotContain(ClientKey, request.Uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.DoesNotContain(ClientKey, request.Body, StringComparison.Ordinal);

        var body = request.Json;
        Assert.Equal(JsonValueKind.Object, body.ValueKind);
        Assert.Equal(
            new[] { "description", "imageUrl", "payload", "routePath", "title" },
            body.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.Equal("/product/42", body.GetProperty("routePath").GetString());
        Assert.Equal("Red shoes", body.GetProperty("title").GetString());
        Assert.Equal("Look at these", body.GetProperty("description").GetString());
        Assert.Equal("https://cdn.example.com/shoes.png", body.GetProperty("imageUrl").GetString());
        Assert.Equal(42, body.GetProperty("payload").GetProperty("productId").GetInt32());
        Assert.Equal(SecretMarker, body.GetProperty("payload").GetProperty("note").GetString());

        Assert.Empty(h.Api.Resolves);
        Assert.Empty(h.Api.Events);
        Assert.Empty(h.Delay.RetryPauses);
    }

    [Fact]
    public async Task Status_200_means_an_identical_link_existed_and_is_success_too()
    {
        await using var h = WithKey();
        h.Api.OnCreate = (_, _) => Task.FromResult(Responses.Created(200));

        var link = await h.Client.CreateLinkAsync("/product/42");

        Assert.Equal("Ab3xK9", link.Slug);
        Assert.Single(h.Api.Creates);
    }

    [Fact]
    public async Task Arguments_that_were_not_given_are_omitted_from_the_body()
    {
        await using var h = WithKey();

        await h.Client.CreateLinkAsync("/product/42");
        await h.Client.CreateLinkAsync("/product/42", title: "Only a title");
        await h.Client.CreateLinkAsync("/product/42", Element("{}"));
        await h.Client.CreateLinkAsync("/product/42", description: "d", imageUrl: "https://cdn.example.com/a.png");

        var creates = h.Api.Creates;
        Assert.Equal("{\"routePath\":\"/product/42\"}", creates[0].Body);
        Assert.DoesNotContain("null", creates[0].Body, StringComparison.Ordinal);

        static string[] Names(RecordedCreate create) =>
            create.Json.EnumerateObject().Select(property => property.Name).ToArray();

        Assert.Equal(new[] { "routePath", "title" }, Names(creates[1]));
        Assert.Equal(new[] { "routePath", "payload" }, Names(creates[2]));
        Assert.Equal("{}", creates[2].Json.GetProperty("payload").GetRawText());
        Assert.Equal(new[] { "routePath", "description", "imageUrl" }, Names(creates[3]));
    }

    [Fact]
    public async Task Text_and_nested_payloads_arrive_as_the_same_json()
    {
        await using var h = WithKey();
        var payload = Element("{\"text\":\"quote \\\" and \\\\ and é\",\"flag\":true,\"nothing\":null,\"ratio\":1.5,"
            + "\"big\":12345678901234567890123,\"list\":[1,\"two\",{\"three\":3}]}");

        await h.Client.CreateLinkAsync("/say/\"hi\"/é", payload, "line1\nline2 <b>");

        var body = Assert.Single(h.Api.Creates).Json;
        Assert.Equal("/say/\"hi\"/é", body.GetProperty("routePath").GetString());
        Assert.Equal("line1\nline2 <b>", body.GetProperty("title").GetString());
        var sent = body.GetProperty("payload");
        Assert.Equal("quote \" and \\ and é", sent.GetProperty("text").GetString());
        Assert.True(sent.GetProperty("flag").GetBoolean());
        Assert.Equal(JsonValueKind.Null, sent.GetProperty("nothing").ValueKind);
        Assert.Equal(1.5, sent.GetProperty("ratio").GetDouble());
        Assert.Equal("12345678901234567890123", sent.GetProperty("big").GetRawText());
        Assert.Equal(3, sent.GetProperty("list")[2].GetProperty("three").GetInt32());
    }

    [Fact]
    public async Task Keeps_a_path_prefix_of_the_api_base_url()
    {
        await using var h = WithKey(configure: options => options.ApiBaseUrl = new Uri("https://example.com/strybok/"));

        await h.Client.CreateLinkAsync("/product/42");

        Assert.Equal("https://example.com/strybok/v1/public/links", Assert.Single(h.Api.Creates).Uri.AbsoluteUri);
    }

    [Fact]
    public async Task The_created_url_is_not_checked_against_allowed_hosts()
    {
        await using var h = WithKey();
        h.Api.OnCreate = (_, _) => Task.FromResult(Responses.Json(
            HttpStatusCode.Created,
            "{\"linkId\":\"l\",\"url\":\"https://links.custom-domain.example/Zz9\",\"slug\":\"Zz9\","
                + "\"routePath\":\"/product/42\",\"extra\":true}"));

        var link = await h.Client.CreateLinkAsync("/product/42");

        Assert.Equal("https://links.custom-domain.example/Zz9", link.Url.OriginalString);
    }

    // ---------------------------------------------------------------------------------------
    // Before any network call
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    [InlineData("pk_with space")]
    [InlineData("pk_line\nbreak")]
    [InlineData("pk_é")]
    public async Task Without_a_usable_client_key_it_fails_as_unauthorized_without_a_network_call(string? key)
    {
        await using var h = WithKey(configure: options => options.ClientKey = key);

        var error = await Assert.ThrowsAsync<StrybokUnauthorizedException>(() => CreateFull(h.Client));

        Assert.Null(error.Uri);
        Assert.Empty(h.Api.Creates);
    }

    [Fact]
    public async Task The_client_key_is_trimmed_and_checked_before_the_route()
    {
        await using var h = WithKey(configure: options => options.ClientKey = "  " + ClientKey + "\n");
        await h.Client.CreateLinkAsync("/a");
        Assert.Equal(ClientKey, Assert.Single(h.Api.Creates).Headers["x-strybok-key"]);

        await using var keyless = new Harness();
        await Assert.ThrowsAsync<StrybokUnauthorizedException>(() => keyless.Client.CreateLinkAsync("no-slash"));
        Assert.Empty(keyless.Api.Creates);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("product/42")]
    [InlineData("//evil.example.com/x")]
    [InlineData("//")]
    [InlineData("https://evil.example.com/x")]
    [InlineData(" /product")]
    public async Task A_bad_route_path_fails_as_validation_without_a_network_call(string? routePath)
    {
        await using var h = WithKey();

        var error = await Assert.ThrowsAsync<StrybokValidationException>(
            () => h.Client.CreateLinkAsync(routePath!, Payload(), "t"));

        Assert.Null(error.StatusCode);
        Assert.Empty(error.Issues);
        Assert.Contains("routePath", error.Message, StringComparison.Ordinal);
        Assert.Empty(h.Api.Creates);
    }

    [Fact]
    public async Task A_route_path_may_be_512_characters_but_not_longer()
    {
        await using var h = WithKey();

        await h.Client.CreateLinkAsync("/" + new string('a', 511));
        await h.Client.CreateLinkAsync("/");
        Assert.Equal(2, h.Api.Creates.Count);

        var error = await Assert.ThrowsAsync<StrybokValidationException>(
            () => h.Client.CreateLinkAsync("/" + new string('a', 512)));
        Assert.Null(error.StatusCode);
        Assert.Equal(2, h.Api.Creates.Count);
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("42")]
    [InlineData("\"SECRET-PAYLOAD-MARKER\"")]
    [InlineData("true")]
    [InlineData("null")]
    public async Task A_payload_that_is_not_a_json_object_fails_as_validation_without_a_network_call(string json)
    {
        await using var h = WithKey();

        var error = await Assert.ThrowsAsync<StrybokValidationException>(
            () => h.Client.CreateLinkAsync("/product/42", Element(json)));

        Assert.Equal("payload must be a JSON object", error.Message);
        Assert.Null(error.StatusCode);
        Assert.DoesNotContain(SecretMarker, error.ToString(), StringComparison.Ordinal);
        Assert.Empty(h.Api.Creates);
    }

    [Fact]
    public async Task An_unusable_payload_element_fails_as_validation_without_a_network_call()
    {
        await using var h = WithKey();

        // default(JsonElement) is not an object.
        await Assert.ThrowsAsync<StrybokValidationException>(
            () => h.Client.CreateLinkAsync("/product/42", default(JsonElement)));

        // An element whose document was disposed cannot be read any more.
        JsonElement stale;
        using (var document = JsonDocument.Parse("{\"note\":\"" + SecretMarker + "\"}"))
        {
            stale = document.RootElement;
        }
        var error = await Assert.ThrowsAsync<StrybokValidationException>(
            () => h.Client.CreateLinkAsync("/product/42", stale));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(SecretMarker, error.ToString(), StringComparison.Ordinal);

        Assert.Empty(h.Api.Creates);
    }

    // ---------------------------------------------------------------------------------------
    // Answers
    // ---------------------------------------------------------------------------------------

    public static IEnumerable<object[]> MalformedSuccessBodies()
    {
        var bodies = new[]
        {
            "",
            "not json",
            "[]",
            "null",
            "\"https://my-app.strybok.link/Ab3xK9\"",
            "{}",
            "{\"url\":\"https://my-app.strybok.link/Ab3xK9\",\"slug\":\"Ab3xK9\",\"routePath\":\"/product/42\"}",
            "{\"linkId\":\"link_9\",\"slug\":\"Ab3xK9\",\"routePath\":\"/product/42\"}",
            "{\"linkId\":\"link_9\",\"url\":\"https://my-app.strybok.link/Ab3xK9\",\"routePath\":\"/product/42\"}",
            "{\"linkId\":\"link_9\",\"url\":\"https://my-app.strybok.link/Ab3xK9\",\"slug\":\"Ab3xK9\"}",
            "{\"linkId\":9,\"url\":\"https://my-app.strybok.link/Ab3xK9\",\"slug\":\"Ab3xK9\",\"routePath\":\"/product/42\"}",
            "{\"linkId\":\"link_9\",\"url\":\"https://my-app.strybok.link/Ab3xK9\",\"slug\":null,\"routePath\":\"/product/42\"}",
            "{\"linkId\":\"link_9\",\"url\":\"http://my-app.strybok.link/Ab3xK9\",\"slug\":\"Ab3xK9\",\"routePath\":\"/product/42\"}",
            "{\"linkId\":\"link_9\",\"url\":\"myapp://Ab3xK9\",\"slug\":\"Ab3xK9\",\"routePath\":\"/product/42\"}",
            "{\"linkId\":\"link_9\",\"url\":\"\",\"slug\":\"Ab3xK9\",\"routePath\":\"/product/42\"}",
            "{\"linkId\":\"link_9\",\"url\":\"https://\",\"slug\":\"Ab3xK9\",\"routePath\":\"/product/42\"}",
            "{\"linkId\":\"link_9\",\"url\":[\"https://x\"],\"slug\":\"Ab3xK9\",\"routePath\":\"/product/42\"}",
        };
        foreach (var status in new[] { 200, 201 })
        {
            foreach (var body in bodies) yield return new object[] { status, body };
        }
    }

    [Theory]
    [MemberData(nameof(MalformedSuccessBodies))]
    public async Task A_malformed_success_body_is_a_server_error_with_the_status_and_is_not_retried(int status, string body)
    {
        await using var h = WithKey();
        h.Api.OnCreate = (_, _) => Task.FromResult(Responses.Json((HttpStatusCode)status, body));

        var error = await Assert.ThrowsAsync<StrybokServerException>(() => h.Client.CreateLinkAsync("/product/42"));

        Assert.Equal(status, error.StatusCode);
        Assert.Null(error.Uri);
        Assert.Single(h.Api.Creates);
    }

    [Fact]
    public async Task Status_401_is_unauthorized_and_is_not_retried()
    {
        await using var h = WithKey();
        h.Api.OnCreate = (_, _) => Task.FromResult(Responses.Json(
            HttpStatusCode.Unauthorized,
            "{\"error\":{\"code\":\"UNAUTHENTICATED\",\"message\":\"Authentication required\",\"details\":{}}}"));

        await Assert.ThrowsAsync<StrybokUnauthorizedException>(() => CreateFull(h.Client));

        Assert.Single(h.Api.Creates);
    }

    [Fact]
    public async Task Status_403_is_route_not_allowed_with_the_route_and_is_not_retried()
    {
        await using var h = WithKey();
        h.Api.OnCreate = (_, _) => Task.FromResult(Responses.Json(
            HttpStatusCode.Forbidden,
            "{\"error\":{\"code\":\"ROUTE_NOT_ALLOWED\",\"message\":\"Route not allowed\",\"details\":{}}}"));

        var error = await Assert.ThrowsAsync<RouteNotAllowedException>(() => CreateFull(h.Client));

        Assert.Equal("/product/42", error.RoutePath);
        Assert.Single(h.Api.Creates);
    }

    [Theory]
    [InlineData("60", 60)]
    [InlineData(" 86400 ", 86400)]
    [InlineData("0", 0)]
    [InlineData("99999999999999999999999999", int.MaxValue)]
    public async Task Status_429_is_rate_limited_with_retry_after_and_is_never_retried(string header, int seconds)
    {
        await using var h = WithKey();
        h.Api.OnCreate = (_, _) => Task.FromResult(RateLimited(header));

        var error = await Assert.ThrowsAsync<StrybokRateLimitedException>(() => CreateFull(h.Client));

        Assert.Equal(TimeSpan.FromSeconds(seconds), error.RetryAfter);
        Assert.Single(h.Api.Creates);
        Assert.Empty(h.Delay.RetryPauses);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Wed, 21 Oct 2026 07:28:00 GMT")]
    [InlineData("-5")]
    [InlineData("1.5")]
    [InlineData("soon")]
    public async Task Status_429_without_a_number_of_seconds_has_no_retry_after(string? header)
    {
        await using var h = WithKey();
        h.Api.OnCreate = (_, _) => Task.FromResult(RateLimited(header));

        var error = await Assert.ThrowsAsync<StrybokRateLimitedException>(() => h.Client.CreateLinkAsync("/product/42"));

        Assert.Null(error.RetryAfter);
        Assert.Single(h.Api.Creates);
    }

    [Fact]
    public async Task A_typed_retry_after_header_is_understood()
    {
        await using var h = WithKey();
        h.Api.OnCreate = (_, _) =>
        {
            var response = Responses.Status(429);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return Task.FromResult(response);
        };

        var error = await Assert.ThrowsAsync<StrybokRateLimitedException>(() => h.Client.CreateLinkAsync("/product/42"));

        Assert.Equal(TimeSpan.FromSeconds(120), error.RetryAfter);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(413)]
    [InlineData(422)]
    public async Task Status_400_413_and_422_are_validation_with_the_servers_message_and_issues(int status)
    {
        await using var h = WithKey();
        h.Api.OnCreate = (_, _) => Task.FromResult(Responses.Json(
            (HttpStatusCode)status,
            "{\"error\":{\"code\":\"VALIDATION_ERROR\",\"message\":\"The request is invalid\",\"details\":{\"issues\":["
                + "{\"path\":\"title\",\"message\":\"Too long\"},{\"path\":\"\",\"message\":\"Unrecognized key\"},"
                + "\"junk\",{\"path\":7}]}}}"));

        var error = await Assert.ThrowsAsync<StrybokValidationException>(() => CreateFull(h.Client));

        Assert.Equal(status, error.StatusCode);
        Assert.Equal("The request is invalid", error.Message);
        Assert.Equal(2, error.Issues.Count);
        Assert.Equal("title", error.Issues[0].Path);
        Assert.Equal("Too long", error.Issues[0].Message);
        Assert.Equal("title: Too long", error.Issues[0].ToString());
        Assert.Equal(string.Empty, error.Issues[1].Path);
        Assert.Equal("Unrecognized key", error.Issues[1].ToString());
        Assert.Single(h.Api.Creates);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>")]
    [InlineData("[]")]
    [InlineData("{\"error\":\"nope\"}")]
    [InlineData("{\"error\":{\"message\":7,\"details\":[]}}")]
    public async Task A_validation_answer_without_a_readable_body_still_is_validation(string body)
    {
        await using var h = WithKey();
        h.Api.OnCreate = (_, _) => Task.FromResult(Responses.Json((HttpStatusCode)413, body));

        var error = await Assert.ThrowsAsync<StrybokValidationException>(() => h.Client.CreateLinkAsync("/product/42"));

        Assert.Equal(413, error.StatusCode);
        Assert.NotEqual(string.Empty, error.Message);
        Assert.Empty(error.Issues);
    }

    [Theory]
    [InlineData(402)]
    [InlineData(404)]
    [InlineData(405)]
    [InlineData(409)]
    [InlineData(418)]
    [InlineData(202)]
    [InlineData(204)]
    [InlineData(304)]
    public async Task Other_client_errors_and_unexpected_statuses_are_server_errors_and_are_not_retried(int status)
    {
        await using var h = WithKey();
        h.Api.OnCreate = (_, _) => Task.FromResult(Responses.Json((HttpStatusCode)status, Responses.CreatedBody));

        var error = await Assert.ThrowsAsync<StrybokServerException>(() => h.Client.CreateLinkAsync("/product/42"));

        Assert.Equal(status, error.StatusCode);
        Assert.Single(h.Api.Creates);
        Assert.Empty(h.Delay.RetryPauses);
    }

    // ---------------------------------------------------------------------------------------
    // Timeout and retries
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Server_errors_are_retried_with_a_doubling_pause_and_exactly_the_same_body()
    {
        await using var h = WithKey();
        var calls = 0;
        h.Api.OnCreate = (_, _) =>
            Task.FromResult(Interlocked.Increment(ref calls) < 3 ? Responses.Status(503) : Responses.Created());

        var link = await CreateFull(h.Client);

        Assert.Equal("link_9", link.LinkId);
        Assert.Equal(
            new[] { TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(800) },
            h.Delay.RetryPauses.ToArray());
        var creates = h.Api.Creates;
        Assert.Equal(3, creates.Count);
        foreach (var request in creates)
        {
            Assert.Equal(creates[0].Body, request.Body);
            Assert.Equal(creates[0].Uri, request.Uri);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("application/json", request.ContentType);
            Assert.Equal(ClientKey, request.Headers["x-strybok-key"]);
        }
    }

    [Fact]
    public async Task A_server_error_after_all_retries_reports_the_last_status()
    {
        await using var h = WithKey();
        var calls = 0;
        h.Api.OnCreate = (_, _) =>
            Task.FromResult(Responses.Status(Interlocked.Increment(ref calls) < 3 ? 500 : 502));

        var error = await Assert.ThrowsAsync<StrybokServerException>(() => h.Client.CreateLinkAsync("/product/42"));

        Assert.Equal(502, error.StatusCode);
        Assert.Equal(3, h.Api.Creates.Count);
    }

    [Fact]
    public async Task Transport_failures_are_retried_and_then_reported_as_network()
    {
        await using var h = WithKey();
        h.Api.OnCreate = (_, _) => throw new HttpRequestException("connection reset");

        var error = await Assert.ThrowsAsync<StrybokNetworkException>(() => CreateFull(h.Client));

        Assert.IsType<HttpRequestException>(error.InnerException);
        Assert.Null(error.Uri);
        var creates = h.Api.Creates;
        Assert.Equal(3, creates.Count);
        Assert.Equal(creates[0].Body, creates[2].Body);
    }

    [Fact]
    public async Task A_retry_after_a_lost_answer_gets_the_existing_link()
    {
        await using var h = WithKey();
        var calls = 0;
        h.Api.OnCreate = (_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new HttpRequestException("lost");
            return Task.FromResult(Responses.Created(200));
        };

        var link = await h.Client.CreateLinkAsync("/product/42");

        Assert.Equal("Ab3xK9", link.Slug);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task No_answer_in_time_is_retried_and_then_reported_as_timeout()
    {
        await using var h = WithKey();
        h.Delay.FireTimeouts = true;
        var cancelled = 0;
        h.Api.OnCreate = (_, token) =>
        {
            token.Register(() => Interlocked.Increment(ref cancelled));
            return Async.Never<HttpResponseMessage>(token);
        };

        var error = await Assert.ThrowsAsync<StrybokTimeoutException>(() => CreateFull(h.Client));

        Assert.Null(error.Uri);
        Assert.Contains("create-link", error.Message, StringComparison.Ordinal);
        var creates = h.Api.Creates;
        Assert.Equal(3, creates.Count);
        Assert.Equal(creates[0].Body, creates[1].Body);
        Assert.Equal(creates[0].Body, creates[2].Body);
        await Async.Eventually(() => Volatile.Read(ref cancelled) == 3, "the abandoned requests to be cancelled");
    }

    [Fact]
    public async Task Zero_retries_means_a_single_attempt()
    {
        await using var h = WithKey(configure: options => options.MaxRetries = 0);
        h.Api.OnCreate = (_, _) => Task.FromResult(Responses.Status(503));

        var error = await Assert.ThrowsAsync<StrybokServerException>(() => h.Client.CreateLinkAsync("/product/42"));

        Assert.Equal(503, error.StatusCode);
        Assert.Single(h.Api.Creates);
        Assert.Empty(h.Delay.RetryPauses);
    }

    // ---------------------------------------------------------------------------------------
    // Errors, cancellation and independence from link handling
    // ---------------------------------------------------------------------------------------

    public static IEnumerable<object[]> FailingAnswers()
    {
        yield return new object[] { "401", typeof(StrybokUnauthorizedException) };
        yield return new object[] { "403", typeof(RouteNotAllowedException) };
        yield return new object[] { "429", typeof(StrybokRateLimitedException) };
        yield return new object[] { "422", typeof(StrybokValidationException) };
        yield return new object[] { "404", typeof(StrybokServerException) };
        yield return new object[] { "500", typeof(StrybokServerException) };
        yield return new object[] { "malformed", typeof(StrybokServerException) };
        yield return new object[] { "network", typeof(StrybokNetworkException) };
        yield return new object[] { "timeout", typeof(StrybokTimeoutException) };
    }

    [Theory]
    [MemberData(nameof(FailingAnswers))]
    public async Task Errors_never_contain_the_client_key_or_the_payload(string answer, Type expected)
    {
        await using var h = WithKey(configure: options => options.MaxRetries = 1);
        h.Delay.FireTimeouts = answer == "timeout";
        h.Api.OnCreate = (_, token) => answer switch
        {
            "malformed" => Task.FromResult(Responses.Json(HttpStatusCode.Created, "{}")),
            "network" => throw new HttpRequestException("offline"),
            "timeout" => Async.Never<HttpResponseMessage>(token),
            "422" => Task.FromResult(Responses.Json(
                (HttpStatusCode)422, "{\"error\":{\"message\":\"The request is invalid\"}}")),
            _ => Task.FromResult(Responses.Status(int.Parse(answer, System.Globalization.CultureInfo.InvariantCulture))),
        };

        var error = await Assert.ThrowsAnyAsync<StrybokException>(() => CreateFull(h.Client));

        Assert.IsType(expected, error);
        Assert.Null(error.Uri);
        foreach (var text in new[] { error.Message, error.ToString() })
        {
            Assert.DoesNotContain(ClientKey, text, StringComparison.Ordinal);
            Assert.DoesNotContain("pk_", text, StringComparison.Ordinal);
            Assert.DoesNotContain(SecretMarker, text, StringComparison.Ordinal);
            Assert.DoesNotContain("productId", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Errors_found_before_sending_do_not_contain_the_client_key_or_the_payload()
    {
        await using var h = WithKey();

        var error = await Assert.ThrowsAsync<StrybokValidationException>(
            () => h.Client.CreateLinkAsync("nope", Payload()));

        Assert.DoesNotContain(SecretMarker, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(ClientKey, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Works_without_initialize_and_never_touches_link_handling()
    {
        var source = new FakeLinkSource { InitialLink = Links.Invite };
        await using var h = WithKey(source);
        h.Listen();

        var link = await h.Client.CreateLinkAsync("/product/42");
        Assert.Equal("Ab3xK9", link.Slug);

        h.Api.OnCreate = (_, _) => Task.FromResult(Responses.Status(401));
        await Assert.ThrowsAsync<StrybokUnauthorizedException>(() => h.Client.CreateLinkAsync("/product/42"));
        await Assert.ThrowsAsync<StrybokValidationException>(() => h.Client.CreateLinkAsync("nope"));
        await Async.Settle();

        Assert.Empty(source.Calls); // Neither subscribed nor asked for the launch link.
        Assert.False(source.HasListener);
        Assert.Empty(h.Api.Resolves);
        Assert.Empty(h.Api.Events);
        Assert.Empty(h.Errors); // Errors of CreateLinkAsync never go to OnError.
        Assert.Empty(h.Received);
    }

    [Fact]
    public async Task Is_never_on_the_start_path()
    {
        var source = new FakeLinkSource { InitialLink = Links.Invite };
        await using var h = WithKey(source);
        h.Listen();

        await h.Client.InitializeAsync();
        source.Emit(Links.On("second"));
        await h.ReceivedCount(2);
        await Async.Eventually(() => h.Api.Events.Count == 2, "two app_open events");
        Assert.Empty(h.Api.Creates); // Starting, resolving and delivering never create links.

        // A create call that hangs does not hold up a link that arrives meanwhile.
        h.Api.OnCreate = (_, token) => Async.Never<HttpResponseMessage>(token);
        using var cancellation = new CancellationTokenSource();
        var hanging = h.Client.CreateLinkAsync("/product/42", cancellationToken: cancellation.Token);
        await Async.Eventually(() => h.Api.Creates.Count == 1, "the create request to start");
        source.Emit(Links.On("third"));
        await h.ReceivedCount(3);
        Assert.False(hanging.IsCompleted);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hanging);
        Assert.Empty(h.Errors);
    }

    [Fact]
    public async Task The_callers_cancellation_token_stops_the_request()
    {
        await using var h = WithKey();
        h.Api.OnCreate = (_, token) => Async.Never<HttpResponseMessage>(token);
        using var cancellation = new CancellationTokenSource();

        var pending = h.Client.CreateLinkAsync("/product/42", cancellationToken: cancellation.Token);
        await Async.Eventually(() => h.Api.Creates.Count == 1, "the request to start");
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Single(h.Api.Creates);
    }

    [Fact]
    public async Task After_dispose_it_fails_with_object_disposed_and_a_call_in_flight_is_aborted()
    {
        var h = WithKey();
        h.Api.OnCreate = (_, token) => Async.Never<HttpResponseMessage>(token);
        var inFlight = h.Client.CreateLinkAsync("/product/42");
        await Async.Eventually(() => h.Api.Creates.Count == 1, "the request to start");

        await h.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => inFlight);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => h.Client.CreateLinkAsync("/product/42"));
        Assert.Single(h.Api.Creates);
    }

    [Fact]
    public async Task Uses_a_supplied_HttpClient()
    {
        var api = new FakeApi();
        using var http = new HttpClient(api);
        await using var client = new StrybokClient(
            new StrybokOptions { ApiBaseUrl = Links.Api, AllowedHosts = { Links.Host }, ClientKey = ClientKey },
            new FakeLinkSource(),
            http);

        var link = await client.CreateLinkAsync("/product/42", title: "Red shoes");

        Assert.Equal("link_9", link.LinkId);
        Assert.Equal(ClientKey, Assert.Single(api.Creates).Headers["x-strybok-key"]);
    }

    [Fact]
    public void The_client_key_is_optional_and_off_by_default()
    {
        Assert.Null(new StrybokOptions().ClientKey);
    }

    [Fact]
    public void A_created_link_can_be_built_for_tests()
    {
        var link = new CreatedLink(new Uri("https://my-app.strybok.link/Ab3xK9"), "Ab3xK9", "/product/42", "link_9");

        Assert.Equal("CreatedLink(https://my-app.strybok.link/Ab3xK9, linkId: link_9)", link.ToString());
        Assert.Throws<ArgumentNullException>(() => new CreatedLink(null!, "s", "/r", "l"));
    }
}
