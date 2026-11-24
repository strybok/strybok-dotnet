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

Full guide, native setup and troubleshooting:
[`docs/dotnet-maui.md`](./docs/dotnet-maui.md).
Behaviour shared by all SDKs: [`docs/sdk-contract.md`](./docs/sdk-contract.md).

| Project               | Targets                    | Contents                                                                |
| --------------------- | -------------------------- | ----------------------------------------------------------------------- |
| `src/Strybok`         | `netstandard2.0`, `net8.0` | `StrybokClient`, options, `ILinkSource`, `ManualLinkSource`, exceptions |
| `src/Strybok.Maui`    | `net8.0`                   | `AddStrybok(...)`, platform detection through `DeviceInfo.Platform`     |
| `tests/Strybok.Tests` | `net8.0`                   | xUnit tests of the contract (core only)                                 |

> **Early release (0.2.x).** The core library builds and passes its tests in CI. The MAUI glue is
> compiled but has no tests and has never been run inside a MAUI app. Not verified on devices.
>
> Deferred deep linking is not supported.

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
