#!/bin/sh
# Checks that a published ProxyDog release is complete and consistent. Read-only: it changes nothing.
#   usage: scripts/verify-release.sh <version> [--full]
# --full also downloads every archive and compares it with checksums.txt (about 170 MB).
# Exits non-zero if anything is wrong, so it can gate a release checklist.
set -u

version=${1:?usage: verify-release.sh <version, e.g. 1.2.0> [--full]}
full=${2:-}
repo=babaktaremi/proxydog
tap=babaktaremi/homebrew-tap
tag="v$version"
fail=0

ok()   { printf '  ok    %s\n' "$1"; }
bad()  { printf '  FAIL  %s\n' "$1"; fail=1; }
note() { printf '  note  %s\n' "$1"; }

case "$version" in *-*) stable=0 ;; *) stable=1 ;; esac
work=$(mktemp -d)
trap 'rm -r "$work"' EXIT

echo "Release $tag"
if ! gh release view "$tag" -R "$repo" --json isDraft,isPrerelease,assets > "$work/release.json" 2>/dev/null; then
  bad "release $tag does not exist on $repo"; exit 1
fi
draft=$(gh release view "$tag" -R "$repo" --json isDraft --jq .isDraft)
pre=$(gh release view "$tag" -R "$repo" --json isPrerelease --jq .isPrerelease)
[ "$draft" = "false" ] && ok "published (not a draft)" || bad "still a draft"
if [ "$stable" = 1 ]; then
  [ "$pre" = "false" ] && ok "marked as a stable release" || bad "marked as a prerelease but the version has no -tag"
else
  [ "$pre" = "true" ] && ok "marked as a prerelease" || bad "version has a -tag but is not marked as a prerelease"
fi

echo "Assets"
names=$(gh release view "$tag" -R "$repo" --json assets --jq '.assets[].name')
expected="checksums.txt proxydog-$version-linux-arm64.tar.gz proxydog-$version-linux-x64.tar.gz ProxyDog-$version-macos-arm64.zip ProxyDog-$version-macos-x64.zip"
[ "$stable" = 1 ] && expected="$expected proxydog-bin-$version-1-x86_64.pkg.tar.zst"
for a in $expected; do
  echo "$names" | grep -qx "$a" && ok "$a" || bad "missing asset: $a"
done

gh release download "$tag" -R "$repo" --pattern checksums.txt -D "$work" --clobber >/dev/null 2>&1
if [ ! -s "$work/checksums.txt" ]; then bad "could not read checksums.txt"; exit 1; fi

echo "Release workflow"
run=$(gh run list -R "$repo" --workflow Release --limit 20 --json headBranch,conclusion,status,databaseId \
      --jq "[.[] | select(.headBranch==\"$tag\")][0] | \"\(.status) \(.conclusion) \(.databaseId)\"" 2>/dev/null)
case "$run" in
  "completed success "*) ok "the tag's Release run succeeded (${run##* })" ;;
  "") note "no Release run found for $tag (a dry run is not tied to a tag)" ;;
  *) bad "the tag's Release run is: $run" ;;
esac

if [ "$stable" = 1 ]; then
  echo "Homebrew tap"
  cask=$(gh api "repos/$tap/contents/Casks/proxydog.rb" -H "Accept: application/vnd.github.raw+json" 2>/dev/null || true)
  if [ -z "$cask" ]; then
    bad "no Casks/proxydog.rb in $tap"
  else
    tapver=$(printf '%s\n' "$cask" | sed -n 's/^ *version "\(.*\)"/\1/p' | head -1)
    [ "$tapver" = "$version" ] && ok "cask version is $version" || bad "cask is at $tapver, not $version (run scripts/update-tap.sh $version)"
    for arch in arm64 x64; do
      want=$(grep "ProxyDog-$version-macos-$arch.zip" "$work/checksums.txt" | cut -d' ' -f1)
      printf '%s\n' "$cask" | grep -q "$want" && ok "cask sha256 for $arch matches checksums.txt" || bad "cask sha256 for $arch does not match checksums.txt"
    done
  fi

  echo "Update alert"
  latest=$(gh api "repos/$repo/releases/latest" --jq .tag_name 2>/dev/null || true)
  [ "$latest" = "$tag" ] && ok "GitHub's latest release is $tag, so installed apps will be offered it" \
                         || bad "GitHub's latest release is '$latest', not $tag"
fi

if [ "$full" = "--full" ]; then
  echo "Downloads (full check)"
  for a in $(echo "$expected" | tr ' ' '\n' | grep -v '^checksums.txt$'); do
    gh release download "$tag" -R "$repo" --pattern "$a" -D "$work" --clobber >/dev/null 2>&1
    got=$(shasum -a 256 "$work/$a" 2>/dev/null | cut -d' ' -f1)
    want=$(grep -F "$a" "$work/checksums.txt" | cut -d' ' -f1)
    # the pacman package is attached after checksums.txt is written, so it is not listed there
    if [ -z "$want" ]; then note "$a is not in checksums.txt (expected for the pacman package)"
    elif [ "$got" = "$want" ]; then ok "$a matches its checksum"
    else bad "$a does not match its checksum"; fi
    rm -f "$work/$a"
  done
fi

echo
if [ "$fail" = 0 ]; then echo "Release $tag looks complete."; else echo "Release $tag has problems (see FAIL lines)."; fi
exit "$fail"
