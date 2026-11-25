using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Strybok.Tests;

/// <summary>"Campaign query" from the SDK contract (decision 0018): what is sent to resolve.</summary>
public sealed class CampaignQueryTests
{
    private const string ResolveUrl =
        "https://api.strybok.com/v1/public/resolve?url=https%3A%2F%2Fmy-app.strybok.link%2Finvite777";

    /// <summary>The lookup URL the SDK sends for <paramref name="url"/>.</summary>
    private static async Task<string> Lookup(string url)
    {
        await using var h = new Harness();
        await h.Client.ResolveAsync(new Uri(url));
        return Assert.Single(h.Api.Resolves).LookupUrl;
    }

    [Fact]
    public async Task The_example_of_the_contract()
    {
        await using var h = new Harness(
            configure: options => options.AllowedHosts = new List<string> { "myapp.strybok.link" });

        await h.Client.ResolveAsync(new Uri(
            "https://MyApp.strybok.link/invite777?utm_source=instagram&fbclid=abc&utm_medium=two%20words&utm_source=x#top"));

        Assert.Equal(
            "https://myapp.strybok.link/invite777?utm_source=instagram",
            Assert.Single(h.Api.Resolves).LookupUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?")]
    [InlineData("#utm_source=fragment")]
    [InlineData("?#utm_source=fragment")]
    [InlineData("?token=secret&fbclid=abc&gclid=1&utm_id=7#f")]
    [InlineData("?a=1#?utm_source=fragment")]
    [InlineData("?utm_source=")]
    [InlineData("?utm_source")]
    [InlineData("?&&=&=x&")]
    public async Task Without_campaign_parameters_the_request_is_what_earlier_versions_sent(string suffix)
    {
        await using var h = new Harness();
        await h.Client.ResolveAsync(new Uri(Links.InviteUrl + suffix));
        Assert.Equal(ResolveUrl, Assert.Single(h.Api.Resolves).Uri.OriginalString);
    }

    [Fact]
    public async Task Keeps_the_five_keys_in_the_fixed_order_and_nothing_else()
    {
        Assert.Equal(
            Links.InviteUrl + "?utm_source=s&utm_medium=m&utm_campaign=c&utm_term=t&utm_content=n",
            await Lookup(Links.InviteUrl
                + "?utm_content=n&x=1&utm_term=t&utm_campaign=c&token=secret&utm_medium=m&utm_source=s#utm_source=f"));
        Assert.Equal(
            Links.InviteUrl + "?utm_medium=m&utm_content=n",
            await Lookup(Links.InviteUrl + "?utm_content=n&&utm_medium=m&"));
        Assert.Equal(Links.InviteUrl + "?utm_source=a", await Lookup(Links.InviteUrl + "?utm_source=a#b"));
    }

    [Fact]
    public async Task Sends_the_campaign_query_percent_encoded_and_nothing_else_of_the_query()
    {
        await using var h = new Harness();
        var uri = new Uri(
            "https://My-App.Strybok.Link/invite777?token=secret&utm_medium=social&utm_source=instagram&email=a%40b.c#private");

        var link = await h.Client.ResolveAsync(uri);

        Assert.Same(uri, link.Uri);
        var sent = Assert.Single(h.Api.Resolves).Uri.OriginalString;
        Assert.Equal(ResolveUrl + "%3Futm_source%3Dinstagram%26utm_medium%3Dsocial", sent);
        foreach (var secret in new[] { "secret", "token", "email", "private", "b.c" })
        {
            Assert.DoesNotContain(secret, sent, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("UTM_SOURCE=a")]
    [InlineData("Utm_Source=a")]
    [InlineData("utm_sourcex=a")]
    [InlineData("xutm_source=a")]
    [InlineData("utm_id=a")]
    [InlineData("utm_source%20=a")]
    [InlineData("+utm_source=a")]
    [InlineData("utm_source[]=a")]
    public async Task Key_names_are_exact_and_lower_case(string query)
    {
        Assert.Equal(Links.InviteUrl, await Lookup(Links.InviteUrl + "?" + query));
    }

    [Theory]
    [InlineData("utm_source=first&utm_source=second", "?utm_source=first")]
    [InlineData("utm_source=not%20valid&utm_source=second", "")]
    [InlineData("utm_source=&utm_source=second", "")]
    [InlineData("utm_source&utm_source=second", "")]
    [InlineData("utm_source=a%2Fb&utm_medium=m&utm_source=second", "?utm_medium=m")]
    public async Task The_first_occurrence_of_a_key_counts_even_when_its_value_is_dropped(string query, string kept)
    {
        Assert.Equal(Links.InviteUrl + kept, await Lookup(Links.InviteUrl + "?" + query));
    }

    [Theory]
    [InlineData("two%20words")]
    [InlineData("two+words")]
    [InlineData("two words")]
    [InlineData("a@b.c")]
    [InlineData("a%40b.c")]
    [InlineData("a/b")]
    [InlineData("a%2Fb")]
    [InlineData("a=b")]
    [InlineData("a%26utm_medium=b")]
    [InlineData("a%3Db")]
    [InlineData("(other)")]
    [InlineData("a,b")]
    [InlineData("a:b")]
    [InlineData("a*b")]
    [InlineData("a!b")]
    [InlineData("a'b")]
    [InlineData("кампанія")]
    [InlineData("%D0%BA%D0%B0")]
    [InlineData("caf%C3%A9")]
    [InlineData("a%00b")]
    [InlineData("a%0Ab")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 65
    public async Task Values_outside_the_allowed_characters_or_length_are_dropped(string value)
    {
        Assert.Equal(Links.InviteUrl, await Lookup(Links.InviteUrl + "?utm_source=" + value));
    }

    [Fact]
    public async Task Values_of_the_allowed_characters_up_to_64_long_are_kept()
    {
        var longest = new string('a', 64);
        Assert.Equal(
            Links.InviteUrl + "?utm_source=" + longest,
            await Lookup(Links.InviteUrl + "?utm_source=" + longest));
        Assert.Equal(
            Links.InviteUrl + "?utm_source=AZaz09._~-",
            await Lookup(Links.InviteUrl + "?utm_source=AZaz09._~-"));
    }

    [Fact]
    public async Task Percent_encoded_keys_and_values_are_decoded()
    {
        Assert.Equal(
            Links.InviteUrl + "?utm_source=insta-gram",
            await Lookup(Links.InviteUrl + "?utm_source=%69nsta%2Dgram"));
        Assert.Equal(Links.InviteUrl + "?utm_source=a._~b", await Lookup(Links.InviteUrl + "?utm_source=a%2e%5f%7Eb"));
        Assert.Equal(Links.InviteUrl + "?utm_source=x", await Lookup(Links.InviteUrl + "?utm%5Fsource=x"));
        Assert.Equal(Links.InviteUrl + "?utm_medium=x", await Lookup(Links.InviteUrl + "?%75tm_medium=x"));

        // 64 characters after decoding.
        var encoded = string.Concat(Enumerable.Repeat("%61", 64));
        Assert.Equal(
            Links.InviteUrl + "?utm_term=" + new string('a', 64),
            await Lookup(Links.InviteUrl + "?utm_term=" + encoded));
        Assert.Equal(Links.InviteUrl, await Lookup(Links.InviteUrl + "?utm_term=" + encoded + "%61"));
    }

    [Theory]
    [InlineData("%")]
    [InlineData("a%")]
    [InlineData("a%2")]
    [InlineData("%2")]
    [InlineData("%zz")]
    [InlineData("%G0")]
    [InlineData("%0G")]
    [InlineData("a%-1")]
    [InlineData("%+1")]
    [InlineData("%%")]
    [InlineData("%u0041")]
    [InlineData("%C3%28")]
    [InlineData("%FF")]
    [InlineData("%C0%AE")]
    [InlineData("%E2%82")]
    [InlineData("%ED%A0%80")]
    public async Task Malformed_escapes_and_invalid_utf8_drop_the_value_and_never_throw(string value)
    {
        Assert.Equal(Links.InviteUrl, await Lookup(Links.InviteUrl + "?utm_source=" + value));
        Assert.Equal(
            Links.InviteUrl + "?utm_medium=ok",
            await Lookup(Links.InviteUrl + "?utm_source=" + value + "&utm_medium=ok"));
        Assert.Equal(
            Links.InviteUrl + "?utm_medium=ok",
            await Lookup(Links.InviteUrl + "?" + value + "=x&utm_medium=ok"));
    }

    [Theory]
    [InlineData("http://my-app.strybok.link/invite777")]
    [InlineData("https://evil.example.com/invite777")]
    [InlineData("https://user:pw@my-app.strybok.link/invite777")]
    [InlineData("https://user@my-app.strybok.link/invite777")]
    [InlineData("https://my-app.strybok.link:8443/invite777")]
    [InlineData("https://my-app.strybok.link/a/b")]
    [InlineData("https://my-app.strybok.link/invite777/")]
    [InlineData("https://my-app.strybok.link/")]
    [InlineData("https://my-app.strybok.link")]
    public async Task A_campaign_query_does_not_make_a_foreign_url_ours(string url)
    {
        await using var h = new Harness();
        Assert.False(h.Client.IsStrybokUri(url), url);
        Assert.False(h.Client.IsStrybokUri(url + "?utm_source=a&utm_medium=b"), url);
        await Assert.ThrowsAsync<UnsupportedLinkException>(
            () => h.Client.ResolveAsync(new Uri(url + "?utm_source=a&utm_medium=b")));
        Assert.Empty(h.Api.Resolves);
    }

    [Theory]
    // Nothing in a query can stand in for the authority or the path.
    [InlineData("https://evil.example.com/x?utm_source=https://my-app.strybok.link/invite777")]
    [InlineData("https://evil.example.com?utm_source=a@my-app.strybok.link/invite777")]
    [InlineData("https://my-app.strybok.link?utm_source=/invite777")]
    [InlineData("https://my-app.strybok.link/?utm_source=a")]
    public async Task A_query_never_supplies_the_host_or_the_slug(string url)
    {
        await using var h = new Harness();
        Assert.False(h.Client.IsStrybokUri(url), url);
    }

    [Fact]
    public async Task Host_and_slug_of_the_lookup_do_not_depend_on_the_query()
    {
        Assert.Equal(
            "https://my-app.strybok.link/Invite777?utm_source=a",
            await Lookup("HTTPS://My-App.STRYBOK.link:443/Invite777?utm_source=a&token=s#f"));
    }
}

/// <summary>The <c>campaign</c> of a resolved link and of the <c>app_open</c> event.</summary>
public sealed class CampaignValuesTests
{
    private static void AnswerWith(Harness h, string? campaign) =>
        h.Api.OnResolve = (_, _) => Task.FromResult(Responses.Json(
            HttpStatusCode.OK,
            "{\"linkId\":\"link_1\",\"appId\":\"app_1\",\"routePath\":\"/x\""
                + (campaign is null ? string.Empty : ",\"campaign\":" + campaign) + "}"));

    private static string[] Names(JsonElement element) =>
        element.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();

    [Fact]
    public async Task Campaign_holds_the_five_string_fields_the_server_sent()
    {
        await using var h = new Harness();
        AnswerWith(
            h,
            "{\"source\":\"instagram\",\"medium\":\"social\",\"campaign\":\"spring\",\"term\":\"shoes\",\"content\":\"banner\",\"extra\":\"x\"}");

        var campaign = (await h.Client.ResolveAsync(Links.Invite)).Campaign;

        Assert.Equal("instagram", campaign.Source);
        Assert.Equal("social", campaign.Medium);
        Assert.Equal("spring", campaign.Campaign);
        Assert.Equal("shoes", campaign.Term);
        Assert.Equal("banner", campaign.Content);
        Assert.False(campaign.IsEmpty);
        Assert.Equal(new LinkCampaign("instagram", "social", "spring", "shoes", "banner"), campaign);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[\"a\"]")]
    [InlineData("\"instagram\"")]
    [InlineData("7")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("{\"other\":\"x\"}")]
    public async Task Campaign_is_empty_never_null_when_missing_or_not_an_object(string? campaign)
    {
        await using var h = new Harness();
        AnswerWith(h, campaign);

        var link = await h.Client.ResolveAsync(Links.Invite);

        Assert.NotNull(link.Campaign);
        Assert.True(link.Campaign.IsEmpty);
        Assert.Same(LinkCampaign.Empty, link.Campaign);
        Assert.Equal("/x", link.RoutePath);
    }

    [Fact]
    public async Task Campaign_fields_that_are_not_strings_are_ignored_not_errors()
    {
        await using var h = new Harness();
        AnswerWith(
            h,
            "{\"source\":7,\"medium\":null,\"campaign\":{\"a\":1},\"term\":[\"x\"],\"content\":\"banner\",\"utm_source\":\"x\"}");

        var campaign = (await h.Client.ResolveAsync(Links.Invite)).Campaign;

        Assert.Equal(new LinkCampaign(content: "banner"), campaign);
        Assert.Null(campaign.Source);
    }

    [Fact]
    public void A_link_built_without_campaign_has_an_empty_one()
    {
        using var document = JsonDocument.Parse("{}");
        var payload = document.RootElement.Clone();

        Assert.Same(LinkCampaign.Empty, new ResolvedDeepLink(Links.Invite, "l", "a", "/x", payload).Campaign);
        Assert.Same(LinkCampaign.Empty, new ResolvedDeepLink(Links.Invite, "l", "a", "/x", payload, null).Campaign);

        var campaign = new LinkCampaign(source: "s");
        Assert.Same(campaign, new ResolvedDeepLink(Links.Invite, "l", "a", "/x", payload, campaign).Campaign);
        Assert.Equal(LinkCampaign.Empty, new LinkCampaign());
        Assert.Equal(LinkCampaign.Empty.GetHashCode(), new LinkCampaign().GetHashCode());
        Assert.NotEqual(campaign, new LinkCampaign(medium: "s"));
        Assert.True(new LinkCampaign().IsEmpty);
        Assert.Equal("LinkCampaign(source: s, medium: , campaign: , term: , content: )", campaign.ToString());
    }

    [Fact]
    public async Task App_open_reports_the_campaign_exactly_as_resolve_returned_it()
    {
        var source = new FakeLinkSource();
        await using var h = new Harness(source);
        // The server answers with other values than the URL has: the answer is what is reported.
        AnswerWith(h, "{\"source\":\"newsletter\",\"content\":\"footer\",\"ignored\":\"x\",\"term\":5}");
        h.Listen();
        await h.Client.InitializeAsync();

        source.Emit(new Uri(Links.InviteUrl + "?utm_source=instagram&utm_medium=social"));
        await h.ReceivedCount(1);
        await Async.Eventually(() => h.Api.Events.Count == 1, "the app_open event");

        var body = Assert.Single(h.Api.Events).Body;
        Assert.Equal(new[] { "campaign", "eventId", "linkId", "platform", "sdkVersion", "type" }, Names(body));
        Assert.Equal(
            "{\"source\":\"newsletter\",\"content\":\"footer\"}",
            body.GetProperty("campaign").GetRawText());
        Assert.Equal(
            new LinkCampaign(source: "newsletter", content: "footer"),
            Assert.Single(h.Received).Campaign);
    }

    [Fact]
    public async Task App_open_carries_all_five_fields_in_order()
    {
        var source = new FakeLinkSource();
        await using var h = new Harness(source);
        AnswerWith(h, "{\"content\":\"n\",\"term\":\"t\",\"campaign\":\"c\",\"medium\":\"m\",\"source\":\"s\"}");
        h.Listen();
        await h.Client.InitializeAsync();

        source.Emit(Links.Invite);
        await Async.Eventually(() => h.Api.Events.Count == 1, "the app_open event");

        Assert.Equal(
            "{\"source\":\"s\",\"medium\":\"m\",\"campaign\":\"c\",\"term\":\"t\",\"content\":\"n\"}",
            Assert.Single(h.Api.Events).Body.GetProperty("campaign").GetRawText());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"source\":1}")]
    public async Task App_open_has_no_campaign_when_resolve_returned_none_whatever_the_url_says(string? campaign)
    {
        var source = new FakeLinkSource();
        await using var h = new Harness(source);
        AnswerWith(h, campaign);
        h.Listen();
        await h.Client.InitializeAsync();

        source.Emit(new Uri(Links.InviteUrl + "?utm_source=instagram"));
        await Async.Eventually(() => h.Api.Events.Count == 1, "the app_open event");

        Assert.Equal(
            new[] { "eventId", "linkId", "platform", "sdkVersion", "type" },
            Names(Assert.Single(h.Api.Events).Body));
    }

    [Fact]
    public async Task The_same_slug_with_different_campaign_parameters_is_two_links()
    {
        var source = new FakeLinkSource();
        await using var h = new Harness(source);
        h.Listen();
        await h.Client.InitializeAsync();

        source.Emit(new Uri(Links.InviteUrl + "?utm_source=a"));
        source.Emit(new Uri(Links.InviteUrl + "?utm_source=a")); // The same URL string: one tap reported twice.
        source.Emit(new Uri(Links.InviteUrl + "?utm_source=b"));
        source.Emit(new Uri(Links.InviteUrl));
        // Compared as received: a parameter that is never sent still makes another URL.
        source.Emit(new Uri(Links.InviteUrl + "?fbclid=1"));
        source.Emit(new Uri(Links.InviteUrl + "?fbclid=1"));

        await h.ReceivedCount(4);
        await Async.Settle();
        Assert.Equal(
            new[]
            {
                Links.InviteUrl,
                Links.InviteUrl + "?fbclid=1",
                Links.InviteUrl + "?utm_source=a",
                Links.InviteUrl + "?utm_source=b",
            },
            h.Received.Select(link => link.Uri.OriginalString).OrderBy(url => url, StringComparer.Ordinal).ToArray());
        Assert.Equal(
            new[]
            {
                Links.InviteUrl,
                Links.InviteUrl,
                Links.InviteUrl + "?utm_source=a",
                Links.InviteUrl + "?utm_source=b",
            },
            h.Api.Resolves.Select(request => request.LookupUrl).OrderBy(url => url, StringComparer.Ordinal).ToArray());
    }
}
