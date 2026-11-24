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
