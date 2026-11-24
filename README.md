# Strybok (.NET SDK)

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

Full guide, native setup and troubleshooting:
[`docs/dotnet-maui.md`](./docs/dotnet-maui.md).
Behaviour shared by all SDKs: [`docs/sdk-contract.md`](./docs/sdk-contract.md).

| Project               | Targets                    | Contents                                                                |
| --------------------- | -------------------------- | ----------------------------------------------------------------------- |
| `src/Strybok`         | `netstandard2.0`, `net8.0` | `StrybokClient`, options, `ILinkSource`, `ManualLinkSource`, exceptions |
| `src/Strybok.Maui`    | `net8.0`                   | `AddStrybok(...)`, platform detection through `DeviceInfo.Platform`     |
| `tests/Strybok.Tests` | `net8.0`                   | xUnit tests of the contract (core only)                                 |

> **Status: written without a .NET SDK on the authoring machine. Nothing in this package has been
> compiled or run yet** — not the library, not the MAUI glue, not the tests. Build it and run the
> tests before relying on it. Not verified on devices and not published to NuGet.
>
> Deferred deep linking is not supported.

## Install

Not on NuGet yet. Clone `https://github.com/strybok/strybok-dotnet` next to your app (or add it as
a git submodule) and reference the projects:

```xml
<ItemGroup>
  <ProjectReference Include="..\strybok-dotnet\src\Strybok\Strybok.csproj" />
  <ProjectReference Include="..\strybok-dotnet\src\Strybok.Maui\Strybok.Maui.csproj" />
</ItemGroup>
```

## Build and test

In the SDK directory:

```sh
dotnet test Strybok.sln                                  # core library + tests
dotnet build src/Strybok.Maui/Strybok.Maui.csproj        # MAUI glue (not part of the solution)
```

## Licence

Apache 2.0, see [LICENSE](./LICENSE).
