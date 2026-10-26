# tools

## test262 runner

```
tools/test262/fetch.sh                  # downloads the pinned suite to ~/oneecma/test262/<sha>
tools/node/install.sh                   # installs the pinned Node (reference) under ~/oneecma/ref/
dotnet run --project tools/OneEcma.Test262 -- --backend node --node <path> [filter ...]
dotnet run --project tools/OneEcma.Test262 -- --backend engine --parse-only language/expressions
```

- `tools/test262/pin.txt` pins the suite; `scope.txt` lists test roots, out-of-scope features (reported as skipped, not failed) and supported Intl locales; a documented-failures file (`<path> | <reason>`, directory prefixes end in `/`, `*` globs) is passed with `--documented`.
- Backends: `engine` runs the library through its public entry points (anything it cannot do yet counts as not implemented); `node` runs each test in a Node process through `node-host.js`, a black box used to validate the harness and as a reference.
- `--parse-only` runs only parse-phase negative tests plus the parse of every positive test, for backends that can parse without running.
- Output: a per-directory text table, `--json` for the machine-readable summary, `--failures` for one line per failing test.

## bench inputs

`tools/bench-inputs/fetch.sh` downloads the pinned lodash, react, react-dom and preact packages (SHA-256 verified) to `~/oneecma/bench-inputs/`. `bench/inputs/react-ssr-app.js` is the component tree rendered by the server-render benchmark.

The three fetch/install scripts take their destination from `ONEECMA_HOME` (default `$HOME/oneecma`) or a per-script override.
