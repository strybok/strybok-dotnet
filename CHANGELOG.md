## 0.3.0

- Campaign values. `ResolvedDeepLink.Campaign` (`LinkCampaign`: `Source`, `Medium`, `Campaign`,
  `Term`, `Content`, each `string?`; `IsEmpty`; `LinkCampaign.Empty`) holds the link's default
  campaign values overridden by the `utm_*` parameters of the tapped URL, as the service returned
  them. It is never `null`. The values are untrusted labels chosen by whoever shared the link.
- The resolve request now carries the five parameters `utm_source`, `utm_medium`, `utm_campaign`,
  `utm_term` and `utm_content` of the tapped URL — exact lower-case names, the first occurrence
  of each, percent-decoded, and only values of 1–64 characters out of `A–Z a–z 0–9 . _ ~ -`, in
  a fixed order. Every other query parameter and the fragment are still never sent, and a URL
  without valid campaign parameters produces exactly the request of earlier versions. Like the
  link check itself, the filter works on `Uri.OriginalString`, not on a normalised URI.
- The `app_open` event reports `campaign` exactly as resolve returned it and leaves it out when
  there is none; it is never built from the URL.
- Additive: `ResolvedDeepLink` keeps its five-argument constructor and gains one with a
  `LinkCampaign?`. Which URIs are accepted, duplicate detection (the whole URL string as
  received) and everything else are unchanged.
- Deferred links, optional and **off by default; nothing of it has run on a device**. Options
  `DeferredLinks`, `AllowProbabilisticMatching`, `InstallReferrerSources`, `AndroidPackageName`,
  `IosBundleId`, `DeviceFamily`, `Locale`, `PasteboardMayContainLink` and `DeferredLinkStore`
  (`IDeferredLinkStore`); `StrybokClient.DeferredLinkStatus` / `DeferredLinkStatusChanged`,
  `EnableDeferredLinks()`, `RedeemPastedLinkAsync(text)` and `SkipPastedLink()`;
  `ResolvedDeepLink.IsDeferred` and `IsProbabilisticMatch` (both `false` for every normal link;
  a constructor that takes them was added, the existing ones are unchanged). The whole flow of
  the SDK contract is implemented: the attempt is stored before a source is asked, at most three
  checks per installation, "done" is stored before delivery, a normal link wins, no `app_open`,
  nothing to `OnError`, never from `GetInitialLinkAsync`.
  - **The packages ship no store integration and no native code.** On Android your app supplies
    the install referrer through `InstallReferrerSource` delegates (`InstallReferrerAnswer`:
    referrer string, unavailable for now, or unavailable); on iOS it supplies the pasted text.
    The SDK never reads the pasteboard and never logs or stores the referrer, the pasted text or
    the id.
  - The core has no default store: without `DeferredLinkStore` and the platform's identifier
    the status stays `Disabled` (never a store that only lives in memory).
  - iOS probabilistic matching is forbidden by Apple's rules and stays off unless
    `AllowProbabilisticMatching` is set; a matched link is marked `IsProbabilisticMatch`.
- `Strybok.Maui` 0.3.0: `AddStrybok` pre-fills what deferred links need and MAUI has —
  `PreferencesDeferredLinkStore` (MAUI `Preferences`), the package name / bundle id from
  `AppInfo.PackageName`, `DetectDeviceFamily()` from `DeviceInfo` and `DetectLocale()` from
  `CultureInfo.CurrentUICulture` — all replaceable in the callback. Deferred links stay off unless
  `DeferredLinks` is set. Compiled by CI only; never run inside a MAUI app.

## 0.2.0

- `StrybokClient.CreateLinkAsync(routePath, payload, title, description, imageUrl, cancellationToken)`:
  create a short link to a screen of the app for a "Share" button. Needs the new
  `StrybokOptions.ClientKey` (publishable key `pk_…`). Returns a `CreatedLink` (`Url`, `Slug`,
  `RoutePath`, `LinkId`). Timeouts, network errors and 5xx are retried with the same body; 4xx
  never are.
- New exceptions for creating links: `StrybokUnauthorizedException`, `RouteNotAllowedException`
  (`RoutePath`), `StrybokRateLimitedException` (`RetryAfter`), `StrybokValidationException`
  (`StatusCode`, `Issues`).
- `Strybok.Maui`: no API change; set `ClientKey` in the `AddStrybok` callback.

## 0.1.0

- Initial version. Core library `Strybok` (`netstandard2.0`, `net8.0`): `StrybokClient` with
  `InitializeAsync`, `LinkReceived`, `GetInitialLinkAsync`, `ResolveAsync`, `IsStrybokUri`,
  `Dispose`/`DisposeAsync`; host allowlist with strict validation of the URL as received;
  duplicate-delivery protection; timeout and retry; typed exceptions; best-effort `app_open`
  events; `ILinkSource` and `ManualLinkSource`; replaceable HTTP transport, clock and delay.
- `Strybok.Maui`: `AddStrybok(...)` service registration and platform detection.
- Written without a .NET SDK at hand; first compiled, tested and published to NuGet by CI after
  this entry was written. The MAUI glue has only been compiled (see README).
