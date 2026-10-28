# One-time setup the user has to do

Agents cannot store these: writing to the repository's secret store or adding deploy keys is blocked for them, and credentials
for the AUR and the Snap Store belong to the user's own accounts. Hand the user the commands below, say which channel each
unlocks, and tell them that typing `!` before a command in the Claude Code prompt runs it in the session.

## Homebrew tap auto-update: secret `TAP_DEPLOY_KEY`

Unlocks the `homebrew` job so the cask updates itself (until then use `scripts/update-tap.sh <version>` after each release).

```sh
ssh-keygen -t ed25519 -N '' -C "proxydog-release-workflow" -f /tmp/tapkey
gh repo deploy-key add /tmp/tapkey.pub -R babaktaremi/homebrew-tap --allow-write --title "proxydog release workflow"
gh secret set TAP_DEPLOY_KEY -R babaktaremi/proxydog < /tmp/tapkey
rm /tmp/tapkey /tmp/tapkey.pub
```

## AUR: secret `AUR_SSH_PRIVATE_KEY`

1. Account at <https://aur.archlinux.org>; add an SSH public key to the profile.
2. Register the package once: `git clone ssh://aur@aur.archlinux.org/proxydog-bin.git`, add a rendered `PKGBUILD` and `.SRCINFO`, push.
3. `gh secret set AUR_SSH_PRIVATE_KEY -R babaktaremi/proxydog < ~/.ssh/<the matching private key>`.

## Snap Store: secret `SNAPCRAFT_STORE_CREDENTIALS`

1. `snapcraft register proxydog`.
2. Request **classic confinement** (reviewed by hand) at <https://forum.snapcraft.io/c/store-requests/19>: ProxyDog launches other programs with `LD_PRELOAD`.
3. `snapcraft export-login --snaps proxydog --acls package_access,package_push,package_release creds.txt`, then
   `gh secret set SNAPCRAFT_STORE_CREDENTIALS -R babaktaremi/proxydog < creds.txt`.

## macOS notarization (optional, not set up)

Needs a paid Apple Developer ID. Add the certificate and an app-specific password as secrets, then extend
`scripts/package-macos.sh` with `codesign --options runtime --sign "Developer ID Application: …"` and
`xcrun notarytool submit --wait`. Until then builds are ad-hoc signed.
