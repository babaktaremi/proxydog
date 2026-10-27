# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

<!-- Note: ProxyDog is a native desktop app (Avalonia, macOS + Linux). Impeccable's init only accepts web/ios/android/adaptive; `web` is recorded because none of the mobile platform guidance applies. -->

## Stack

Avalonia UI on .NET (user-specified).

## Users

Developers and DevOps engineers, mainly. Other technical and general users should be able to use it too. They need specific applications to reach the network through a SOCKS5 or HTTP proxy without routing the whole machine through it.

## Product Purpose

ProxyDog proxifies any application through proxy rules the user defines. The user sets up a rule, picks the application it applies to, and can see what that application connects to. Success is that the chosen app's traffic goes through the proxy and the user can verify it from the connection log.

## Positioning

Proxy rules are per application, not system-wide, and each rule has its own live connection log.

## Operating Context

Runs as a desktop app on macOS and Linux and lives in the top bar (macOS menu bar, Linux tray). Used alongside the other tools a developer or DevOps engineer keeps open.

## Capabilities and Constraints

- Two proxy types: SOCKS5 and HTTP.
- Flow: the user defines a rule, then a picker opens and the user selects the application to proxify.
- After successful configuration, a tab is added to the app showing that application's connection logs through the proxy.
- Each rule has an on/off toggle.
- The app is present in the top navigation bar (menu bar / tray) on macOS and Linux.
- Mechanism: ProxyDog launches the chosen app with a preloaded hook (`DYLD_INSERT_LIBRARIES` on macOS, `LD_PRELOAD` on Linux). It redirects `connect()` and `connectx()` to a local forwarding engine, and on macOS 14+ registers the engine as a SOCKS5 proxy with Network.framework (which URLSession and most modern Mac apps use, with no BSD sockets). Chromium-based apps are launched with `--proxy-server` instead. No root required. UDP traffic is not proxied. Apps protected by SIP or hardened runtime (and not Chromium-based) cannot be hooked.
- Platforms for v1: macOS and Linux. Windows is deferred.
- Undecided: what happens to a running app when its rule is toggled off (terminate or leave it running unproxied).

## Evidence on Hand

None. No users, metrics, testimonials, or assets exist yet; future work must not fabricate any.

## Product Principles

- Per application, not system-wide: the user chooses exactly what gets proxied.
- Visibility: every rule shows what its app connects to.
- Rule state is always obvious: on or off is never ambiguous.
- No root required for normal use.
- Say plainly when an app cannot be hooked instead of failing silently.
