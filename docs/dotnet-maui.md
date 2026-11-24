# .NET MAUI integration

The .NET SDK is for .NET MAUI apps, and for .NET for iOS / .NET for Android apps without MAUI.
Your app hands it the verified HTTPS links the OS delivers, it asks the Strybok API which in-app
route a link stands for, and it gives your app each link exactly once.

> **Status — read this first.** Early release (0.2.x), published on NuGet.
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
> Deferred deep linking is **not supported**: a link tapped before the app is installed is not
> restored after installation.

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

| Member                         | Purpose                                                                                                                             |
| ------------------------------ | ----------------------------------------------------------------------------------------------------------------------------------- |
| `InitializeAsync()`            | Start listening; completes after the launch link was handled. Idempotent.                                                           |
| `LinkReceived`                 | Event with a `ResolvedDeepLink` (`Uri`, `LinkId`, `AppId`, `RoutePath`, `Payload`). Queued links are replayed to the first handler. |
| `GetInitialLinkAsync()`        | The launch link, if not delivered through `LinkReceived` yet.                                                                       |
| `ResolveAsync(uri, token)`     | Resolve a URI explicitly (QR scanner, tests). Does not raise `LinkReceived` or report an app open.                                  |
| `IsStrybokUri(uri)`            | Whether a URI (or URL string) is one of this app's links.                                                                           |
| `CreateLinkAsync(routePath)`   | Create a short link to share, see [below](#create-a-link-to-share).                                                                 |
| `Dispose()` / `DisposeAsync()` | Stop listening and release resources. Nothing is delivered afterwards.                                                              |

Options (`StrybokOptions`): `ApiBaseUrl`, `AllowedHosts` (both required), `Timeout` (5 s per
attempt), `MaxRetries` (2; only timeouts, network errors and 5xx are retried), `RetryDelay`
(400 ms, doubling), `DedupeWindow` (2 s), `ReportAppOpens` (on), `Platform`, `OnError`,
`ClientKey` (only for creating links), plus
`HttpMessageHandler`, `Clock` and `Delay` for custom transports and tests. An `HttpClient` can be
passed to the `StrybokClient` constructor instead.

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

Only the link's host and slug are sent to `resolve`; query parameters and fragments stay on the
device.

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
