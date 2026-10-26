#!/usr/bin/env bash
# Fetches the pinned Unicode Character Database files into the cache (never into the repo).
# usage: tools/fetch-ucd.sh [cache-dir]   (default: $ONEECMA_CACHE/ucd or ~/oneecma/cache/ucd)
set -euo pipefail
VER=18.0.0
DEST="${1:-${ONEECMA_CACHE:-$HOME/oneecma/cache}/ucd}/$VER"
mkdir -p "$DEST"
while read -r sha file; do
  if [ ! -f "$DEST/$file" ]; then
    curl -fsSL "https://www.unicode.org/Public/$VER/ucd/$file" -o "$DEST/$file"
  fi
  echo "$sha  $DEST/$file" | sha256sum -c --quiet -
done <<SUMS
09c928886a178fcafd93c29e4bd59073a058e5a100b716d425cb563ab50f68c9 DerivedCoreProperties.txt
f438f532e8737bb8a2702126cdf9c4af5e357c58c7acf9d9eb2fc7c1a1d955d6 PropList.txt
0736451de439ae7baf1425136617da495e09ee5afbe6e394374db7009ea08950 UnicodeData.txt
SUMS
echo "$DEST"
