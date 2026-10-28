#!/bin/sh
# Updates the Homebrew cask in babaktaremi/homebrew-tap from a published release's checksums.
#   usage: scripts/update-tap.sh <version> [--dry-run]
# The Release workflow does this itself once the TAP_DEPLOY_KEY secret exists. Until then this is the manual step
# that follows every stable release. --dry-run shows the change without pushing anything.
set -eu

version=${1:?usage: update-tap.sh <version> [--dry-run]}
dry=${2:-}
repo=babaktaremi/proxydog
tap=babaktaremi/homebrew-tap
tag="v$version"
root=$(cd "$(dirname "$0")/.." && pwd)

case "$version" in
  *-*) echo "$version is a prerelease; the tap only carries stable releases." >&2; exit 1 ;;
esac

work=$(mktemp -d)
trap 'rm -r "$work"' EXIT

gh release download "$tag" -R "$repo" --pattern checksums.txt -D "$work"
VERSION=$version
SHA256_MACOS_ARM64=$(grep "ProxyDog-$version-macos-arm64.zip" "$work/checksums.txt" | cut -d' ' -f1)
SHA256_MACOS_X64=$(grep "ProxyDog-$version-macos-x64.zip" "$work/checksums.txt" | cut -d' ' -f1)
export VERSION SHA256_MACOS_ARM64 SHA256_MACOS_X64   # render.sh refuses to run with any of these empty
"$root/scripts/render.sh" "$root/packaging/homebrew/proxydog.rb.in" "$work/proxydog.rb"
ruby -c "$work/proxydog.rb" >/dev/null

git clone --quiet "git@github.com:$tap.git" "$work/tap"
mkdir -p "$work/tap/Casks"
if cmp -s "$work/proxydog.rb" "$work/tap/Casks/proxydog.rb"; then
  echo "The cask is already at $version. Nothing to do."
  exit 0
fi

diff -u "$work/tap/Casks/proxydog.rb" "$work/proxydog.rb" || true
if [ "$dry" = "--dry-run" ]; then
  echo "(dry run: nothing was pushed)"
  exit 0
fi

cp "$work/proxydog.rb" "$work/tap/Casks/proxydog.rb"
cd "$work/tap"
git add Casks/proxydog.rb
git -c user.name="$(git config --global user.name || echo 'proxydog release')" \
    -c user.email="$(git config --global user.email || echo 'noreply@github.com')" \
    commit --quiet -m "proxydog $version"
git push --quiet
echo "Pushed proxydog $version to $tap."
