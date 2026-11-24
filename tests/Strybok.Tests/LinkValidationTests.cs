using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace Strybok.Tests;

/// <summary>"Which URLs are ours" from the SDK contract.</summary>
public sealed class LinkValidationTests
{
    private static Harness WithUpperCaseAllowedHost() =>
        new Harness(configure: options => options.AllowedHosts = new List<string> { "My-App.Strybok.Link" });

    [Theory]
    [InlineData("https://my-app.strybok.link/invite777")]
    [InlineData("https://MY-APP.strybok.link/AbC_7-x")]
    [InlineData("HTTPS://my-app.strybok.link/invite777")]
    [InlineData("https://my-app.strybok.link/abc?utm=1#frag")]
    [InlineData("https://my-app.strybok.link/abc?")]
    [InlineData("https://my-app.strybok.link/abc#frag")]
    [InlineData("https://my-app.strybok.link/abc?next=/a/../b&x=%2F")]
    [InlineData("https://my-app.strybok.link:443/invite777")]
    [InlineData("https://my-app.strybok.link/a")]
    public async Task Accepts_https_links_on_an_allowed_host_with_a_single_slug(string url)
    {
        await using var h = WithUpperCaseAllowedHost();
        Assert.True(h.Client.IsStrybokUri(url), url);
        Assert.True(h.Client.IsStrybokUri(new Uri(url)), url);
    }

    [Theory]
    // Scheme.
    [InlineData("http://my-app.strybok.link/invite777")]
    [InlineData("myapp://my-app.strybok.link/invite777")]
    [InlineData("https:/my-app.strybok.link/invite777")]
    [InlineData("https:my-app.strybok.link/invite777")]
    [InlineData("https:\\\\my-app.strybok.link\\invite777")]
    [InlineData(" https://my-app.strybok.link/invite777")]
    [InlineData("//my-app.strybok.link/invite777")]
    [InlineData("/invite777")]
    [InlineData("")]
    // Host.
    [InlineData("https://evil.example.com/invite777")]
    [InlineData("https://my-app.strybok.link.evil.example.com/invite777")]
    [InlineData("https://evil-my-app.strybok.link/invite777")]
    [InlineData("https://my-app.strybok.link./invite777")]
    [InlineData("https://my%2Dapp.strybok.link/invite777")]
    [InlineData("https://my-app.stryboK.link/invite777")] // Kelvin sign lower-cases to 'k'.
    [InlineData("https:///invite777")]
    // User info.
    [InlineData("https://user:pw@my-app.strybok.link/invite777")]
    [InlineData("https://user@my-app.strybok.link/invite777")]
    [InlineData("https://@my-app.strybok.link/invite777")]
    [InlineData("https://my-app.strybok.link@evil.example.com/invite777")]
    [InlineData("https://evil.example.com\\@my-app.strybok.link/invite777")]
    [InlineData("https://my-app.strybok.link\\@evil.example.com/invite777")]
    // Port.
    [InlineData("https://my-app.strybok.link:8443/invite777")]
    [InlineData("https://my-app.strybok.link:80/invite777")]
    [InlineData("https://my-app.strybok.link:/invite777")]
    [InlineData("https://my-app.strybok.link:0443/invite777")]
    [InlineData("https://my-app.strybok.link:443:443/invite777")]
    // Path: exactly one slug segment, no trailing slash.
    [InlineData("https://my-app.strybok.link")]
    [InlineData("https://my-app.strybok.link/")]
    [InlineData("https://my-app.strybok.link?x=/invite777")]
    [InlineData("https://my-app.strybok.link#/invite777")]
    [InlineData("https://my-app.strybok.link/invite777/")]
    [InlineData("https://my-app.strybok.link//invite777")]
    [InlineData("https://my-app.strybok.link/a/b")]
    [InlineData("https://my-app.strybok.link/.well-known/assetlinks.json")]
    [InlineData("https://my-app.strybok.link/invite.777")]
    [InlineData("https://my-app.strybok.link/inv ite")]
    [InlineData("https://my-app.strybok.link/invite777\n")]
    [InlineData("https://my-app.strybok.link/invite777 ")]
    [InlineData("https://my-app.strybok.link/invité")]
    [InlineData("https://my-app.strybok.link/invite;x=1")]
    [InlineData("https://my-app.strybok.link\\invite777")]
    [InlineData("https://my-app.strybok.link/invite777\\")]
    // Encoded characters: never decoded, never accepted.
    [InlineData("https://my-app.strybok.link/a%2Fb")]
    [InlineData("https://my-app.strybok.link/%69nvite777")]
    [InlineData("https://my-app.strybok.link/invite777%2F")]
    [InlineData("https://my-app.strybok.link/invite777%3Fx")]
    [InlineData("https://my-app.strybok.link/invite%20777")]
    // Dot segments, which System.Uri would collapse into a single slug.
    [InlineData("https://my-app.strybok.link/./invite777")]
    [InlineData("https://my-app.strybok.link/a/../invite777")]
    [InlineData("https://my-app.strybok.link/a/b/../../invite777")]
    [InlineData("https://my-app.strybok.link/invite777/.")]
    [InlineData("https://my-app.strybok.link/invite777/..")]
    [InlineData("https://my-app.strybok.link/%2E%2E/invite777")]
    [InlineData("https://my-app.strybok.link/..")]
    public async Task Rejects_everything_else(string url)
    {
        await using var h = WithUpperCaseAllowedHost();
        Assert.False(h.Client.IsStrybokUri(url), url);

        // The same holds when the URL went through System.Uri first, whatever it normalised.
        if (Uri.TryCreate(url, UriKind.RelativeOrAbsolute, out var uri))
        {
            Assert.False(h.Client.IsStrybokUri(uri), url);
        }
    }

