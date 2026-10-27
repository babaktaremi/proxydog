#!/bin/sh
# Builds ProxyDog.app for one macOS architecture and zips it.
#   usage: scripts/package-macos.sh <osx-arm64|osx-x64> <version> [outdir]
# The app is ad-hoc signed. It is not notarized (that needs a paid Apple Developer ID), so the Homebrew cask
# removes the quarantine flag, and people installing by hand use right-click > Open once.
set -eu

rid=${1:?usage: package-macos.sh <osx-arm64|osx-x64> <version> [outdir]}
version=${2:?version required}
out=${3:-dist}
arch=${rid#osx-}

root=$(cd "$(dirname "$0")/.." && pwd)
work="$root/obj/package/$rid"
app="$work/ProxyDog.app"
mkdir -p "$out"
out=$(cd "$out" && pwd)

# Wipe only the scratch folder this script owns.
if [ -d "$work" ]; then rm -r "$work"; fi
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"

dotnet publish "$root/ProxyDog.App/ProxyDog.App.csproj" -c Release -r "$rid" --self-contained true \
  -p:Version="$version" -p:DebugType=none -o "$work/publish"

cp -R "$work/publish/." "$app/Contents/MacOS/"
cp "$root/ProxyDog.App/Branding/AppIcon.icns" "$app/Contents/Resources/AppIcon.icns"

# CFBundleVersion must be dotted numbers only, so drop any -prerelease tag there.
numeric=${version%%-*}
cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>ProxyDog</string>
  <key>CFBundleDisplayName</key><string>ProxyDog</string>
  <key>CFBundleIdentifier</key><string>dev.babaktaremi.proxydog</string>
  <key>CFBundleExecutable</key><string>ProxyDog</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundleVersion</key><string>$numeric</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.developer-tools</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSHumanReadableCopyright</key><string>Copyright 2026 Babak Taremi. Apache-2.0 license.</string>
</dict>
</plist>
PLIST

chmod +x "$app/Contents/MacOS/ProxyDog"
codesign --force --deep --sign - "$app"
codesign --verify --deep --strict "$app"

zip="$out/ProxyDog-$version-macos-$arch.zip"
if [ -f "$zip" ]; then rm "$zip"; fi
(cd "$work" && ditto -c -k --keepParent ProxyDog.app "$zip")
echo "$zip"
