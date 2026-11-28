# Strybok (.NET SDK)

[![NuGet](https://img.shields.io/nuget/v/Strybok.svg?label=Strybok)](https://www.nuget.org/packages/Strybok) [![NuGet](https://img.shields.io/nuget/v/Strybok.Maui.svg?label=Strybok.Maui)](https://www.nuget.org/packages/Strybok.Maui) [![License](https://img.shields.io/badge/license-Apache%202.0-blue.svg)](./LICENSE)

For .NET MAUI, .NET for iOS and .NET for Android apps. Receives iOS Universal Links / Android App
Links, resolves them through the Strybok API to an in-app route and payload, and delivers each
link exactly once — on cold start, from the background and in the foreground.

```csharp
// MauiProgram.cs
builder.Services.AddStrybok(options =>
{
    options.ApiBaseUrl = new Uri("https://api.strybok.com");
    options.AllowedHosts.Add("my-app.strybok.link");
});

// App.xaml.cs
protected override void OnAppLinkRequestReceived(Uri uri)
{
    base.OnAppLinkRequestReceived(uri);
    _linkSource.Push(uri); // ManualLinkSource from DI
}

// Wherever you navigate
strybok.LinkReceived += (_, link) => MainThread.BeginInvokeOnMainThread(() => Open(link.RoutePath));
await strybok.InitializeAsync();
```

Create a link to share (needs the application's publishable client key `pk_…`, with "Links from
the app" switched on and route patterns listed in the dashboard — never a secret API key):

```csharp
// options.ClientKey = "pk_…";
CreatedLink link = await strybok.CreateLinkAsync("/product/42", title: "Red shoes");
await Share.Default.RequestAsync(new ShareTextRequest { Uri = link.Url.OriginalString });
```

Campaign values (`utm_source`, `utm_medium`, `utm_campaign`, `utm_term`, `utm_content`) of the
open come with the link, as `link.Campaign.Source` and so on (`null` when not set).
Campaign values are the link's defaults (set in the dashboard) overridden by the `utm_*`
parameters of the tapped URL. They are labels chosen by whoever shared the link: use them for
your analytics, never to decide anything. Details:
[integration guide](./docs/dotnet-maui.md#campaign-values).

## Links without an API call (optional)

Path links (`https://my-app.strybok.link/product/42?id=7`) and Firebase long links
(`https://my-app.strybok.link/?link=…`) open a route without a link made in advance, once the
owner switched them on. Copy `parameterLinks` of the integration guide
(`GET /v1/apps/:appId/integration`, shown on the dashboard's setup screen) into the options:

```csharp
builder.Services.AddStrybok(options =>
{
    options.ApiBaseUrl = new Uri("https://api.strybok.com");
    options.AllowedHosts.Add("my-app.strybok.link");
    options.ParameterLinks = new ParameterLinksOptions("id", "lang"); // parameterLinks.payloadKeys
});
```

Only the path and the values of these keys are sent, and `link.RoutePath` / `link.Payload` are
what the server read, never the tapped path. Off by default; without it nothing changes. New in
0.4.0, covered by unit tests only. Details:
[integration guide](./docs/dotnet-maui.md#links-without-an-api-call-optional).

## Deferred links (optional)

Restoring a link that was tapped before the app was installed is optional, **off by default and
has never run on a device**. The packages ship no store integration and never read the
pasteboard; your app supplies what the platform has:

- **Android:** `DeferredLinks = true` plus at least one `InstallReferrerSources` entry: a delegate
  of yours that returns the store's raw install referrer, `UnavailableForNow` or `Unavailable`.
  There is no install referrer API in MAUI: you need a .NET binding of Google's library. Huawei
  AppGallery and Samsung Galaxy Store flows are unverified even in the native SDKs.
- **iOS:** `await strybok.RedeemPastedLinkAsync(text)` with the text the person pasted. MAUI has
  no control for Apple's paste button, so `Clipboard.Default.GetTextAsync()` shows the iOS paste
  prompt; it costs the person extra taps and works only if they paste.
- **iOS probabilistic matching** (`AllowProbabilisticMatching`) is **forbidden by Apple's rules**
  (the App Review and legal risk is yours), can hand over another person's link on a shared
  network, fails behind Private Relay or a VPN, and has privacy-policy consequences. Check
  `link.IsProbabilisticMatch` before doing anything personal with such a link.

`AddStrybok` keeps the two state values in MAUI `Preferences` and takes the package name / bundle
id from `AppInfo.PackageName`. The core package has no default: pass an `IDeferredLinkStore`
(`DeferredLinkStore`) over storage that survives a restart, and `AndroidPackageName` /
`IosBundleId`; otherwise the status stays `Disabled`. Deferred links arrive through `LinkReceived`
with `link.IsDeferred`; `strybok.DeferredLinkStatus` says where the check stands. Details:
[integration guide](./docs/dotnet-maui.md#deferred-links-optional),
[Android](./docs/android.md), [iOS](./docs/ios.md).

Full guide, native setup and troubleshooting:
[`docs/dotnet-maui.md`](./docs/dotnet-maui.md).
Behaviour shared by all SDKs: [`docs/sdk-contract.md`](./docs/sdk-contract.md).

| Project               | Targets                    | Contents                                                                |
| --------------------- | -------------------------- | ----------------------------------------------------------------------- |
| `src/Strybok`         | `netstandard2.0`, `net8.0` | `StrybokClient`, options, `ILinkSource`, `ManualLinkSource`, exceptions |
| `src/Strybok.Maui`    | `net8.0`                   | `AddStrybok(...)`, platform detection, `PreferencesDeferredLinkStore`   |
| `tests/Strybok.Tests` | `net8.0`                   | xUnit tests of the contract (core only)                                 |

> **Early release (0.4.x).** The core library builds and passes its tests. The MAUI glue is
> compiled but has no tests and has never been run inside a MAUI app. Not verified on devices.
>
> A link tapped before the app is installed is not restored by default. Deferred links are
> optional, off by default and unverified on devices, and the packages ship no store integration:
> see [Deferred links](#deferred-links-optional).

## Install

```sh
dotnet add package Strybok
dotnet add package Strybok.Maui
```

## Build and test

In the SDK directory:

```sh
dotnet test Strybok.sln                                  # core library + tests
dotnet build src/Strybok.Maui/Strybok.Maui.csproj        # MAUI glue (not part of the solution)
```

## Licence

Apache 2.0, see [LICENSE](./LICENSE).
