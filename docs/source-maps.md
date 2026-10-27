# Source maps (ECMA-426)

Reports about minified, bundled or transpiled code point at the source the author wrote. The consumer follows ECMA-426 (`https://tc39.es/ecma426/`); this page lists what it does where the specification leaves room, and how reports use it. Types live in `RecursiveAI.OneEcma.SourceMaps`.

## Decoding (`SourceMap.Parse`)

Input is the map text, an optional base URL (where the map was loaded from) and limits. It never throws for bad input: it returns a `SourceMapParseResult` with the `Map` (null when nothing usable could be decoded) and a list of problems, each `{ code, message, path, fatal }`. `ToDiagnostics()` turns problems into warning diagnostics of kind `SourceMap` over the map text, so they render like any report.

- **Standard maps** (section 9) and **index maps** with `sections` (section 10). `sources`, `sourcesContent`, `names`, `sourceRoot`, `file` and `ignoreList` are read as the specification's `GetOptional...` operations; other properties are ignored (9.4).
- **Mappings** are decoded with the VLQ rules of section 6 (values below 2^32 before the sign is applied; continuation digits that add nothing are allowed) and the segment grammar of 9.2.1 (1, 4 or 5 fields; empty groups allowed; an empty segment is not).
- **Sorting.** Mappings are sorted by generated position, ties keep map order (9.1.2). Index-map sections are merged: sources that are equal (URL, content, ignored) become one, names are concatenated, the section offset moves line 0's columns and every line (10.1).
- **Source URLs.** `sourceRoot` is prefixed with a `/` added when missing (9.3.1); the result is resolved against the base URL with `SourceMapUrl.Resolve` (relative references fold `.` and `..`; a reference with a scheme is kept; with no base, the text is kept as written).
- **Optional errors** of the specification are problems that leave the map usable (wrong `version`, a field of the wrong type, a mapping whose source or name index is out of range, which then has no original). **Thrown errors** are fatal problems (not JSON, not an object, `mappings` not a string, `sources` not an array, a malformed index-map section header) and leave `Map` null.

### Decisions where the specification is silent or optional

- **A grammar error in `mappings`** (bad character, wrong field count, VLQ over 32 bits, empty segment) stops decoding at that point and keeps the mappings decoded before it. The specification would return no mappings. The problem is reported either way, `IsValid` is false, and a half-working map still places most errors correctly.
- **`sourceRoot` of `""`** counts as absent (the literal rule would turn `a.js` into `/a.js`).
- **Index map with a top-level `mappings` or `sources`** is reported, and an index-map section that starts at or before the position of the previous section's last mapping is reported as an overlap (the conformance data treats both as invalid).
- **`ignoreList`** entries with no matching source are reported.
- **Limits** (`SourceMapLimits`): 64 million characters of JSON, JSON depth 32, 4 million mappings (the rest are dropped, one problem), 100 000 sources, 10 000 sections, 100 problems recorded. Exceeding size, depth, sources or sections is fatal.

## Lookup (`GetOriginalPositions`, `TryGetOriginalPosition`)

Generated line and column are zero-based, columns in UTF-16 code units. The applying mapping is the last one at or before the position in line-then-column order (12.1) — it may be on an earlier line — and every mapping at exactly that generated position contributes, in map order. Entries without an original source (one-field segments) are omitted, so an unmapped position gives an empty list. A result carries the source, original line and column, the mapping's name and the matched mapping's generated position.

## Finding the map (`SourceMapUrl`, `SourceMapLoader`)

- `SourceMapUrl.Extract` is the "without parsing" extraction of 11.1.2.1, scanning lines from the end: blank lines and comment lines are skipped upward, the first matching `//# sourceMappingURL=` (or `//@`) wins, anything else stops with none, and a comment containing a quote, a backtick or `*/` is ambiguous and gives none.
- `data:` URLs (base64 or percent-encoded UTF-8) are decoded by `SourceMapUrl.TryDecodeDataUrl`.
- `SourceMapLoader.Load` returns null when there is no comment or the map cannot be obtained. Any other URL is resolved against the generated file's name and handed to the host's `SourceMapFetcher`; the engine reads no files and opens no connections. A fetched text starting with `)]}'` loses its first line (11.2.1).

## Reports (`SourceMapReports.Remap`, `RemapAll`)

For each diagnostic the primary span is looked up at its start. No mapping: the diagnostic is returned unchanged.

- **Range.** A mapping is a point, so the range is rebuilt. The start is the mapping's original position, plus the distance inside the mapping when the mapping has no name (code between mappings is assumed copied as written). The end comes from the last code unit of the span the same way; a mapped name gives the token length (the generated identifier may be renamed); with no usable end the span is the name length or the generated length.
- **Original text available** (from `sourcesContent`, or from the host's resolver by URL): the diagnostic is rebuilt over the original `SourceText` named by the source URL. Secondary labels that map into the same source stay labels; those that map elsewhere or nowhere become notes naming the other place or the generated position. Help lines that quote code get " (in the generated code)". The rest of a runtime report is carried over: every stack frame of the generated source moves to the original source (its span and the position `err.stack` reports; locals, async and construct flags travel with it; a frame with no mapping, a host frame and a frame of another source stay as they are), provenance steps move like secondary labels (a step that maps elsewhere or nowhere becomes a note), and the did-you-mean suggestions, which hold no positions, are kept. Suggested fixes and recovery steps are dropped. A note `generated position: file:line:col` is added and `Diagnostic.Mapping` records both places (JSON `mapping`).
- **Original text not available:** the diagnostic keeps the generated source, with its stack, provenance and suggestions unchanged, and gains a note `original position: file:line:col (the original source text is not available)`, and `Mapping.SpansAreOriginal` is false.

## CLI

`oneecma check <file>` follows a trailing `data:` source map comment, or a map file next to the script named by the comment, only when there is something to report; `--source-map <file>` names the map explicitly. Original sources missing from `sourcesContent` are read from the path the map names. A map that cannot be used adds warnings to the report and never changes the exit code. Repaired source (`--repair`) is not remapped.

## Tests

`tools/source-map-tests/fetch.sh` fetches the pinned `tc39/source-map-tests` data (BSD-3-Clause, never committed); set `ONEECMA_SOURCE_MAP_TESTS` to the directory and `SourceMapSpecTests` runs every validity case, every mapping, ignore-list and transitive-mapping action and the decoding goldens. Without it those tests skip. Our own cases are in `SourceMapTests` and `testdata/golden/map-001-*`; the decoder has a fuzz target (`oneecma-fuzz sourcemap`).
