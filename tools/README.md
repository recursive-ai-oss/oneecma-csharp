# tools

## test262 runner

```
tools/test262/fetch.sh                  # downloads the pinned suite to ~/oneecma/test262/<sha>
tools/node/install.sh                   # installs the pinned Node (reference) under ~/oneecma/ref/
dotnet run --project tools/OneEcma.Test262 -- --backend node --node <path> [filter ...]
dotnet run --project tools/OneEcma.Test262 -- --backend engine --parse-only language/expressions
```

- `tools/test262/pin.txt` pins the suite; `scope.txt` lists test roots, out-of-scope features (reported as skipped, not failed) and supported Intl locales; documented-failures files (`<path> | <reason>`, directory prefixes end in `/`, `*` globs; `documented-node.txt` lists the known gaps of the pinned Node) are passed with `--documented`.
- Backends: `engine` runs the library through its public entry points (anything it cannot do yet counts as not implemented); `node` runs each test in a Node process through `node-host.js`, a black box used to validate the harness and as a reference.
- `--parse-only` runs only parse-phase negative tests plus the parse of every positive test, for backends that can parse without running.
- Output: a per-directory text table, `--json` for the machine-readable summary, `--failures` for one line per failing test.

## bench inputs

`tools/bench-inputs/fetch.sh` downloads the pinned lodash, react, react-dom and preact packages (SHA-256 verified) to `~/oneecma/bench-inputs/`. `bench/inputs/react-ssr-app.js` is the component tree rendered by the server-render benchmark.

The three fetch/install scripts take their destination from `ONEECMA_HOME` (default `$HOME/oneecma`) or a per-script override.

## locale data

`tools/locale-data/fetch.sh` downloads the pinned CLDR 48.0 and UCD 17.0.0 inputs (SHA-256 verified) to `~/oneecma/locale-data/` (override with `ONEECMA_LOCALE_DATA`). `tools/LocaleDataGen` turns them into the embedded blobs under `src/RecursiveAI.OneEcma/Intl/Data/Generated/`; the output is deterministic. Format, pins and how to add a locale: `docs/locale-data.md`.

## text-service differentials

`tools/text-diff/` records the reference Node's `Intl.Collator` and `Intl.Segmenter` output for the committed corpora in `testdata/text/` (`gen-collation.js`, `gen-segmentation.js`; `make-collation-corpus.py` writes the collation corpus). The tests compare the engine's collator and segmenter with those recordings; the conformance tests for normalization, collation and segmentation read the Unicode test files that `tools/locale-data/fetch.sh` downloads. See `docs/text-services.md`.

## RegExp tools

`tools/RegExpUnicodeGen` turns the UCD 17.0.0 files pinned in its `pin.txt` (fetch with `tools/RegExpUnicodeGen/fetch.sh`) into the embedded `regexp-unicode.bin`. `tools/RegExpTest262` (`oneecma-regexp-test262 <test262-root>`) runs the test262 RegExp data tests and regex-literal validation through the engine, and `oneecma-regexp-test262 diff testdata/regexp/expected.json` replays the recorded Node differential (`testdata/regexp/` holds the cases and the recorder). Details: `docs/regexp.md`.

## source map conformance data

`tools/source-map-tests/fetch.sh` downloads the pinned tc39/source-map-tests data (ECMA-426 conformance cases; BSD-3-Clause; only the JSON, `resources/` and `decoding/` are unpacked) to `~/oneecma/source-map-tests/<sha>` (override with `ONEECMA_SOURCE_MAP_TESTS`). Point `ONEECMA_SOURCE_MAP_TESTS` at it and `SourceMapSpecTests` runs them; without it they skip. Details: `docs/source-maps.md`.
