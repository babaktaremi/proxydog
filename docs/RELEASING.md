# Releasing ProxyDog

## Versioning

ProxyDog follows [Semantic Versioning](https://semver.org/): `MAJOR.MINOR.PATCH`, with an optional `-prerelease` tag
(`1.2.0-rc.1`).

- **MAJOR**: a change that breaks existing rules or settings files.
- **MINOR**: new features that keep working with existing data.
- **PATCH**: bug fixes only.

**The git tag is the single source of truth.** A tag `v1.2.3` produces version `1.2.3`. CI passes that number to the build
(`-p:Version=1.2.3`), so the About window, the macOS `Info.plist`, the package files and the in-app update check all agree.
The `<Version>` in `ProxyDog.App.csproj` is only the number used for local builds.

A tag with a `-prerelease` part is published as a GitHub *prerelease*: it is not offered by the in-app update alert
(which only follows the latest stable release) and it does not update Homebrew, the AUR or the Snap Store.

## Cutting a release

1. Move the changes from `## [Unreleased]` in `CHANGELOG.md` to a new `## [x.y.z] - date` section and update the
   compare links at the bottom. This section becomes the release notes.
2. Commit and push to `main`, and wait for CI to pass.
3. Run:

   ```sh
   scripts/release.sh 1.2.3
   ```

   It checks that the tree is clean, you are on an up-to-date `main`, the version is valid and the changelog has a
   section for it, then pushes the tag `v1.2.3`.

The tag starts the **Release** workflow.

## What the pipeline does

| Step | Result |
| --- | --- |
| macOS (arm64 + x64) | `ProxyDog-<v>-macos-<arch>.zip`: `ProxyDog.app`, self-contained, ad-hoc signed |
| Linux (x64 + arm64) | `proxydog-<v>-linux-<arch>.tar.gz`: self-contained app, desktop file, icon |
| GitHub release | Both of the above, `checksums.txt`, and notes from `CHANGELOG.md` |
| Homebrew | Updates `Casks/proxydog.rb` in `babaktaremi/homebrew-tap` |
| Pacman | Builds `proxydog-bin-<v>-1-x86_64.pkg.tar.zst` in an Arch container and attaches it to the release |
| AUR | Pushes the `PKGBUILD` to `proxydog-bin` (needs a secret, below) |
| Snap | Builds the snap for amd64 and arm64, and publishes it (needs a secret, below) |

To test the packaging without publishing anything, run the workflow by hand: **Actions > Release > Run workflow**. It
builds every package and uploads them as workflow artifacts, but creates no release.

## One-time setup

### Homebrew tap (already configured)

`babaktaremi/homebrew-tap` is a normal repository. The release job pushes to it with a deploy key:

- the tap has a **deploy key with write access** (public half), and
- this repository has the matching private key as the secret **`TAP_DEPLOY_KEY`**.

To rotate it: `ssh-keygen -t ed25519 -N '' -f tap_key`, add `tap_key.pub` as a deploy key on the tap (allow write), then
`gh secret set TAP_DEPLOY_KEY < tap_key`.

### AUR (optional)

1. Create an account at <https://aur.archlinux.org> and add an SSH public key to it.
2. Register the package once: `git clone ssh://aur@aur.archlinux.org/proxydog-bin.git`, add a `PKGBUILD`, push.
3. Store the matching **private** key as the secret `AUR_SSH_PRIVATE_KEY`.

Without the secret the AUR job is skipped; the pacman package is still attached to every release.

### Snap Store (optional)

1. `snapcraft register proxydog` with your Snapcraft account.
2. ProxyDog needs **classic confinement**, which the Snap Store reviews by hand: request it at
   <https://forum.snapcraft.io/c/store-requests/19> (explain that it launches other programs with `LD_PRELOAD`).
3. `snapcraft export-login --snaps proxydog --acls package_access,package_push,package_release creds.txt`, then
   `gh secret set SNAPCRAFT_STORE_CREDENTIALS < creds.txt`.

Without the secret the snap is built and kept as an artifact but not published.

## macOS signing

Builds are **ad-hoc signed**, not notarized, because notarization needs a paid Apple Developer ID. People installing by
hand open the app once with right-click > **Open**; the Homebrew cask removes the quarantine flag itself. To notarize
later, add the Developer ID certificate and an app-specific password as secrets and extend `scripts/package-macos.sh`
with `codesign --options runtime --sign "Developer ID Application: ..."` and `xcrun notarytool submit --wait`.
Note that enabling the hardened runtime for ProxyDog itself is fine: only the apps it launches need to be hookable.

## Update alerts

The app asks `https://api.github.com/repos/babaktaremi/proxydog/releases/latest` (stable releases only), compares the tag
with its own version, and shows the banner, tray item and a one-time system notification. Which update command it shows
depends on how it was installed (Homebrew cask, Snap, pacman, or none for a manual download).

If you ever publish a release by mistake, delete it on GitHub; the alert follows the latest remaining stable release.
