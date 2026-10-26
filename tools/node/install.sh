#!/usr/bin/env bash
# Installs the pinned Node release user-locally from the official tarball, after verifying its
# SHA-256, under ${ONEECMA_HOME:-$HOME/oneecma}/ref/node-<version>/. Node is a black-box
# reference for conformance and differential runs, never a dependency. Run with the path it
# prints: <dest>/bin/node.
set -euo pipefail
version="v24.22.1"   # Node 24 LTS ("Krypton"), V8 14.x, full ICU
archive="node-$version-linux-x64.tar.gz"
sha256="eb6728e77c576a35162c122c45ccadf224d89ff00abc8dae46cc2897c6002d7c"   # from nodejs.org/dist/$version/SHASUMS256.txt
dest="${ONEECMA_NODE_DEST:-${ONEECMA_HOME:-$HOME/oneecma}/ref/node-$version}"
if [ -x "$dest/bin/node" ]; then echo "$dest/bin/node"; exit 0; fi
work="$(mktemp -d "${TMPDIR:-/tmp}/node-install.XXXXXX")"
trap 'rm -rf "$work"' EXIT
curl -fsSL -o "$work/$archive" "https://nodejs.org/dist/$version/$archive"
actual="$(sha256sum "$work/$archive" | cut -d' ' -f1)"
if [ "$actual" != "$sha256" ]; then
  echo "checksum mismatch: expected $sha256, got $actual" >&2; exit 1
fi
rm -rf "$dest"; mkdir -p "$dest"
tar xzf "$work/$archive" -C "$dest" --strip-components=1
"$dest/bin/node" --version >&2
echo "$dest/bin/node"
