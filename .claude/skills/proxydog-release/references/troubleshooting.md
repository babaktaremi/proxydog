# Release troubleshooting: what has gone wrong before

Each entry is a real failure from shipping 1.0.0 and 1.1.0, with the cause and the fix. Check here before debugging a
failed release from scratch.

## Tests

- **macOS CI fails on `Finds_and_quits_a_running_copy` (process "not found").** The test copied Apple's `sleep`. On Apple
  Silicon those are `arm64e`, and macOS 14 will not run a *copied* `arm64e` binary without a boot flag, so the process died at
  once. Fix: compile a tiny C program in the test (`cc`), as the other tests do. **Never copy an Apple system binary in a test.**
- **Tests pass alone but fail together ("hook missing", "cc failed").** Test classes run in parallel and two of them built the
  native hook into the same folder at once. Fix: `EnsureHookBuiltAsync()` builds it once under a lock. Any new test that needs the
  hook must go through it (`KillSwitchTests.PrepareAppPublicAsync`).
- **A process-timing assertion flakes on CI.** Between fork and exec a new process still reports its parent's path, so
  `AppLauncher.FindRunning` can miss it for a moment. Poll, don't assert immediately.
- **The V2Ray core "failed to start" error had no explanation (Linux only).** The core's last words arrive on the output
  pipes after it exits. Use the parameterless `Process.WaitForExit()` (which waits for the redirected streams) before
  reading the captured lines.
- **A kill-switch test passes for the wrong reason.** A fake "server" pointing at a closed port makes the watchdog trip before
  the thing under test happens. Give tests a real listening port when they need the server to be healthy.

## Running things

- **`scripts/release.sh` ends with "Please make sure you have the correct access rights and the repository exists."** The
  pre-flight `git fetch` failed transiently, before any tag existed. `git tag -l vX.Y.Z` is empty; re-run the script.
- **`gh run watch` shows the previous release.** Right after a tag push the newest run is the old one. Select the run whose
  `headBranch` equals the tag (section 4 of SKILL.md).
- **`brew install` says the tap is untrusted.** Homebrew 7 requires `brew trust babaktaremi/tap` once (README and the tap's README say so).
- **`brew audit` says Command Line Tools are outdated.** A local machine problem; it does not mean the cask is wrong. Check the cask with
  `ruby -c` and verify the SHA-256s with `scripts/verify-release.sh`.
- **A `rm` with a glob or variable is refused by the permission system.** Avoid it: overwrite output instead, or use a script that
  removes only a directory it created.

## Workflows

- **`makepkg` fails with "Missing dependencies: libx11 …".** Building needs no runtime deps: use `makepkg -f -d`. Fixed in `pacman.yml`.
  Because the tag points at old workflow files, re-running the failed job reproduces the failure; run `pacman.yml` by hand instead.
- **The AUR job takes ~30 s but did nothing.** The action's Docker image is built at job setup, before the secret check. The
  step log says "AUR_SSH_PRIVATE_KEY is not set; skipping."
- **Homebrew job is green but the cask is old.** Same: skipped without `TAP_DEPLOY_KEY`. Run `scripts/update-tap.sh`.
- **`actions/*` show "Node.js 20 is deprecated".** A warning only; Dependabot opens PRs to bump the actions.
- **`dotnet restore` fails with "Central Directory corrupt" in a container.** A truncated runtime-pack download; just retry.

## Packaging

- **`dotnet msbuild -getProperty:InformationalVersion` is empty.** It is computed during the build, not at evaluation. Check the
  built DLL or the About window instead.
- **macOS app blocked on first launch.** Expected for a non-notarized app. Homebrew clears quarantine in the cask's `postflight`;
  manual installs use right-click > Open once.
- **`render.sh: NAME is not set`.** A placeholder had no value: for the cask that means the checksums could not be read from
  `checksums.txt`. Fix the lookup; never substitute a blank.
