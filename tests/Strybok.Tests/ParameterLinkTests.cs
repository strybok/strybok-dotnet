using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Strybok.Tests;

/// <summary>
/// The shared test vectors of the SDK contract ("Links without an API call"), read from the
/// byte-identical copy in <c>Fixtures</c>, which the project copies next to the tests.
/// </summary>
internal static class ParameterLinkVectors
{
    private const string FileName = "parameter-links-vectors.json";

    private static readonly Lazy<JsonElement> Root = new Lazy<JsonElement>(Load);

    public static IReadOnlyList<string> AllowedHosts => Strings(Root.Value.GetProperty("allowedHosts"))!;

    /// <summary>name, url, payload keys (<c>null</c>: no option), lookup (<c>null</c>: not ours).</summary>
    public static IEnumerable<(string Name, string Url, string[]? PayloadKeys, string? Lookup)> Lookups =>
        Root.Value.GetProperty("lookups").EnumerateArray().Select(entry => (
            entry.GetProperty("name").GetString()!,
            entry.GetProperty("url").GetString()!,
            PayloadKeysOf(entry),
            entry.GetProperty("lookup").GetString()));

    /// <summary>name, payload keys (<c>null</c>: no option), valid.</summary>
    public static IEnumerable<(string Name, string[]? PayloadKeys, bool Valid)> Configurations =>
        Root.Value.GetProperty("configurations").EnumerateArray().Select(entry => (
            entry.GetProperty("name").GetString()!,
            PayloadKeysOf(entry),
            entry.GetProperty("valid").GetBoolean()));

    private static string[]? PayloadKeysOf(JsonElement entry)
    {
        var option = entry.GetProperty("parameterLinks");
        return option.ValueKind == JsonValueKind.Null ? null : Strings(option.GetProperty("payloadKeys"));
    }

    private static string[]? Strings(JsonElement list) =>
        list.EnumerateArray().Select(item => item.GetString()!).ToArray();

    private static JsonElement Load()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FindFile()));
        return document.RootElement.Clone();
    }

    /// <summary>Copied to the test output by the project; next to this file when run from the sources.</summary>
    private static string FindFile([CallerFilePath] string thisFile = "")
    {
        var candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, "Fixtures", FileName) };
        if (thisFile.Length > 0) candidates.Add(Path.Combine(Path.GetDirectoryName(thisFile) ?? ".", "Fixtures", FileName));
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(FileName + " not found at " + string.Join(", ", candidates));
    }
}

/// <summary>Links without an API call (SDK contract; decision 0035): the shared vectors and the behaviour.</summary>
public sealed class ParameterLinkTests
{
    private const string ResolvePrefix = "https://api.strybok.com/v1/public/resolve?url=";
    private const string ClickId = "dc_0123456789abcdefghijABCDEFGHIJ01";

    public static IEnumerable<object?[]> LookupCases() =>
        ParameterLinkVectors.Lookups.Select(vector => new object?[] { vector.Name, vector.Url, vector.PayloadKeys, vector.Lookup });

    public static IEnumerable<object?[]> ConfigurationCases() =>
        ParameterLinkVectors.Configurations.Select(vector => new object?[] { vector.Name, vector.PayloadKeys, vector.Valid });

    private static ParameterLinksOptions? Option(IEnumerable<string>? payloadKeys) =>
        payloadKeys is null ? null : new ParameterLinksOptions { PayloadKeys = new List<string>(payloadKeys) };

    /// <summary>What resolve is asked for <paramref name="lookup"/>, encoded as the SDK encodes <c>url</c>.</summary>
    private static string ResolveUrlOf(string lookup) => ResolvePrefix + Uri.EscapeDataString(lookup);

    private static Harness Make(
        IEnumerable<string>? payloadKeys,
        FakeLinkSource? source = null,
        Action<StrybokOptions>? configure = null,
        IEnumerable<string>? allowedHosts = null) =>
        new Harness(source, options =>
        {
            options.AllowedHosts = new List<string>(allowedHosts ?? ParameterLinkVectors.AllowedHosts);
            options.ParameterLinks = Option(payloadKeys);
            configure?.Invoke(options);
        });

    private static StrybokOptions PlainOptions(ParameterLinksOptions? option) => new StrybokOptions
    {
        ApiBaseUrl = Links.Api,
        AllowedHosts = { Links.Host },
        ParameterLinks = option,
    };

    // ------------------------------------------------------------------ the shared vectors

