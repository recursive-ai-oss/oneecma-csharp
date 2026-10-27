#!/usr/bin/env bash
# Downloads the CLDR and UCD files named in pin.txt, verifies their SHA-256 and unpacks the CLDR archives.
# Nothing is committed. Destination root: $ONEECMA_LOCALE_DATA if set, else ${ONEECMA_HOME:-$HOME/oneecma}/locale-data.
# Layout afterwards: <root>/cldr/48.0/{json,common}/, <root>/ucd/17.0.0/*.txt and <root>/tz/2025b/zone.tab
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
unzip -qo "$root/cldr/48.0/cldr-48.0.0-json-full.zip" -d "$root/cldr/48.0/json"
unzip -qo "$root/cldr/48.0/cldr-common-48.zip" -d "$root/cldr/48.0/common"
tar -xzf "$root/tz/2025b/tzdata2025b.tar.gz" -C "$root/tz/2025b" zone.tab
echo "locale data ready under $root"
