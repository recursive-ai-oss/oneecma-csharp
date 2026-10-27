#!/usr/bin/env bash
# Downloads the UCD files named in pin.txt and verifies their SHA-256. Nothing is committed.
# Destination root: $ONEECMA_LOCALE_DATA if set, else ${ONEECMA_HOME:-$HOME/oneecma}/locale-data; files land in <root>/ucd/17.0.0/.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="${ONEECMA_LOCALE_DATA:-${ONEECMA_HOME:-$HOME/oneecma}/locale-data}"
grep -v '^#' "$here/pin.txt" | while read -r sum rel url; do
  [ -n "$sum" ] || continue
  dest="$root/$rel"
  if [ -f "$dest" ] && [ "$(sha256sum "$dest" | cut -d' ' -f1)" = "$sum" ]; then continue; fi
  mkdir -p "$(dirname "$dest")"
  curl -fsSL -o "$dest.part" "$url"
  actual="$(sha256sum "$dest.part" | cut -d' ' -f1)"
  if [ "$actual" != "$sum" ]; then rm -f "$dest.part"; echo "checksum mismatch for $rel: expected $sum, got $actual" >&2; exit 1; fi
  mv "$dest.part" "$dest"
  echo "fetched $rel"
done
echo "UCD files ready under $root/ucd/17.0.0"
