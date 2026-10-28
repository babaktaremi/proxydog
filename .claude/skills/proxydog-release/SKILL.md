---
name: proxydog-release
description: How to build, version, tag and publish a ProxyDog release (GitHub Releases, Homebrew cask, pacman/AUR, Snap) and verify it. Use this whenever the task involves releasing or deploying ProxyDog, bumping or choosing a version number, writing the changelog for a release, tagging, the Release or CI GitHub workflows, building the macOS .app or Linux tarball, the Homebrew tap, the PKGBUILD or snapcraft file, update alerts, or a failed or half-finished release. Read it before touching any version, tag, workflow, packaging or release file, even for a small hotfix, because the order of steps and a few traps (picking the wrong workflow run, secrets you must not write, manual tap updates) are not obvious from the code.
---

# Releasing ProxyDog

ProxyDog (`babaktaremi/proxydog`) ships as a macOS app, Linux tarballs, a pacman package, a Homebrew cask and a Snap.
A tag does almost everything; a few steps are still manual. This skill is the checked procedure. Three ideas explain
most of it:

- **The git tag is the single source of truth for the version.** CI stamps the tag's number into the build, so the About
  window, `Info.plist`, package files and the in-app update check all agree. Never type a version into a package file.
- **A release is public and cannot be cleanly undone.** Tags, assets and the Homebrew cask are visible at once and
  installed apps are offered the new version within hours. Fix forward with a new patch release; never move, delete or
  reuse a published tag.
- **Verify, don't assume.** Several steps can "succeed" while doing nothing (a skipped job is green). End every release
  with `scripts/verify-release.sh`.

## Before you start

- **Confirm with the user before tagging or pushing** unless they asked for a release in this conversation. If they said
  "do not commit/push", that holds until they lift it.
- **Never write secrets or deploy keys yourself** (`gh secret set`, `gh repo deploy-key add`). That is blocked for agents and belongs to
  the user. If a channel needs one, give them the commands from `references/one-time-setup.md`.
- Check tools: `gh auth status` (logged in as `babaktaremi`, SSH for git), `dotnet --version` (global.json pins the SDK).

## Channels and their state

| Channel | How it ships | State |
| --- | --- | --- |
| GitHub release (macOS zips, Linux tarballs, `checksums.txt`) | automatic on tag | works |
| Pacman package (`proxydog-bin-<v>-1-x86_64.pkg.tar.zst`) | automatic (`pacman.yml`) | works |
| Homebrew cask (`babaktaremi/homebrew-tap`) | **manual**: `scripts/update-tap.sh <v>` | automatic once `TAP_DEPLOY_KEY` is set |
| AUR `proxydog-bin` | automatic if `AUR_SSH_PRIVATE_KEY` is set | skipped: no key |
| Snap Store | built every release; published if `SNAPCRAFT_STORE_CREDENTIALS` is set | not published: no credentials, and classic confinement needs Store approval |

Check the secrets with `gh secret list -R babaktaremi/proxydog`; this table may be out of date once the user adds some.

## 1. Choose the version (Semantic Versioning)

`MAJOR.MINOR.PATCH`, optional `-prerelease` (`1.2.0-rc.1`).

- **PATCH**: bug fixes only, nothing new (1.0.0 → 1.0.1).
- **MINOR**: new features that keep existing rules and settings working (V2Ray profiles took 1.0.0 → 1.1.0).
- **MAJOR**: breaks saved data (`rules.json`, `settings.json`, profiles) or removes something people use.
- **Prerelease** (`-rc.N`): publishes a GitHub *prerelease*; the update alert, Homebrew, AUR and Snap ignore it. Use it
  to test the pipeline end to end with real users or machines.

Ask the user if it is genuinely ambiguous; do not guess a major bump.

## 2. Prepare the release commit

1. **Clean tree.** `git status --short` must be empty. Commit the feature work first, in its own commit, so the release
   commit contains only release prep.
2. **Test.** `dotnet build ProxyDog.slnx && dotnet test ProxyDog.Tests` and expect zero failures. If the change touches the
   native hook, process handling, packaging or anything OS-specific, also run the suite on Linux in Docker (command in
   `references/pipeline.md`); macOS-only green has hidden Linux bugs before.
3. **`CHANGELOG.md`**: rename `## [Unreleased]` content into `## [X.Y.Z] - YYYY-MM-DD` (today) with *Added / Changed / Fixed*
   bullets written for users, keep an empty `## [Unreleased]` above it, and fix the compare links at the bottom
   (`[Unreleased]: .../compare/vX.Y.Z...HEAD`, `[X.Y.Z]: .../compare/vPREV...vX.Y.Z`). This section becomes the release
   notes verbatim, and `scripts/release.sh` refuses to run without it.