    [Fact]
    public void The_vectors_file_has_cases_of_both_kinds()
    {
        Assert.Equal(new[] { "myapp.strybok.link", "go.example.com" }, ParameterLinkVectors.AllowedHosts);
        Assert.NotEmpty(LookupCases());
        Assert.NotEmpty(ConfigurationCases());
    }

    [Theory]
    [MemberData(nameof(LookupCases))]
    public async Task Vector_lookup(string name, string url, string[]? payloadKeys, string? lookup)
    {
        // resolve(url)
        await using (var h = Make(payloadKeys))
        {
            var uri = new Uri(url);
            if (lookup is null)
            {
                Assert.False(h.Client.IsStrybokUri(url), name);
                await Assert.ThrowsAsync<UnsupportedLinkException>(() => h.Client.ResolveAsync(uri));
                Assert.Empty(h.Api.Resolves);
            }
            else
            {
                Assert.True(h.Client.IsStrybokUri(url), name);
                var link = await h.Client.ResolveAsync(uri);
                Assert.Same(uri, link.Uri);
                Assert.Equal(ResolveUrlOf(lookup), Assert.Single(h.Api.Resolves).Uri.OriginalString);
            }
        }

        // The automatic flow.
        var source = new FakeLinkSource();
        await using var automatic = Make(payloadKeys, source);
        automatic.Listen();
        await automatic.Client.InitializeAsync();
        source.Emit(new Uri(url));
        if (lookup is null)
        {
            await Async.Settle();
            Assert.Empty(automatic.Api.Resolves);
            Assert.Empty(automatic.Received);
            Assert.Empty(automatic.Errors);
        }
        else
        {
            await automatic.ReceivedCount(1);
            Assert.Equal(ResolveUrlOf(lookup), Assert.Single(automatic.Api.Resolves).Uri.OriginalString);
            Assert.Empty(automatic.Errors);
        }
    }

