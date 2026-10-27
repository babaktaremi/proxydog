#!/bin/sh
# Builds the connect() hook for the current OS into the directory given as $1 (default: ./out).
set -e
dir=$(cd "$(dirname "$0")" && pwd)
out="${1:-$dir/out}"
mkdir -p "$out"

case "$(uname -s)" in
  Darwin)
    ${CC:-clang} -O2 -Wall -dynamiclib -arch arm64 -arch x86_64 \
      -o "$out/libproxydog_hook.dylib" "$dir/proxydog_hook.c"
    ;;
  Linux)
    ${CC:-cc} -O2 -Wall -fPIC -shared \
      -o "$out/libproxydog_hook.so" "$dir/proxydog_hook.c" -ldl
    ;;
  *)
    echo "Unsupported OS: $(uname -s)" >&2
    exit 1
    ;;
esac
