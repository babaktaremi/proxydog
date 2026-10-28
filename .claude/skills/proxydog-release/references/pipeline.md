# How the release pipeline works

Read this when changing a workflow, a packaging file or a script, or when a job fails and you need to know what it should
have done.

## Version stamping

`ProxyDog.App.csproj` has a default `<Version>`. Every package script passes `-p:Version=<tag without v>` to `dotnet publish`,
and a command-line property wins over the project file. At runtime `AppInfo.VersionText` reads the assembly's informational
version and strips the `+commit` suffix. The About window, the update check (`AppInfo.Version` → `SemVer`) and the macOS
`Info.plist` (`CFBundleShortVersionString`; `CFBundleVersion` is the numeric part only) all come from that one number.

## Workflows (`.github/workflows/`)

- **`ci.yml`**: on push to `main`, PRs and manual runs. `dotnet build` + `dotnet test` on `ubuntu-24.04` and `macos-14`, then a
  packaging smoke test (`scripts/package-linux.sh linux-x64 0.0.0-ci`, checks the tarball contents).
- **`release.yml`**: on tags `v[0-9]+.[0-9]+.[0-9]+*`, or manual `workflow_dispatch` with a `version` input (no publishing).
  - `prepare`: derives `version`, validates it as SemVer, sets `publish` and `prerelease` outputs.
  - `macos` (matrix `osx-arm64`, `osx-x64`, both on `macos-14`): `scripts/package-macos.sh`.
  - `linux` (matrix `linux-x64` on `ubuntu-24.04`, `linux-arm64` on `ubuntu-24.04-arm`): `scripts/package-linux.sh`. The native hook is
    compiled with the local compiler, so each architecture must build on its own runner.
  - `release` (publish only): downloads artifacts, writes `checksums.txt`, extracts the version's section from `CHANGELOG.md`
    as notes (falls back to GitHub's generated notes), runs `gh release create`.
  - `homebrew` (stable only): needs secret `TAP_DEPLOY_KEY`; renders `packaging/homebrew/proxydog.rb.in` with the published
    SHA-256s and pushes to `babaktaremi/homebrew-tap` over a deploy key. Skips cleanly without the secret.
  - `arch`: calls `pacman.yml`.
  - `aur` (stable only): needs `AUR_SSH_PRIVATE_KEY`; `continue-on-error`.
  - `snap` (stable only, amd64 + arm64): renders `packaging/snap/snapcraft.yaml.in`, builds with `snapcore/action-build`,
    publishes only if `SNAPCRAFT_STORE_CREDENTIALS` is set; `continue-on-error`.
- **`pacman.yml`**: reusable (`workflow_call`) and manual. In an `archlinux` container: renders `packaging/arch/PKGBUILD.in`
  from the release's `checksums.txt`, runs `makepkg -f -d` as a normal user (`-d`: the package only repackages binaries, so
  runtime deps are not needed to build), checks the contents, uploads the `.pkg.tar.zst` to the release.

## Packaging scripts

- `scripts/package-macos.sh <osx-arm64|osx-x64> <version> [outdir]`: `dotnet publish` self-contained, builds `ProxyDog.app`
  (Info.plist, `AppIcon.icns`, the hook in `Contents/MacOS/native/`), ad-hoc signs (`codesign --force --deep --sign -`), zips with `ditto`.
  Ad-hoc signed, **not notarized** (no paid Apple ID), so the cask removes the quarantine flag.
- `scripts/package-linux.sh <linux-x64|linux-arm64> <version> [outdir]`: tarball `proxydog-<v>-linux-<arch>.tar.gz` containing
  `proxydog/` (the app, `native/libproxydog_hook.so`), `proxydog.desktop`, `proxydog.png`, `LICENSE`, `NOTICE`, `README.md`.
  Run it on Linux of the same architecture.
- `scripts/render.sh <template> <out>`: replaces `@NAME@` with the environment variable `NAME` and **fails if one is unset**.
- Asset names are a contract: `ProxyDog-<v>-macos-<arm64|x64>.zip`, `proxydog-<v>-linux-<x64|arm64>.tar.gz`,
  `proxydog-bin-<v>-1-x86_64.pkg.tar.zst`, `checksums.txt`. The cask, PKGBUILD, snap, `verify-release.sh` and `UpdateChecker`
  all depend on them. Change one and you must change all.

## Native hook and publish

The app's csproj builds `ProxyDog.Hook/build.sh` after a normal build (`BuildNativeHook`) and copies the result into the publish
folder (`CopyHookToPublish`). `AppLauncher.FindHook()` looks in `AppContext.BaseDirectory/native/`.

## Testing on Linux from a Mac

Docker (OrbStack) works. Docker Hub library images can fail with a 403 here; `mcr.microsoft.com/dotnet/sdk:10.0` works.

```sh
rsync -a --delete --exclude bin --exclude obj --exclude dist --exclude .git --exclude .idea \
  --exclude .claude --exclude .agents --exclude .codex --exclude .impeccable ./ /tmp/claude-501/linuxsrc/
docker run --rm -v /tmp/claude-501/linuxsrc:/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 bash -c \
  'apt-get update -qq >/dev/null 2>&1 && apt-get install -y -qq gcc libc6-dev procps >/dev/null 2>&1; dotnet test ProxyDog.Tests'
```

Copy the source first so the container's `bin/obj` don't clobber yours. The first run is slow (image pull, apt, restore).
To smoke-test a built Linux tarball: unpack it in the same image with `xvfb libx11-6 libice6 libsm6 libfontconfig1 libgl1`
installed and run `timeout 10 xvfb-run -a ./ProxyDog`; exit code 124 means it was still running.

## Where the version is read at runtime

The update checker calls `https://api.github.com/repos/babaktaremi/proxydog/releases/latest` (stable only, drafts excluded) and
compares the tag with `AppInfo.Version`. So the release being *the latest non-prerelease* is what triggers alerts;
`verify-release.sh` checks this.