    [Theory]
    [MemberData(nameof(ConfigurationCases))]
    public void Vector_configuration(string name, string[]? payloadKeys, bool valid)
    {
        var options = PlainOptions(Option(payloadKeys));
        var error = Record.Exception(() => new StrybokClient(options, new FakeLinkSource()).Dispose());
        if (valid)
        {
            Assert.True(error is null, name + ": " + error);
            return;
        }

        // Rejected like an invalid ApiBaseUrl.
        var rejected = Assert.IsType<ArgumentException>(error);
        Assert.Equal("options", rejected.ParamName);
        Assert.StartsWith("ParameterLinks: ", rejected.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ configuration

    [Fact]
    public void The_option_is_absent_by_default()
    {
        Assert.Null(new StrybokOptions().ParameterLinks);
        Assert.Empty(new ParameterLinksOptions().PayloadKeys);
        Assert.Equal(new[] { "id", "lang" }, new ParameterLinksOptions("id", "lang").PayloadKeys);
        Assert.Throws<ArgumentNullException>(() => new ParameterLinksOptions(null!));
    }

    [Fact]
    public void Null_keys_are_rejected()
    {
        foreach (var option in new[]
        {
            new ParameterLinksOptions { PayloadKeys = null! },
            new ParameterLinksOptions { PayloadKeys = new List<string> { "id", null! } },
        })
        {
            var error = Assert.Throws<ArgumentException>(() => new StrybokClient(PlainOptions(option), new FakeLinkSource()));
            Assert.Equal("options", error.ParamName);
        }
    }

    [Fact]
    public async Task The_client_keeps_the_keys_it_was_created_with()
    {
        var keys = new List<string> { "id" };
        StrybokOptions? captured = null;
        await using var h = new Harness(configure: options =>
        {
            options.ParameterLinks = new ParameterLinksOptions { PayloadKeys = keys };
            captured = options;
        });
        keys.Add("lang");
        captured!.ParameterLinks = null;

        await h.Client.ResolveAsync(new Uri("https://my-app.strybok.link/product/42?lang=uk&id=7"));

        Assert.Equal(
            ResolveUrlOf("https://my-app.strybok.link/product/42?id=7"),
            Assert.Single(h.Api.Resolves).Uri.OriginalString);
    }

    // ------------------------------------------------------------------ behaviour

    [Fact]
    public async Task The_route_and_payload_come_from_the_answer_not_from_the_tapped_url()
    {
        var source = new FakeLinkSource();
        await using var h = Make(new[] { "id", "lang" }, source, allowedHosts: new[] { Links.Host });
        h.Api.OnResolve = (_, _) => Task.FromResult(Responses.Json(
            HttpStatusCode.OK,
            "{\"linkId\":\"plink_path\",\"appId\":\"app_1\",\"routePath\":\"/catalog/item\","
                + "\"payload\":{\"id\":\"from-server\"},\"campaign\":{\"source\":\"mail\"}}"));
        h.Listen();
        await h.Client.InitializeAsync();

        var tapped = new Uri("https://my-app.strybok.link/product/42?id=7&utm_source=x#top");
        source.Emit(tapped);
        await h.ReceivedCount(1);

        var link = Assert.Single(h.Received);
        Assert.Same(tapped, link.Uri);
        Assert.Equal("/catalog/item", link.RoutePath);
        Assert.Equal("from-server", link.GetPayloadString("id"));
        Assert.Equal("{\"id\":\"from-server\"}", link.Payload.GetRawText());
        Assert.Equal("plink_path", link.LinkId);
        Assert.Equal(new LinkCampaign(source: "mail"), link.Campaign);
        Assert.False(link.IsDeferred);

        // The same through ResolveAsync, for a long link.
        var resolved = await h.Client.ResolveAsync(
            new Uri("https://my-app.strybok.link/?link=https%3A%2F%2Fexample.com%2Fp%3Fid%3D7&apn=com.example.app"));
        Assert.Equal("/catalog/item", resolved.RoutePath);
        Assert.Equal("from-server", resolved.GetPayloadString("id"));
    }

    [Fact]
    public async Task App_open_carries_the_link_id_the_server_returned()
    {
        var source = new FakeLinkSource();
        await using var h = Make(new[] { "id" }, source, allowedHosts: new[] { Links.Host });
        h.Api.OnResolve = (_, _) => Task.FromResult(Responses.Json(
            HttpStatusCode.OK, "{\"linkId\":\"plink_firebase\",\"appId\":\"app_1\",\"routePath\":\"/p\",\"payload\":{}}"));
        h.Listen();
        await h.Client.InitializeAsync();

        source.Emit(new Uri("https://my-app.strybok.link/?link=https%3A%2F%2Fexample.com%2Fp"));
        await Async.Eventually(() => h.Api.Events.Count == 1, "the app_open event");

        var body = Assert.Single(h.Api.Events).Body;
        Assert.Equal("plink_firebase", body.GetProperty("linkId").GetString());
        Assert.Equal("app_open", body.GetProperty("type").GetString());
        Assert.Equal("0.4.0", body.GetProperty("sdkVersion").GetString());
        Assert.Equal(
            new[] { "eventId", "linkId", "platform", "sdkVersion", "type" },
            body.EnumerateObject().Select(property => property.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task A_path_that_is_no_link_is_reported_as_not_found()
    {
        var source = new FakeLinkSource();
        await using var h = Make(Array.Empty<string>(), source, allowedHosts: new[] { Links.Host });
        h.Api.OnResolve = (_, _) => Task.FromResult(Responses.Status(404));
        h.Listen();
        await h.Client.InitializeAsync();

        var tapped = new Uri("https://my-app.strybok.link/no/such/route");
        source.Emit(tapped);
        await Async.Eventually(() => h.Errors.Count == 1, "the error");

        var error = Assert.IsType<LinkNotFoundException>(Assert.Single(h.Errors));
        Assert.Same(tapped, error.Uri);
        Assert.Empty(h.Received);
        Assert.Single(h.Api.Resolves); // a 404 is not retried
    }

    [Fact]
    public async Task Dedupe_compares_the_full_url_as_received()
    {
        var source = new FakeLinkSource();
        await using var h = Make(new[] { "id" }, source, allowedHosts: new[] { Links.Host });
        h.Listen();
        await h.Client.InitializeAsync();

        source.Emit(new Uri("https://my-app.strybok.link/product/42?id=7"));
        source.Emit(new Uri("https://my-app.strybok.link/product/42?id=7")); // one tap reported twice
        source.Emit(new Uri("https://my-app.strybok.link/product/42?id=7&ref=x")); // another URL, the same lookup
        await h.ReceivedCount(2);
        await Async.Settle();

        Assert.Equal(2, h.Received.Count);
        var expected = ResolveUrlOf("https://my-app.strybok.link/product/42?id=7");
        Assert.Equal(new[] { expected, expected }, h.Api.Resolves.Select(request => request.Uri.OriginalString).ToArray());
    }

    [Theory]
    [InlineData("https://my-app.strybok.link/product/42")]
    [InlineData("https://my-app.strybok.link/")]
    [InlineData("https://my-app.strybok.link/?link=https%3A%2F%2Fexample.com%2Fp")]
    public async Task A_deferred_answer_with_a_url_that_is_not_one_slug_is_still_refused(string url)
    {
        var store = new MemoryStore();
        await using var h = AndroidWithOption(
            store,
            "{\"linkId\":\"l\",\"appId\":\"app_1\",\"routePath\":\"/product/42\",\"url\":\"" + url + "\",\"deferred\":true}");
        h.Listen();
        await h.Client.InitializeAsync();
        await Async.Eventually(() => h.Client.DeferredLinkStatus == DeferredLinkStatus.Failed, "the check to fail");
        await Async.Settle();

        Assert.Empty(h.Received);
        Assert.Single(h.Api.Deferred);
        Assert.Equal("1", store.Get(MemoryStore.Done));
    }

    [Fact]
    public async Task A_deferred_answer_with_a_slug_is_delivered_with_the_option_set()
    {
        await using var h = AndroidWithOption(
            new MemoryStore(),
            "{\"linkId\":\"l\",\"appId\":\"app_1\",\"routePath\":\"/product/42\",\"url\":\"https://my-app.strybok.link/Ab3xK9\",\"deferred\":true}");
        h.Listen();
        await h.Client.InitializeAsync();
        await h.ReceivedCount(1);

        Assert.True(Assert.Single(h.Received).IsDeferred);
        Assert.Equal(DeferredLinkStatus.Delivered, h.Client.DeferredLinkStatus);
    }

    [Fact]
    public async Task A_pasted_path_link_is_not_ours()
    {
        await using var h = Make(new[] { "id" }, configure: options =>
        {
            options.Platform = StrybokPlatform.iOS;
            options.DeferredLinks = true;
            options.IosBundleId = "com.example.App";
            options.DeferredLinkStore = new MemoryStore();
        }, allowedHosts: new[] { Links.Host });
        await h.Client.InitializeAsync();

        // The page before the App Store copies a stored link's URL; a path link never has a click id.
        Assert.Equal(
            PastedLinkResult.NotOurs,
            await h.Client.RedeemPastedLinkAsync("https://my-app.strybok.link/product/42?strybok_click=" + ClickId));
        Assert.Empty(h.Api.Deferred);
        Assert.Equal(
            PastedLinkResult.NotFound,
            await h.Client.RedeemPastedLinkAsync("https://my-app.strybok.link/invite777?strybok_click=" + ClickId));
        Assert.Single(h.Api.Deferred);
    }

    [Fact]
    public async Task A_path_link_tapped_first_supersedes_the_deferred_link()
    {
        // A path link is a normal link of this app: the deferred link is dropped (one launch, one navigation).
        var source = new FakeLinkSource { InitialLink = new Uri("https://my-app.strybok.link/product/42?id=7") };
        await using var h = AndroidWithOption(
            new MemoryStore(),
            "{\"linkId\":\"l\",\"appId\":\"app_1\",\"routePath\":\"/invite\",\"url\":\"https://my-app.strybok.link/Ab3xK9\",\"deferred\":true}",
            source);
        h.Listen();
        await h.Client.InitializeAsync();
        await Async.Eventually(() => h.Client.DeferredLinkStatus == DeferredLinkStatus.Superseded, "superseded");

        Assert.False(Assert.Single(h.Received).IsDeferred);
        Assert.Empty(h.Api.Deferred); // no request is made
    }

    /// <summary>Android deferred links answered with <paramref name="redeemBody"/>, with the option set.</summary>
    private static Harness AndroidWithOption(MemoryStore store, string redeemBody, FakeLinkSource? source = null)
    {
        var harness = Make(new[] { "id", "lang" }, source, options =>
        {
            options.Platform = StrybokPlatform.Android;
            options.DeferredLinks = true;
            options.AndroidPackageName = "com.example.app";
            options.DeferredLinkStore = store;
            options.InstallReferrerSources.Add(_ => Task.FromResult(InstallReferrerAnswer.Referrer("strybok_click=" + ClickId)));
        }, new[] { Links.Host });
        harness.Api.OnDeferred = (_, _) => Task.FromResult(Responses.Json(HttpStatusCode.OK, redeemBody));
        return harness;
    }
}
