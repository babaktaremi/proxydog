#!/bin/sh
# Fills a packaging template: every @NAME@ is replaced by the environment variable NAME.
#   usage: VERSION=1.2.3 SHA256_MACOS_ARM64=... scripts/render.sh <template> <output>
# Fails if a placeholder has no value, so a release never ships with a blank checksum.
set -eu
template=${1:?usage: render.sh <template> <output>}
output=${2:?output required}

cp "$template" "$output"
for name in $(grep -o '@[A-Z0-9_]*@' "$template" | sort -u | tr -d '@'); do
  value=$(printenv "$name" || true)
  if [ -z "$value" ]; then
    echo "render.sh: $name is not set (needed by $template)" >&2
    exit 1
  fi
  sed -i.bak "s|@$name@|$value|g" "$output"
  rm -f "$output.bak"
done