4. **`ProxyDog.App/ProxyDog.App.csproj`**: set `<Version>X.Y.Z</Version>`. CI overrides it from the tag; this only keeps local
   builds honest.
5. Update `README.md` / `PRODUCT.md` if user-visible behaviour changed.
6. Commit as `Release X.Y.Z`, push `main`.
7. **Wait for CI to pass on that exact commit** (don't tag a red or unbuilt `main`):

   ```sh
   sha=$(git rev-parse HEAD)
   id=$(gh run list -R babaktaremi/proxydog --workflow CI --limit 10 --json databaseId,headSha --jq "[.[]|select(.headSha==\"$sha\")][0].databaseId")
   gh run watch "$id" -R babaktaremi/proxydog --exit-status
   ```

## 3. Tag

```sh
scripts/release.sh X.Y.Z
```

It checks a clean tree, branch `main` in sync with `origin/main`, a valid version, no existing tag, and a changelog
section, then creates an annotated tag and pushes it, which starts the **Release** workflow.

If it ends with *"Please make sure you have the correct access rights"*, a `git fetch` failed transiently before anything
was tagged. Confirm with `git tag -l vX.Y.Z` (empty), then simply re-run it.

## 4. Watch the Release workflow

Right after the push, "the latest run" is still the *previous* release's. Select by tag:

```sh
id=$(gh run list -R babaktaremi/proxydog --workflow Release --limit 10 --json databaseId,headBranch --jq '[.[]|select(.headBranch=="vX.Y.Z")][0].databaseId')
gh run watch "$id" -R babaktaremi/proxydog --exit-status
```

Expected jobs: version → macOS ×2 and Linux ×2 builds → publish release → pacman, snap ×2, Homebrew, AUR. The Homebrew
and AUR jobs show green while doing nothing when their secret is missing; the log says "…is not set; skipping." That is
why step 5 exists. `references/pipeline.md` describes each job.

## 5. After the workflow

1. **Homebrew** (stable releases only), until `TAP_DEPLOY_KEY` exists:

   ```sh
   scripts/update-tap.sh X.Y.Z --dry-run   # shows the cask diff, pushes nothing
   scripts/update-tap.sh X.Y.Z
   ```

   It renders the cask from the published `checksums.txt` and pushes one commit to the tap.
2. **Verify**: `scripts/verify-release.sh X.Y.Z` (add `--full` to re-download and re-hash every archive). It checks the release
   is published, all assets exist, the tag's workflow run succeeded, the cask matches the checksums, and GitHub's "latest
   release" is this one (that last point is what makes installed apps show the update alert). Do not report success until it ends with
   *"looks complete"*.
3. Optional, cheap: unzip the macOS zip and `codesign --verify --deep --strict` it, or launch it.

## 6. Tell the user

Say what shipped (version, link), which channels are live, which were skipped and why (missing secrets, Store approval),
and anything unverified (for example the Snap was never installed). Don't claim a channel is live because its job was green.

## Dry run (no publishing)

To test packaging or a workflow change without a release:

```sh
gh workflow run release.yml -R babaktaremi/proxydog -f version=0.0.0-dryrun
```

It builds all four packages and keeps them as workflow artifacts; the publish jobs are skipped. Download with
`gh run download <id> -R babaktaremi/proxydog` to inspect. `pacman.yml` can also be run by hand for an existing release:
`gh workflow run pacman.yml -R babaktaremi/proxydog -f version=X.Y.Z`.

## When something fails

Fix forward. Details and past incidents are in `references/troubleshooting.md`; the rules:

- A build/test job failed **before the release was published**: fix on `main`, then you may delete the *unpublished* tag
  (`git push origin :refs/tags/vX.Y.Z` and `git tag -d vX.Y.Z`) **only if no release object exists** (`gh release view vX.Y.Z`),
  and re-tag. Ask the user first.
- The release **is published** but a later channel failed (pacman, Homebrew): fix the workflow or data on `main`, then re-run
  just that channel (`pacman.yml` by hand, `scripts/update-tap.sh`). The tag keeps pointing at the old workflow files, so
  re-running the failed job from the old run will fail the same way.
- A published release is **bad**: `gh release delete vX.Y.Z -R babaktaremi/proxydog` (installed apps then follow the latest remaining
  stable release), ship a patch with a new number, and say so in its changelog. Never reuse a version number.

## Files this skill relies on

`scripts/release.sh`, `scripts/verify-release.sh`, `scripts/update-tap.sh`, `scripts/package-macos.sh`,
`scripts/package-linux.sh`, `scripts/render.sh`, `.github/workflows/{ci,release,pacman}.yml`, `packaging/`, `CHANGELOG.md`,
`docs/RELEASING.md` (the human-facing version of this process).
