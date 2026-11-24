## 0.1.0

- Initial version. Core library `Strybok` (`netstandard2.0`, `net8.0`): `StrybokClient` with
  `InitializeAsync`, `LinkReceived`, `GetInitialLinkAsync`, `ResolveAsync`, `IsStrybokUri`,
  `Dispose`/`DisposeAsync`; host allowlist with strict validation of the URL as received;
  duplicate-delivery protection; timeout and retry; typed exceptions; best-effort `app_open`
  events; `ILinkSource` and `ManualLinkSource`; replaceable HTTP transport, clock and delay.
- `Strybok.Maui`: `AddStrybok(...)` service registration and platform detection.
- Not compiled, not run and not published yet (see README).
