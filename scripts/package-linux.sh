#!/bin/sh
# Builds a self-contained Linux tarball that distro packages (pacman, snap) install from.
#   usage: scripts/package-linux.sh <linux-x64|linux-arm64> <version> [outdir]
# Run it on a Linux machine of the same architecture: the LD_PRELOAD hook is compiled with the local C compiler.
set -eu

rid=${1:?usage: package-linux.sh <linux-x64|linux-arm64> <version> [outdir]}
version=${2:?version required}
out=${3:-dist}
arch=${rid#linux-}

root=$(cd "$(dirname "$0")/.." && pwd)
work="$root/obj/package/$rid"
stage="$work/proxydog-$version-linux-$arch"
mkdir -p "$out"
out=$(cd "$out" && pwd)

if [ -d "$work" ]; then rm -r "$work"; fi
mkdir -p "$stage/proxydog"

dotnet publish "$root/ProxyDog.App/ProxyDog.App.csproj" -c Release -r "$rid" --self-contained true \
  -p:Version="$version" -p:DebugType=none -o "$work/publish"

cp -R "$work/publish/." "$stage/proxydog/"
chmod +x "$stage/proxydog/ProxyDog"
cp "$root/packaging/linux/proxydog.desktop" "$root/packaging/linux/proxydog.png" "$stage/"
cp "$root/LICENSE" "$root/NOTICE" "$root/README.md" "$stage/"

tarball="$out/proxydog-$version-linux-$arch.tar.gz"
tar -C "$work" -czf "$tarball" "proxydog-$version-linux-$arch"
echo "$tarball"
