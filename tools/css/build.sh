#!/bin/sh
# Rebuilds src/ResolveAI.App/wwwroot/app.css with the pinned Tailwind standalone CLI, checking its published checksum.
# The generated file is committed, so running the app needs nothing beyond the .NET SDK. Run this after changing classes.
set -eu
VERSION=4.3.3
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
CACHE="$ROOT/tools/css/.cache/$VERSION"
case "$(uname -s)-$(uname -m)" in
  MINGW*|MSYS*|CYGWIN*) ASSET=tailwindcss-windows-x64.exe ;;
  Linux-x86_64) ASSET=tailwindcss-linux-x64 ;;
  Linux-aarch64) ASSET=tailwindcss-linux-arm64 ;;
  Darwin-arm64) ASSET=tailwindcss-macos-arm64 ;;
  Darwin-x86_64) ASSET=tailwindcss-macos-x64 ;;
  *) echo "Unsupported platform" >&2; exit 1 ;;
esac
BASE="https://github.com/tailwindlabs/tailwindcss/releases/download/v$VERSION"
mkdir -p "$CACHE"
if [ ! -x "$CACHE/$ASSET" ]; then
  curl -fsSL -o "$CACHE/$ASSET.tmp" "$BASE/$ASSET"
  curl -fsSL -o "$CACHE/sha256sums.txt" "$BASE/sha256sums.txt"
  EXPECTED=$(grep " \*\{0,1\}\./\{0,1\}$ASSET\$" "$CACHE/sha256sums.txt" | cut -d' ' -f1)
  ACTUAL=$(sha256sum "$CACHE/$ASSET.tmp" | cut -d' ' -f1)
  [ -n "$EXPECTED" ] && [ "$EXPECTED" = "$ACTUAL" ] || { echo "Checksum mismatch for $ASSET" >&2; exit 1; }
  mv "$CACHE/$ASSET.tmp" "$CACHE/$ASSET"
  chmod +x "$CACHE/$ASSET"
fi
cd "$ROOT/src/ResolveAI.App"
"$CACHE/$ASSET" -i Styles/app.css -o wwwroot/app.css --minify
