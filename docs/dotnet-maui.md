# .NET MAUI integration

The .NET SDK is for .NET MAUI apps, and for .NET for iOS / .NET for Android apps without MAUI.
Your app hands it the verified HTTPS links the OS delivers, it asks the Strybok API which in-app
route a link stands for, and it gives your app each link exactly once.

> **Status — read this first.** Early release (0.3.x), published on NuGet.
>
> | Part                                                | Compiled | Tests run | On a device |
> | --------------------------------------------------- | -------- | --------- | ----------- |
> | Core library `Strybok` (`netstandard2.0`, `net8.0`) | yes (CI) | yes (CI)  | **no**      |
> | MAUI glue `Strybok.Maui`                            | yes (CI) | no tests  | **no**      |
> | The code samples on this page                       | **no**   | —         | **no**      |
>
> The MAUI glue has only been compiled, never run inside a MAUI app, and the samples on this page
> were not compiled. Walk through the [manual test plan](./manual-test-plan.md) on real devices
> before you rely on it.
>
> A link tapped before the app is installed is **not restored by default**. Deferred links are an
> option that is off by default and has never run on a device; the packages ship no store
> integration, so your app supplies the install referrer (Android) or the pasted link (iOS). See
> [Deferred links](#deferred-links-optional).

The behaviour every Strybok SDK implements is described in the [SDK contract](./sdk-contract.md).

## 1. Prerequisites

1. Create an application in Strybok with your iOS and Android identifiers.
2. Read its setup data: `GET /v1/apps/:appId/integration` returns the managed hostname
   (for example `my-app.strybok.link`) and ready-to-copy snippets.
3. Tell Strybok about the app as described in step 1 of the [Android](./android.md) and
   [iOS](./ios.md) guides (package name and certificate fingerprints, Team ID and bundle ID).
   Their "verify on a device" and troubleshooting sections apply to .NET apps unchanged.

## 2. Add the packages

```sh
dotnet add package Strybok
dotnet add package Strybok.Maui
```

The source is at [github.com/strybok/strybok-dotnet](https://github.com/strybok/strybok-dotnet).

- `Strybok` is the whole SDK. It has no third-party dependencies on `net8.0` and later; on
  `netstandard2.0` it references `System.Text.Json`.
- `Strybok.Maui` is optional glue: `builder.Services.AddStrybok(...)` and platform detection
  through `DeviceInfo.Platform`. It targets plain `net8.0` and references
  `Microsoft.Maui.Essentials` 8.0.3; your app's own MAUI version takes precedence.

A .NET for iOS / .NET for Android app without MAUI references only `Strybok` (section 8).

## 3. Android: intent filter

`Platforms/Android/MainActivity.cs` — add the `IntentFilter` attribute. It generates the
`<intent-filter android:autoVerify="true">` from the [Android guide](./android.md):

```csharp
using Android.App;
using Android.Content;
using Android.Content.PM;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode
        | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[IntentFilter(
    new[] { Intent.ActionView },
    Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataScheme = "https",
    DataHost = "my-app.strybok.link",
    AutoVerify = true)]
public class MainActivity : MauiAppCompatActivity
{
}
```

Declare the whole host (no `DataPath`); the slug is resolved at runtime. `LaunchMode.SingleTop`
makes a link tapped while the app is running arrive in the existing activity (`OnNewIntent`)
instead of starting a second one.

## 4. iOS: Associated Domains

`Platforms/iOS/Entitlements.plist` (create the file if the project has none):

```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>com.apple.developer.associated-domains</key>
    <array>
        <string>applinks:my-app.strybok.link</string>
    </array>
</dict>
</plist>
```

No `https://`, no path, exact hostname. The Associated Domains capability must also be enabled
for the App ID in the Apple Developer portal, and the provisioning profile regenerated afterwards.
If the build does not pick the file up, point to it explicitly in the app's `.csproj`:

```xml
<PropertyGroup Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'ios'">
  <CodesignEntitlements>Platforms\iOS\Entitlements.plist</CodesignEntitlements>
</PropertyGroup>
```

For development builds, `applinks:my-app.strybok.link?mode=developer` bypasses Apple's CDN (see
the [iOS guide](./ios.md)); do not ship that value.

## 5. Hand the links to the SDK

MAUI reports app links through `Application.OnAppLinkRequestReceived(Uri)`. Following the MAUI
documentation ([Android app links](https://learn.microsoft.com/dotnet/maui/android/app-links),
[Apple universal links](https://learn.microsoft.com/dotnet/maui/macios/universal-links)), forward
the platform events to that callback in `MauiProgram.cs`, and register the SDK:

```csharp
using Microsoft.Maui.LifecycleEvents;
using Strybok.Maui;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureLifecycleEvents(lifecycle =>
            {
#if ANDROID
                lifecycle.AddAndroid(android =>
                {
                    android.OnCreate((activity, bundle) => HandleIntent(activity.Intent));
                    android.OnNewIntent((activity, intent) => HandleIntent(intent));
                });
#elif IOS
                lifecycle.AddiOS(ios =>
                {
                    ios.FinishedLaunching((app, options) => HandleActivity(app.UserActivity));
                    ios.ContinueUserActivity((app, activity, handler) => HandleActivity(activity));
                    // Only when the app uses scenes:
                    ios.SceneWillConnect((scene, session, options) => HandleActivity(
                        options.UserActivities.ToArray()
                            .FirstOrDefault(a => a.ActivityType == Foundation.NSUserActivityType.BrowsingWeb)));
                    ios.SceneContinueUserActivity((scene, activity) => HandleActivity(activity));
                });
#endif
            });

        builder.Services.AddStrybok(options =>
        {
            options.ApiBaseUrl = new Uri("https://api.strybok.com");
            options.AllowedHosts.Add("my-app.strybok.link");
            options.OnError = error => System.Diagnostics.Debug.WriteLine($"Link failed: {error}");
        });

        return builder.Build();
    }

#if ANDROID
    static void HandleIntent(Android.Content.Intent? intent)
    {
        if (intent?.Action == Android.Content.Intent.ActionView && intent.DataString is string url)
            HandleAppLink(url);
    }
#elif IOS
    static bool HandleActivity(Foundation.NSUserActivity? activity)
    {
        if (activity?.ActivityType != Foundation.NSUserActivityType.BrowsingWeb || activity.WebPageUrl is null)
            return false;
        HandleAppLink(activity.WebPageUrl.AbsoluteString!);
        return true;
    }
#endif

    static void HandleAppLink(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            MainThread.BeginInvokeOnMainThread(() => App.Current?.SendOnAppLinkRequestReceived(uri));
    }
}
```

Then push every URI into the SDK's link source in `App.xaml.cs`:

```csharp
using Strybok;

public partial class App : Application
{
    private readonly ManualLinkSource _linkSource;
    private readonly StrybokClient _strybok;

    public App(ManualLinkSource linkSource, StrybokClient strybok)
    {
        InitializeComponent();
        _linkSource = linkSource;
        _strybok = strybok;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new AppShell());      // .NET 8: MainPage = new AppShell(); in the constructor
        _strybok.LinkReceived += OnLinkReceived;      // section 6
        _ = _strybok.InitializeAsync();
        return window;
    }

    protected override void OnAppLinkRequestReceived(Uri uri)
    {
        base.OnAppLinkRequestReceived(uri);
        _linkSource.Push(uri);
    }
}
```

Notes:

- Pass the URL **as the OS delivered it**. The SDK validates `Uri.OriginalString`, because
  `System.Uri` normalises what it parses (`/a/../slug` becomes `/slug`). Do not rebuild or
  "clean" the URI before `Push`.
- It does not matter whether `Push` happens before or after `InitializeAsync()`: URLs pushed
  early are buffered, and the first of them is treated as the launch link.
- If a platform reports the same tap twice (for example through `FinishedLaunching` _and_
  `SceneWillConnect`), the duplicate is dropped (`DedupeWindow`, 2 s by default). The same link
  tapped again later is a new delivery.
- URLs that are not links of this app are ignored silently, so the same callback can keep
  serving your other app links.

## 6. Navigate (Shell example)

`LinkReceived` covers every case with one subscription:

| Situation                                 | What happens                                                                                           |
| ----------------------------------------- | ------------------------------------------------------------------------------------------------------ |
| **Cold start** (app launched by the link) | The link is resolved and kept until the first handler is attached, then replayed to that handler once. |
| **Background** (app resumed by the link)  | Raised on `LinkReceived`.                                                                              |
| **Foreground**                            | Raised on `LinkReceived`.                                                                              |

```csharp
// AppShell.xaml.cs
Routing.RegisterRoute("invite", typeof(InvitePage));

// App.xaml.cs
private void OnLinkReceived(object? sender, ResolvedDeepLink link)
{
    // Links can arrive on a background thread: always switch to the UI thread.
    MainThread.BeginInvokeOnMainThread(async () =>
    {
        try
        {
            // routePath is untrusted input: only accept routes the app knows.
            const string prefix = "/invite/";
            if (link.RoutePath.StartsWith(prefix, StringComparison.Ordinal)
                && link.RoutePath.Length > prefix.Length
                && Shell.Current is not null)
            {
                var code = link.RoutePath.Substring(prefix.Length);
                await Shell.Current.GoToAsync($"invite?code={Uri.EscapeDataString(code)}");
            }
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine($"Navigation failed: {error}");
        }
    });
}
```

```csharp
[QueryProperty(nameof(Code), "code")]
public partial class InvitePage : ContentPage
{
    public string? Code { get; set; }
}
```

`link.Payload` is a `System.Text.Json.JsonElement` (always a JSON object);
`link.GetPayloadString("invitationId")` reads one string property.

On a cold start `Shell.Current` can still be `null` when the link arrives. If you see that,
attach the handler later (for example from `AppShell.OnAppearing`): resolved links wait in the
queue until the first handler is attached, so nothing is lost by subscribing late.

Prefer to read the launch link yourself? `await strybok.GetInitialLinkAsync()` returns it at most
once and removes it from the queue, so combining both never navigates twice.

### API

| Member                         | Purpose                                                                                                                                         |
| ------------------------------ | ----------------------------------------------------------------------------------------------------------------------------------------------- |
| `InitializeAsync()`            | Start listening; completes after the launch link was handled. Idempotent.                                                                       |
| `LinkReceived`                 | Event with a `ResolvedDeepLink` (`Uri`, `LinkId`, `AppId`, `RoutePath`, `Payload`, `Campaign`). Queued links are replayed to the first handler. |
| `GetInitialLinkAsync()`        | The launch link, if not delivered through `LinkReceived` yet.                                                                                   |
| `ResolveAsync(uri, token)`     | Resolve a URI explicitly (QR scanner, tests). Does not raise `LinkReceived` or report an app open.                                              |
| `IsStrybokUri(uri)`            | Whether a URI (or URL string) is one of this app's links.                                                                                       |
| `CreateLinkAsync(routePath)`   | Create a short link to share, see [below](#create-a-link-to-share).                                                                             |
| `DeferredLinkStatus`           | Where the deferred-link check stands (event `DeferredLinkStatusChanged`), see [Deferred links](#deferred-links-optional).                       |
| `EnableDeferredLinks()`        | Switch deferred links on after the client was created, for example after a consent screen.                                                      |
| `RedeemPastedLinkAsync(text)`  | iOS: redeem a link the person pasted; `SkipPastedLink()` when they moved on.                                                                    |
| `Dispose()` / `DisposeAsync()` | Stop listening and release resources. Nothing is delivered afterwards.                                                                          |

Options (`StrybokOptions`): `ApiBaseUrl`, `AllowedHosts` (both required), `Timeout` (5 s per
attempt), `MaxRetries` (2; only timeouts, network errors and 5xx are retried), `RetryDelay`
(400 ms, doubling), `DedupeWindow` (2 s), `ReportAppOpens` (on), `Platform`, `OnError`,
`ClientKey` (only for creating links), plus
`HttpMessageHandler`, `Clock` and `Delay` for custom transports and tests. An `HttpClient` can be
passed to the `StrybokClient` constructor instead. For
[deferred links](#deferred-links-optional): `DeferredLinks` and `AllowProbabilisticMatching` (both
off), `InstallReferrerSources`, `AndroidPackageName`, `IosBundleId`, `DeviceFamily`, `Locale`,
`PasteboardMayContainLink` and `DeferredLinkStore`.

An exception thrown by a `LinkReceived` or `OnError` handler is caught by the SDK (and written to
`Debug`), so one failing handler cannot stop later links.

### Create a link to share

`CreateLinkAsync` asks the service for a short link to a screen of your app — the "Share" button.
It is new in 0.2.0; like the other samples on this page, the code below was not compiled.

1. In the dashboard, open the application, switch on **Links from the app** and list the route
   patterns the app may create links for (for example `/product/*`). Routes outside the list are
   refused.
2. Set `ClientKey` to the application's **publishable client key** (`pk_…`). The key is safe to
   ship inside the app: it can only create links for those patterns. **Never put a secret API key
   (`lb_…`) in an app**; it is not accepted here.

```csharp
// MauiProgram.cs
builder.Services.AddStrybok(options =>
{
    options.ApiBaseUrl = new Uri("https://api.strybok.com");
    options.AllowedHosts.Add("my-app.strybok.link");
    options.ClientKey = "pk_…";
});

// The "Share" button
try
{
    CreatedLink link = await strybok.CreateLinkAsync(
        "/product/42",
        payload: JsonSerializer.SerializeToElement(new { productId = 42 }),
        title: "Red shoes");
    await Share.Default.RequestAsync(new ShareTextRequest { Uri = link.Url.OriginalString });
}
catch (StrybokRateLimitedException error)
{
    ShowTryAgainLater(error.RetryAfter); // may be null
}
catch (StrybokException error)
{
    Debug.WriteLine("Could not create a link: " + error.Message);
}
```

Only the route is required; `payload` (a `JsonElement` that is a JSON object), `title`,
`description` and `imageUrl` are optional and left out of the request when not given. Under
trimming or Native AOT create the payload with a source-generated `JsonSerializerContext` or
`JsonDocument.Parse`. Asking again for the same content returns the same link.

It only runs when you call it and is independent of receiving links: it works before
`InitializeAsync`, never raises `LinkReceived` and never calls `OnError`. It throws:

| Type                                                                             | Meaning                                                                                                              | Retry later?            |
| -------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------- | ----------------------- |
| `StrybokUnauthorizedException`                                                   | No `ClientKey` set (no network call), or the key was refused: wrong or replaced key, or the feature is switched off. | no                      |
| `RouteNotAllowedException`                                                       | `RoutePath` is not covered by the route patterns in the dashboard.                                                   | no                      |
| `StrybokValidationException`                                                     | Bad route or payload (no network call), or rejected by the service; `Issues` lists what it found.                    | no                      |
| `StrybokRateLimitedException`                                                    | Too many requests, or the app's daily limit of new links is reached.                                                 | yes, after `RetryAfter` |
| `StrybokNetworkException` / `StrybokTimeoutException` / `StrybokServerException` | No usable answer after the retries (`Timeout`, `MaxRetries`, `RetryDelay` as for resolving).                         | yes                     |

Also `OperationCanceledException` for your token and `ObjectDisposedException` after `Dispose`.
Errors never contain the client key or the payload. Do not put personal data in a route or
payload: whoever receives the link can read them.

### Deferred links (optional)

A deferred link is one a person tapped **before** the app was installed. The feature is off by
default, works very differently on the two platforms, and **nothing of it has run on a device**
(in `Strybok.Maui` it has not even been run in an app, only compiled). What the server does, what
the person sees and what it costs is in the
[Android](./android.md#links-tapped-before-the-app-is-installed-deferred-links) and
[iOS](./ios.md#links-tapped-before-the-app-is-installed) guides; the exact behaviour is in the
[SDK contract](./sdk-contract.md#deferred-links-optional-android).

These packages ship **no store integration and no native code**: they never ask a store for the
install referrer and never read the pasteboard. Your app supplies both; the SDK does the rest
(the state, the request, the once-only delivery).

| What                                        | Who supplies it                                                                                               |
| ------------------------------------------- | ------------------------------------------------------------------------------------------------------------- |
| Switch                                      | You: `DeferredLinks = true`, or `strybok.EnableDeferredLinks()` later (after a consent screen).               |
| The owner's setting                         | The application's owner, in the dashboard or the API (see the two native guides).                             |
| State (attempt counter, "done" flag)        | `AddStrybok`: MAUI `Preferences` (`PreferencesDeferredLinkStore`). Core only: **you**, `DeferredLinkStore`.   |
| Package name / bundle id                    | `AddStrybok`: `AppInfo.PackageName`. Core only: **you**, `AndroidPackageName` / `IosBundleId`.                |
| Android: the store's install referrer       | **You**: one or more `InstallReferrerSources` (below).                                                        |
| iOS: the pasted text                        | **You**: a paste action of your app, handed to `strybok.RedeemPastedLinkAsync(text)`.                         |
| iOS matching: device family and language    | `AddStrybok`: `DeviceInfo` and `CultureInfo.CurrentUICulture`. Core only: **you**, `DeviceFamily` / `Locale`. |
| iOS matching: "is there anything to paste?" | Optional, you: `PasteboardMayContainLink` (skipped when not set).                                             |

Everything `AddStrybok` pre-fills can be replaced in its callback. With the core package alone
there are no defaults: without a `DeferredLinkStore` (an `IDeferredLinkStore`: two methods,
`Read(key)` and `Write(key, value)`, over any storage that survives a restart) and the identifier
of the platform the feature stays `Disabled`. Never pass a store that only lives in memory; the
link would be redeemed again on the next launch.

The link arrives through `LinkReceived` like any other, with `link.IsDeferred == true`, possibly a
second or two after your first page is shown. `GetInitialLinkAsync` never returns it, no
`app_open` is reported for it, and if a normal link opened the app in the same launch the deferred
one is dropped. Nothing about the check reaches `OnError` or throws: most people who install an
app never tapped a link. For QA there is `strybok.DeferredLinkStatus` (event
`DeferredLinkStatusChanged`, raised on any thread):

| Status           | Meaning                                                                                                |
| ---------------- | ------------------------------------------------------------------------------------------------------ |
| `Disabled`       | Off, or it cannot run: no store, no identifier, or the platform is neither Android nor iOS.            |
| `Pending`        | Not started yet, or this launch could not finish (store or network unreachable); a later launch tries. |
| `Checking`       | Running now.                                                                                           |
| `NotFirstLaunch` | Finished in an earlier launch.                                                                         |
| `Unavailable`    | Android: no source, or none can ever answer. iOS: nothing automatic exists (see below).                |
| `NoReferrer`     | The store's referrer has no link of ours: an ordinary install.                                         |
| `NotFound`       | The server refused: unknown, older than 24 hours, already used, or another application's.              |
| `Superseded`     | A normal link arrived first.                                                                           |
| `Delivered`      | Handed to `LinkReceived`.                                                                              |
| `Failed`         | Three launches could not finish, or the answer was unusable.                                           |
| `AwaitingPaste`  | iOS matching is waiting for `RedeemPastedLinkAsync` or `SkipPastedLink`.                               |

The check runs at most three times in the life of an installation and delivers at most once; the
SDK stores only the counter and the flag, never the referrer, the pasted text or the id.

#### Android: supply the install referrer

A source is a delegate of your app (`InstallReferrerSource`) that answers with the raw referrer
string of one store, or "unavailable for now" (try again on a later launch), or "unavailable"
(never on this device). It is called on a thread-pool thread, must not throw and has 10 seconds.
Sources are asked in order; the first referrer that holds a Strybok id wins.

```csharp
builder.Services.AddStrybok(options =>
{
    options.ApiBaseUrl = new Uri("https://api.strybok.com");
    options.AllowedHosts.Add("my-app.strybok.link");
    options.DeferredLinks = true;              // or strybok.EnableDeferredLinks() after consent
#if ANDROID
    options.InstallReferrerSources.Add(PlayInstallReferrer.AskAsync);
#endif
});
```

`PlayInstallReferrer.AskAsync` is **yours to write** — there is no install referrer API in MAUI or
in .NET for Android. Google's library (`com.android.installreferrer:installreferrer:2.2`) is a
Java library: you need a .NET binding of it (a binding package you trust, or your own Android
binding project around the AAR). Its shape, with the mapping every Strybok SDK uses:

```csharp
#if ANDROID
// App code, not part of the SDK, never compiled or run by us. `client` stands for your binding of
// com.android.installreferrer.api.InstallReferrerClient.
public static class PlayInstallReferrer
{
    public static async Task<InstallReferrerAnswer> AskAsync(CancellationToken cancellationToken)
    {
        // 1. client = InstallReferrerClient.newBuilder(context).build(); client.startConnection(listener)
        // 2. In onInstallReferrerSetupFinished(responseCode):
        //      OK (0)                                         -> InstallReferrerAnswer.Referrer(
        //                                                          client.getInstallReferrer().getInstallReferrer())
        //      SERVICE_UNAVAILABLE (1), SERVICE_DISCONNECTED (-1),
        //      a RemoteException, onInstallReferrerServiceDisconnected -> InstallReferrerAnswer.UnavailableForNow
        //      FEATURE_NOT_SUPPORTED (2), DEVELOPER_ERROR (3) -> InstallReferrerAnswer.Unavailable
        // 3. Always client.endConnection(), also when cancellationToken is cancelled.
        // Read only the referrer string, not the timestamps, and never log it.
        throw new NotImplementedException();
    }
}
#endif
```

Huawei AppGallery and Samsung Galaxy Store hand the referrer over in their own ways (a content
provider and Samsung's library). **Those two flows are unverified even in the native Strybok
SDKs** — nobody has tried how the stores treat the value — so if you write sources for them,
treat them as experiments.

Reading the install referrer is access to information stored on the device. Whether you need the
person's consent first is your decision; the SDK supports it by doing nothing until
`DeferredLinks` is on — leave it off and call `strybok.EnableDeferredLinks()` after your consent
screen.

#### iOS: the pasted link

The App Store passes nothing to a newly installed app. With the owner's "copy link" option the
person copies the link on a page before the App Store; your app offers a **Paste** action and
hands the text to the SDK:

```csharp
// From the tap on your own "Paste link" button — never automatically.
string? text = await Clipboard.Default.GetTextAsync();
PastedLinkResult result = await strybok.RedeemPastedLinkAsync(text);
```

`Delivered` means the link is on its way through `LinkReceived`. `NotOurs` means the text was not
a copied link of this app: nothing was sent and the text is dropped. `NotFound`: the server
refused it (older than 24 hours, already used). `AlreadyHandled`: a deferred link was handled
before on this installation. `Failed`: no answer (the person can tap again) or an unusable one.
`Disabled`: off, or not iOS.

Be honest with yourself about this step. MAUI has **no control for Apple's paste button**
(`UIPasteControl`, iOS 16 or later): `Clipboard.Default.GetTextAsync()` is a programmatic read of
the pasteboard, so iOS shows its "Allow Paste" prompt each time. To avoid the prompt, show
Apple's `UIPasteControl` from iOS platform code (a custom handler) and pass the string it delivers
to `RedeemPastedLinkAsync`. Either way: place the action on the first page or in onboarding with
a sentence that says what it does, describe it in the App Review notes, and never read the
clipboard at launch. The costs of this option: one extra page and tap for every iOS visitor
without the app, it overwrites the person's clipboard, and it works only if they copy there
**and** paste here.

#### iOS: probabilistic matching (not recommended)

With `AllowProbabilisticMatching = true` (and the owner's setting, and an opt-in on each link) the
SDK asks the server on the first launch whether a link was tapped from the same network address,
device family and language. It sends the bundle id, `iphone`/`ipad`/`ipod`, a language tag and the
SDK version — nothing else; the server uses the connection's IP address.

- **Apple forbids it.** This is fingerprinting in the sense of the App Store Review Guidelines and
  the Apple Developer Program License Agreement, with or without consent. The App Review and
  legal risk is the app owner's.
- **A match is a guess.** On a shared network (Wi-Fi, office, carrier NAT) it can be **another
  person's link**. Check `link.IsProbabilisticMatch`: such a link must never by itself sign anyone
  in, accept an invitation, apply a personal discount or show personal content.
- **It often cannot work**: iCloud Private Relay and VPNs give the browser and the app different
  addresses, and so does changing network between the tap and the first launch.
- **Privacy consequences**: the service processes IP addresses for your application; your privacy
  policy and App Store privacy labels must cover it, and consent may be needed.
- **Per-link opt-in**: only links explicitly allowed for matching can be matched.
- **Accuracy is unknown.** Nobody can measure how often a match was wrong.

If you also offer the paste action, set `PasteboardMayContainLink` (from iOS platform code,
`UIPasteboard`'s detect-patterns call for a probable web URL, which reads no content): when it
answers `true` the status becomes `AwaitingPaste` and matching starts only after
`RedeemPastedLinkAsync` returned `NotOurs` or you called `strybok.SkipPastedLink()`. Without that
callback matching does not wait. The default `Locale` is the UI culture, which can differ from
the device's first preferred language; set it to `NSLocale.PreferredLanguages[0]` from iOS
platform code for the exact value.

## 7. Errors

`ResolveAsync` throws, and `OnError` receives, a `StrybokException` (its `Uri` property is the
link being handled):

| Type                                                  | Meaning                                                                                                                                                                                  |
| ----------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `UnsupportedLinkException`                            | Not `https`, host not in `AllowedHosts`, credentials, a port other than 443, or not a single-slug path. Thrown before any network call. Ignored silently for links arriving from the OS. |
| `LinkNotFoundException`                               | Unknown, disabled or expired link (404).                                                                                                                                                 |
| `StrybokTimeoutException` / `StrybokNetworkException` | No answer after all retries (e.g. offline). The app keeps working; the link is simply not opened. The transport error is the `InnerException`.                                           |
| `StrybokServerException`                              | Unexpected status or malformed response; `StatusCode` carries the HTTP status (200 for a malformed body).                                                                                |

`ResolveAsync` additionally throws `OperationCanceledException` when your token is cancelled and
`ObjectDisposedException` after `Dispose`. `OnError` may be called on a background thread.

## 8. Without MAUI (.NET for iOS / .NET for Android)

Reference only `Strybok`, create the client yourself and push URLs from the native callbacks:

```csharp
var linkSource = new ManualLinkSource();
var strybok = new StrybokClient(
    new StrybokOptions
    {
        ApiBaseUrl = new Uri("https://api.strybok.com"),
        AllowedHosts = { "my-app.strybok.link" },
        Platform = StrybokPlatform.Android, // or StrybokPlatform.iOS
    },
    linkSource);
strybok.LinkReceived += (_, link) => { /* switch to the UI thread, then navigate */ };
await strybok.InitializeAsync();

// Android: Activity.OnCreate and OnNewIntent
if (Intent?.Action == Intent.ActionView && Intent.DataString is string url)
    linkSource.Push(new Uri(url));

// iOS: AppDelegate.ContinueUserActivity (and the scene equivalents)
if (userActivity.ActivityType == NSUserActivityType.BrowsingWeb && userActivity.WebPageUrl is not null)
    linkSource.Push(new Uri(userActivity.WebPageUrl.AbsoluteString!));
```

## 9. Testing

Run the SDK's own tests in the SDK directory (needs the .NET 8 SDK or later):

```sh
dotnet test Strybok.sln
dotnet build src/Strybok.Maui/Strybok.Maui.csproj   # the MAUI glue is not part of the solution
```

In your own tests, drive the SDK with a `ManualLinkSource` and a fake `HttpMessageHandler`; no
device or network is needed:

```csharp
sealed class FakeApi : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"linkId":"link_1","appId":"app_1","routePath":"/invite/abc123","payload":{}}"""),
        });
}

var source = new ManualLinkSource();
await using var strybok = new StrybokClient(
    new StrybokOptions
    {
        ApiBaseUrl = new Uri("https://api.strybok.com"),
        AllowedHosts = { "my-app.strybok.link" },
        HttpMessageHandler = new FakeApi(),
        ReportAppOpens = false,
    },
    source);

source.Push(new Uri("https://my-app.strybok.link/invite777"));   // cold-start link
var link = await strybok.GetInitialLinkAsync();                    // link.RoutePath == "/invite/abc123"
```

On devices, simulate a tap with the commands from the [Android](./android.md) and [iOS](./ios.md)
guides (`adb shell am start -a android.intent.action.VIEW ...`, a link in Notes on a physical
iPhone) and follow the [manual test plan](./manual-test-plan.md).

## 10. Analytics and privacy

After a link is delivered (or queued) the SDK sends one best-effort `app_open` event
(`POST /v1/public/events`) containing a random event id, the link id, the platform
(`ios`/`android`/`unknown`) and the SDK version — no device identifiers. It never delays
navigation and all failures are ignored. Disable it with `ReportAppOpens = false`.

Only the link's host and slug are sent to `resolve`, plus the five `utm_*` campaign parameters of
the tapped URL when they are valid (see below); every other query parameter and the fragment
stay on the device.

With [deferred links](#deferred-links-optional) switched on the SDK additionally sends, at most a
few times per installation: on Android the single-use id found in the install referrer your code
handed over, the package name and the SDK version; on iOS the id found in a pasted link, the
bundle id and the SDK version — or, only with `AllowProbabilisticMatching`, the bundle id, the
device family, a language tag and the SDK version, to a server that then uses the connection's IP
address. That last case is the one exception to "no fingerprint".

### Campaign values

A resolved link has `Campaign` (`LinkCampaign`): the `utm_source`, `utm_medium`, `utm_campaign`,
`utm_term` and `utm_content` of this open as `Source`, `Medium`, `Campaign`, `Term` and
`Content`. It is never `null`; a property without a value is `null`, and `Campaign.IsEmpty` is
`true` when there is none at all. Like the other samples on this page, the code below was not
compiled.

```csharp
strybok.LinkReceived += (_, link) =>
{
    if (link.Campaign.Source is { } source) analytics.SetLabel("link_source", source); // a label, nothing more
    Navigate(link.RoutePath);
};
```

- **Where the values come from.** The link's defaults — set in the dashboard (link form →
  "Campaign (optional)") or through the management API — with the `utm_*` parameters of the
  tapped URL on top, key by key. `https://my-app.strybok.link/invite777?utm_source=instagram`
  opens with source `instagram` whatever the default is; a link tapped without parameters gives
  the defaults. A URL can replace a default but cannot clear it.
- **Untrusted labels.** Whoever shares a link can write any `utm_*` value into its URL. Use the
  values as labels for your own analytics; never decide access, prices, rewards or navigation by
  them.
- **What is accepted.** Only these five lower-case names, and only values of 1–64 characters
  out of `A–Z a–z 0–9 . _ ~ -`. Anything else (`spring sale`, an e-mail address, non-Latin
  letters) is dropped silently, as if the parameter were absent — by the SDK and again by the
  server.
- **What is sent.** With the resolve request the SDK sends those five parameters when they are
  valid, and nothing else of the query and never the fragment. With the `app_open` event it
  reports the values exactly as resolve returned them. The dashboard's link analytics then
  shows a "Campaigns" breakdown by source, medium and campaign, with app opens and browser opens
  counted separately (term and content are not broken down).
- **Versions.** New in 0.3.0. Older versions send no query: their links carry no `campaign` and
  their app opens are counted under the link's defaults.

## Troubleshooting

| Symptom                                                     | Check                                                                                                                                                                            |
| ----------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| The SDK projects do not compile                             | See the status note at the top: nothing was compiled when this was written. Please report or fix the errors; the tests in `tests/Strybok.Tests` describe the intended behaviour. |
| Link opens the browser/store instead of the app             | Native association is not verified: see the [Android](./android.md) and [iOS](./ios.md) guides. The SDK is not involved yet at this point.                                       |
| App opens but `OnAppLinkRequestReceived` is never called    | The lifecycle events of section 5 are missing, or `MainActivity` has no `IntentFilter` / is not `Exported`.                                                                      |
| App opens, the callback runs, but nothing happens           | Host missing from `AllowedHosts`; `InitializeAsync()` never called; no `LinkReceived` handler attached; look at `OnError`; API reachable from the device?                        |
| Works when running, not on cold start                       | The handler runs before `Shell.Current` exists (section 6), or the launch URL never reaches `Push` (`OnCreate` / `FinishedLaunching` / `SceneWillConnect`).                      |
| Screen opens twice                                          | You navigate from both `GetInitialLinkAsync()` and your own handling of the raw URI, or two `StrybokClient` instances exist.                                                     |
| A second activity/window appears on Android                 | `LaunchMode = LaunchMode.SingleTop` is missing on `MainActivity`.                                                                                                                |
| Crash or no navigation with "wrong thread" errors           | `LinkReceived` can be raised on a background thread; wrap navigation in `MainThread.BeginInvokeOnMainThread`.                                                                    |
| Works on tap in Notes/Messages, not from Instagram/Telegram | In-app browsers often do not hand links to apps. Users get the fallback page with a manual button.                                                                               |
