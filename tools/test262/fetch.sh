#!/usr/bin/env bash
# Downloads exactly the test262 commit named in pin.txt. Nothing is vendored or committed.
# Destination: $ONEECMA_TEST262 if set, else ${ONEECMA_HOME:-$HOME/oneecma}/test262/<sha>.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
sha="$(head -n1 "$here/pin.txt" | tr -d '[:space:]')"
expected="$(sed -n 's/^# archive-sha256: \([0-9a-f]\{64\}\).*/\1/p' "$here/pin.txt")"
dest="${ONEECMA_TEST262:-${ONEECMA_HOME:-$HOME/oneecma}/test262/$sha}"
if [ -f "$dest/.fetched-ok" ]; then echo "test262 $sha already at $dest"; exit 0; fi
work="$(mktemp -d "${TMPDIR:-/tmp}/test262.XXXXXX")"
trap 'rm -rf "$work"' EXIT
curl -fsSL -o "$work/t.tgz" "https://github.com/tc39/test262/archive/$sha.tar.gz"
actual="$(sha256sum "$work/t.tgz" | cut -d' ' -f1)"
if [ -n "$expected" ] && [ "$actual" != "$expected" ]; then
  echo "archive checksum mismatch: expected $expected, got $actual" >&2; exit 1
fi
rm -rf "$dest"; mkdir -p "$dest"
tar xzf "$work/t.tgz" -C "$dest" --strip-components=1
[ -f "$dest/harness/assert.js" ] || { echo "unexpected archive layout" >&2; exit 1; }
touch "$dest/.fetched-ok"
echo "test262 $sha fetched to $dest"
