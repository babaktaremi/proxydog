# Changelog

All notable changes to ProxyDog are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and versions follow [Semantic Versioning](https://semver.org/). Release notes on GitHub are taken from the matching section.

## [Unreleased]

## [1.0.0] - 2026-10-27

First release.

### Added
- Route any application through a SOCKS5 or HTTP proxy, chosen per application, on macOS and Linux.
- A tab per rule with a live log of every connection: destination, status, and bytes sent and received.
- An on/off switch per rule, in the app and in the menu bar / tray.
- Kill switch: close the application if the proxy stops responding or the rule is turned off.
- Chromium and Electron apps (Chrome, Brave, VS Code, ...) are routed with `--proxy-server`; Telegram is handed its own proxy setting.
- Apps using Network.framework or `connectx()` on macOS (such as Speedtest) are routed too.
- Recent proxies dropdown, restart-if-already-running option, and a stable local port per rule.
- Update alerts: a banner, a menu item and a system notification when a newer release is published.

[Unreleased]: https://github.com/babaktaremi/proxydog/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/babaktaremi/proxydog/releases/tag/v1.0.0
