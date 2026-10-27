#!/bin/sh
# Tags a release and pushes the tag, which starts the Release workflow.
#   usage: scripts/release.sh 1.2.3
# Checks first: a clean tree on main, a valid semantic version, and a CHANGELOG section for it.
set -eu
version=${1:?usage: release.sh <version, e.g. 1.2.3 or 1.2.3-rc.1>}

if ! echo "$version" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$'; then
  echo "'$version' is not a semantic version (MAJOR.MINOR.PATCH[-prerelease])" >&2; exit 1
fi
if [ -n "$(git status --porcelain)" ]; then echo "The working tree has uncommitted changes." >&2; exit 1; fi
branch=$(git rev-parse --abbrev-ref HEAD)
if [ "$branch" != "main" ]; then echo "Releases are cut from main, not '$branch'." >&2; exit 1; fi
if git rev-parse -q --verify "refs/tags/v$version" >/dev/null; then echo "Tag v$version already exists." >&2; exit 1; fi
if ! grep -q "^## \[$version\]" CHANGELOG.md; then
  echo "CHANGELOG.md has no '## [$version]' section. Add one before releasing." >&2; exit 1
fi

git fetch origin main --quiet
if [ "$(git rev-parse HEAD)" != "$(git rev-parse origin/main)" ]; then
  echo "main is not in sync with origin/main. Push or pull first." >&2; exit 1
fi

git tag -a "v$version" -m "ProxyDog $version"
git push origin "v$version"
echo "Tagged v$version. Watch it build: gh run watch"