    [Fact]
    public async Task Slug_length_is_limited_to_64_characters()
    {
        await using var h = new Harness();
        Assert.True(h.Client.IsStrybokUri("https://my-app.strybok.link/" + new string('a', 64)));
        Assert.False(h.Client.IsStrybokUri("https://my-app.strybok.link/" + new string('a', 65)));
    }

    [Fact]
    public async Task Null_and_relative_uris_are_not_ours()
    {
        await using var h = new Harness();
        Assert.False(h.Client.IsStrybokUri((Uri?)null));
        Assert.False(h.Client.IsStrybokUri((string?)null));
        Assert.False(h.Client.IsStrybokUri(new Uri("/invite777", UriKind.Relative)));
    }

    [Fact]
    public async Task Does_not_trust_the_path_System_Uri_normalised()
    {
        await using var h = new Harness();
        h.Listen();
        await h.Client.InitializeAsync();

        var uri = new Uri("https://my-app.strybok.link/a/../invite777");
        // The hazard: after normalisation this looks exactly like a valid link.
        Assert.Equal("/invite777", uri.AbsolutePath);

        Assert.False(h.Client.IsStrybokUri(uri));
        await Assert.ThrowsAsync<UnsupportedLinkException>(() => h.Client.ResolveAsync(uri));

        h.Source.Emit(uri);
        await Async.Settle();
        Assert.Empty(h.Api.Resolves);
        Assert.Empty(h.Received);
        Assert.Empty(h.Errors);
    }

    [Fact]
    public async Task Resolve_refuses_foreign_links_without_any_network_call()
    {
        await using var h = new Harness();
        var foreign = new Uri("https://evil.example.com/invite777");

        var error = await Assert.ThrowsAsync<UnsupportedLinkException>(() => h.Client.ResolveAsync(foreign));

        Assert.Same(foreign, error.Uri);
        Assert.Empty(h.Api.Resolves);
    }

    [Fact]
    public async Task Foreign_links_from_the_os_are_dropped_silently()
    {
        var source = new FakeLinkSource();
        await using var h = new Harness(source);
        h.Listen();
        await h.Client.InitializeAsync();

        source.Emit(new Uri("https://evil.example.com/invite777"));
        source.Emit(new Uri("https://user:pw@my-app.strybok.link/invite777"));
        source.Emit(new Uri("https://my-app.strybok.link:8443/invite777"));
        source.Emit(new Uri("https://my-app.strybok.link/invite777/"));
        source.Emit(new Uri("myapp://my-app.strybok.link/invite777"));
        await Async.Settle();

        Assert.Empty(h.Api.Resolves);
        Assert.Empty(h.Received);
        Assert.Empty(h.Errors);
    }
}
