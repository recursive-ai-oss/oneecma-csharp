#!/usr/bin/env bash
# Fetches the pinned Unicode Character Database files into the cache (never into the repo).
# usage: tools/fetch-ucd.sh [cache-dir]   (default: $ONEECMA_CACHE/ucd or ~/oneecma/cache/ucd)
set -euo pipefail
# 17.0.0 is the version test262 (property-escape tests) and the reference Node v24 use; the locale data
# generator pins the same files. All tables move together when the pinned suite moves.
VER=17.0.0
DEST="${1:-${ONEECMA_CACHE:-$HOME/oneecma/cache}/ucd}/$VER"
mkdir -p "$DEST"
while read -r sha file; do
  if [ ! -f "$DEST/$file" ]; then
    curl -fsSL "https://www.unicode.org/Public/$VER/ucd/$file" -o "$DEST/$file"
  fi
  echo "$sha  $DEST/$file" | sha256sum -c --quiet -
done <<SUMS
24c7fed1195c482faaefd5c1e7eb821c5ee1fb6de07ecdbaa64b56a99da22c08 DerivedCoreProperties.txt
130dcddcaadaf071008bdfce1e7743e04fdfbc910886f017d9f9ac931d8c64dd PropList.txt
2e1efc1dcb59c575eedf5ccae60f95229f706ee6d031835247d843c11d96470c UnicodeData.txt
SUMS
echo "$DEST"
