#!/usr/bin/env bash
# Downloads the pinned third-party scripts the benchmarks run, verifies each archive's SHA-256
# and unpacks it. Nothing is committed: the scripts are published npm packages (all MIT) that
# the benchmarks execute as data.
#
#   lodash 4.18.1        utility-library workload (lodash.js, plus lodash.min.js as minified code)
#   react 19.3.1         server-render workload, together with react-dom/server and a component
#   react-dom 19.3.1     tree of our own (bench/inputs/react-ssr-app.js)
#   preact 11.0.2        the minified page bundle: one dependency-free minified file that runs
#                        without a DOM, unlike a minified jQuery, which needs a document to load
#
# Destination: $ONEECMA_BENCH_INPUTS if set, else ${ONEECMA_HOME:-$HOME/oneecma}/bench-inputs.
set -euo pipefail
dest="${ONEECMA_BENCH_INPUTS:-${ONEECMA_HOME:-$HOME/oneecma}/bench-inputs}"
work="$(mktemp -d "${TMPDIR:-/tmp}/bench-inputs.XXXXXX")"
trap 'rm -rf "$work"' EXIT

# name  version  sha256-of-the-registry-tarball
entries=(
  "lodash 4.18.1 696942c2a488c9d7428e9ca5f12ccf03b52de01dcdbfe4d4751d392f0491ae16"
  "react 19.3.1 56936913918dcf1f7a6fe58784e79df54b18200a88655ce809cf5e9c4ef871aa"
  "react-dom 19.3.1 8b49975821eec6311e00d2b3f4b8337923253c876517388a33f9839c61ab262e"
  "preact 11.0.2 b9dca8d7eb0e758a65fb2f9e41cf284142065b6d6337a3367d15c906a6f892dc"
)

mkdir -p "$dest"
for e in "${entries[@]}"; do
  read -r name version sha <<<"$e"
  target="$dest/$name-$version"
  if [ -f "$target/.fetched-ok" ]; then echo "$name $version already present"; continue; fi
  curl -fsSL -o "$work/$name.tgz" "https://registry.npmjs.org/$name/-/$name-$version.tgz"
  actual="$(sha256sum "$work/$name.tgz" | cut -d' ' -f1)"
  if [ "$actual" != "$sha" ]; then
    echo "$name $version: checksum mismatch (expected $sha, got $actual)" >&2; exit 1
  fi
  rm -rf "$target"; mkdir -p "$target"
  tar xzf "$work/$name.tgz" -C "$target" --strip-components=1
  touch "$target/.fetched-ok"
  echo "$name $version ok ($sha)"
done
