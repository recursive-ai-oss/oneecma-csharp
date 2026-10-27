#!/usr/bin/env bash
# Downloads the tc39/source-map-tests commit named in pin.txt (ECMA-426 conformance data). Nothing is vendored or committed.
# Destination: $ONEECMA_SOURCE_MAP_TESTS if set, else ${ONEECMA_HOME:-$HOME/oneecma}/source-map-tests/<sha>.
# The unit tests read the directory named by ONEECMA_SOURCE_MAP_TESTS and skip when it is not set.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
sha="$(head -n1 "$here/pin.txt" | tr -d '[:space:]')"
expected="$(sed -n 's/^# archive-sha256: \([0-9a-f]\{64\}\).*/\1/p' "$here/pin.txt")"
dest="${ONEECMA_SOURCE_MAP_TESTS:-${ONEECMA_HOME:-$HOME/oneecma}/source-map-tests/$sha}"
if [ -f "$dest/.fetched-ok" ]; then echo "source-map-tests $sha already at $dest"; exit 0; fi
work="$(mktemp -d "${TMPDIR:-/tmp}/smt.XXXXXX")"
trap 'rm -rf "$work"' EXIT
curl -fsSL -o "$work/t.tgz" "https://github.com/tc39/source-map-tests/archive/$sha.tar.gz"
actual="$(sha256sum "$work/t.tgz" | cut -d' ' -f1)"
if [ -n "$expected" ] && [ "$actual" != "$expected" ]; then
  echo "archive checksum mismatch: expected $expected, got $actual" >&2; exit 1
fi
rm -rf "$dest"; mkdir -p "$dest"
tar xzf "$work/t.tgz" -C "$dest" --strip-components=1 --wildcards '*/source-map-spec-tests.json' '*/resources' '*/decoding' '*/LICENSE.md'
[ -f "$dest/source-map-spec-tests.json" ] || { echo "unexpected archive layout" >&2; exit 1; }
touch "$dest/.fetched-ok"
echo "source-map-tests $sha fetched to $dest"
