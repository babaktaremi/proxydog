<p align="center">
  <img src="ProxyDog.App/Assets/AppIcon.png" width="128" alt="ProxyDog">
</p>

<h1 align="center">ProxyDog</h1>

<p align="center">
  Route any application through a SOCKS5 or HTTP proxy, one app at a time, and watch every connection it makes.
</p>

<p align="center">
  <a href="https://github.com/babaktaremi/proxydog/actions/workflows/ci.yml"><img src="https://github.com/babaktaremi/proxydog/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <a href="https://github.com/babaktaremi/proxydog/releases/latest"><img src="https://img.shields.io/github/v/release/babaktaremi/proxydog" alt="Latest release"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-Apache--2.0-blue" alt="Apache-2.0"></a>
</p>

## What it does

A system-wide VPN sends everything through one tunnel. ProxyDog is the opposite: you pick **one application**, give it a
proxy, and only that application's traffic goes through it. Everything else on your machine is untouched.

- **Per-app rules** with SOCKS5 or HTTP (CONNECT) proxies, with optional username and password.
- **A live log per rule**: every destination, whether it connected, and how much was sent and received.
- **An on/off switch per rule**, in the window and in the menu bar / tray.
- **Kill switch** (optional, per rule): closes the app if the proxy stops responding or the rule is turned off, so it
  can never quietly carry on without the proxy.
- **Works with the apps you actually use**: native apps, Chromium and Electron apps (Chrome, Brave, VS Code, Slack...),
  Telegram, and apps built on Apple's Network.framework.
- **Update alerts** when a new release is published.

| Rules | Live connection log |
| --- | --- |
| ![Rules](docs/screenshots/rules-light.png) | ![Connection log](docs/screenshots/log-dark.png) |

## Install

**macOS 12 or newer** (Apple Silicon and Intel)

```sh
brew tap babaktaremi/tap
brew trust babaktaremi/tap      # Homebrew 7+ asks you to trust a third-party tap once
brew install --cask proxydog
```

Or download `ProxyDog-<version>-macos-<arm64|x64>.zip` from the [latest release](https://github.com/babaktaremi/proxydog/releases/latest),
drag **ProxyDog.app** to Applications, and open it with right-click > **Open** the first time. The app is signed
ad hoc but not notarized by Apple, so macOS asks once. Homebrew clears that prompt for you.

**Linux** (x86_64 and aarch64, glibc)

| Distribution | Command |
| --- | --- |
| Arch / Manjaro (AUR) | `yay -S proxydog-bin` |
| Arch (package file) | `sudo pacman -U proxydog-bin-<version>-1-x86_64.pkg.tar.zst` (from the [release](https://github.com/babaktaremi/proxydog/releases/latest)) |
| Snap | `sudo snap install proxydog --classic` |
| Any distribution | Download `proxydog-<version>-linux-<x64\|arm64>.tar.gz`, unpack it and run `proxydog/ProxyDog` |

The Snap needs `--classic` because ProxyDog starts other programs from your system with a preload library; a
sandboxed snap cannot do that.

> The Homebrew cask follows the latest release. The AUR and Snap Store entries appear once the maintainer's store accounts
> are connected (see [docs/RELEASING.md](docs/RELEASING.md)); until then use the package file or tarball.

## Use it

1. Click **Add Rule**, choose SOCKS5 or HTTP, and enter the proxy's host and port.
2. Click **Choose...** and pick the application.
3. Save, then switch the rule **on**. ProxyDog opens the app with its traffic routed through your proxy, and the rule's
   tab starts filling with connections.

If the app is already running when you switch a rule on, ProxyDog can restart it so the proxy takes effect
(**If already running > Restart it**, the default).

## How it works

ProxyDog runs a small forwarding engine on `127.0.0.1` for each rule. How an app is pointed at it depends on the app:

| App | Method |
| --- | --- |
| Most native apps | The app is started with a small preload library (`DYLD_INSERT_LIBRARIES` on macOS, `LD_PRELOAD` on Linux) that redirects its `connect()` and `connectx()` calls to the engine. |
| Network.framework / `URLSession` apps on macOS 14+ | The same library registers the engine as a SOCKS5 proxy with Network.framework, which these apps use instead of sockets. |
| Chromium and Electron apps | Started with `--proxy-server` pointing at the engine (the hardened runtime stops the library from loading into them). |
| Telegram | Handed the engine as its own SOCKS5 proxy setting through a `tg://socks` link; you confirm once in Telegram. |

When a connection is tunnelled, the engine opens it to your proxy, performs the SOCKS5 or HTTP CONNECT handshake for
the real destination, and relays the bytes. If the engine is not running, redirected connections fail, so a hooked app
never silently falls back to a direct connection.

### Limits

- **UDP is not proxied** (calls, QUIC/HTTP3, game traffic). Only TCP is.
- Apps that macOS refuses to inject into (system programs, and hardened-runtime apps that are not Chromium-based) cannot be
  routed; ProxyDog warns you when it detects one.
- Programs that bypass libc, such as Go binaries, are not covered by the preload library.
- ProxyDog is not a security product. It routes traffic; it does not anonymise it.

## Updates

ProxyDog checks GitHub for a newer release shortly after it starts and every 12 hours. When there is one you get a banner
in the window, an item in the tray menu and a system notification, with the right update command for how you installed
it. You can skip a version, turn the check off (tray menu > **Check Automatically**), or check by hand from **About**.
The check only asks GitHub for the latest release number; nothing else is sent.

## Build from source

You need the .NET SDK pinned in [`global.json`](global.json) and a C compiler (`clang` or `gcc`) for the preload library.

```sh
dotnet build ProxyDog.slnx
dotnet test ProxyDog.Tests
dotnet run --project ProxyDog.App
```

Packaging scripts live in [`scripts/`](scripts); see [docs/RELEASING.md](docs/RELEASING.md) for how releases are made.

## Contributing

Issues and pull requests are welcome. Please run `dotnet test` first; CI runs the suite on macOS and Linux.

## Contact

Babak Taremi, [babak.taremi.dev@gmail.com](mailto:babak.taremi.dev@gmail.com)

## License

[Apache License 2.0](LICENSE). Third-party components are listed in [NOTICE](NOTICE).
