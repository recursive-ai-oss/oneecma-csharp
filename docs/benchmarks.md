# Benchmarks

Throughput is not the product, but it has a floor: real page scripts must stay usable. The floor is roughly **at most 2× Jint's time** on a small suite. This page records how the suite is run and the latest numbers.

## What is measured

Every row evaluates one plain script on a **fresh engine** per iteration, on this engine and on Jint, so realm creation, parsing, compilation and execution are all inside the number. Rows:

| Row | Script |
|---|---|
| `startup` | `1 + 1`: the cost of creating an engine |
| `fib25`, `loops`, `props`, `closures`, `strings`, `vectors`, `calc`, `qsort`, `throws` | small kernels (`BenchScripts.cs`) |
| `lodash` | lodash 4.18.1 (`lodash.js`, unminified) loaded, then a utility workload: `groupBy`/`mapValues`/`orderBy`, `uniq`/`flatMap`, `chunk`, `camelCase`/`kebabCase`/`startCase`, `cloneDeep`, `merge`, `isEqual`, `template` (compiled with the `Function` constructor) over 1000 records; the result is the JSON of everything computed |
| `preact` | preact 11.0.2 `dist/preact.mjs` (the minified bundle; its trailing `export{…}` list becomes an object so a plain script can run it). The workload builds a 300-row table with `h`, a class component and function components, `cloneElement` and `toChildArray`, and serializes it to HTML with a small serializer of ours. preact has no string renderer in the core package and its DOM renderer needs a `document`, so the real library is exercised without one |
| `react-ssr` | react 19.3.1 and react-dom 19.3.1 production builds (`react.production.js`, `react-dom.production.js`, `react-dom-server-legacy.browser.production.js`) behind a three-line CommonJS loader, then `bench/inputs/react-ssr-app.js`: `renderToString` of a 300-row product table, plus a second `renderToStaticMarkup` of a 40-row table. Two renders, the full page and a fragment, is what a server does for one request with a partial; the legacy browser build is the synchronous string renderer |

Why a minified-bundle row on preact: jQuery needs a DOM to load, and preact is one dependency-free minified file.

**Correctness first.** The bundle rows return a string. Before a row is timed, the same script runs in the pinned Node (v24.22.1, in a fresh `vm` context, so no `require`/`process`/timers leak in) and both engines must produce the **same string byte for byte**: 1968 characters of JSON for lodash, 56,424 characters of HTML for preact and 66,963 for React. A mismatch stops the row. `OneEcma.Benchmarks verify` runs that check for every row without timing.

## Method

- BenchmarkDotNet 0.15.8, `[SimpleJob(warmupCount: 2, iterationCount: 8)]`, `[MemoryDiagnoser]`, Release build, default launch (one process per row and engine).
- Jint 4.17.0 is the baseline (`Ratio` = this engine's mean ÷ Jint's mean). Jint is referenced only by the benchmark project.
- Peak resident memory: one fresh-engine run of the row per process under `/usr/bin/time -v` (`OneEcma.Benchmarks once <engine> <row>`).
- Inputs are fetched by `tools/bench-inputs/fetch.sh` at pinned versions with their SHA-256 and never committed. `tools/bench-inputs/run-suite.sh` runs the whole suite and the memory pass and samples the load average and the busiest processes every 20 s into a log.
- The machine is shared. A timing run takes an exclusive benchmark lock on it, checked atomically at the start of the job, and is repeated if the load log shows an overlapping heavy job. The lock stops other benchmarks, not other people's small jobs, so the load log is part of the record.

Machine: AMD Ryzen 9 7950X3D (16 cores, 32 threads), Linux x64, .NET SDK 10.0.402 / runtime 10.0.13, Node v24.22.1.

## Results

Second of two runs (quieter; load average 6.0 → 2.1, only single-core background jobs such as a compiler server and a compressor beside it). Time per fresh-engine run; ratio is this engine ÷ Jint.

| Row | Jint mean | This engine mean | ± error | Ratio | Jint alloc | This engine alloc | Peak RSS Jint / ours |
|---|---:|---:|---:|---:|---:|---:|---|
| lodash | 189.7 ms | 162.1 ms | 3.9 ms | **0.85** | 39.4 MB | 298.7 MB | 93 / 122 MB |
| preact | 20.7 ms | 24.4 ms | 0.3 ms | **1.18** | 12.7 MB | 38.2 MB | 73 / 103 MB |
| react-ssr | 53.4 ms | 60.5 ms | 13.4 ms | **1.13** | 17.8 MB | 78.6 MB | 80 / 115 MB |
| startup | 1.55 µs | 204 µs | 5.5 µs | 132 | 9.7 KB | 741 KB | — |
| fib25 | 29.8 ms | 40.1 ms | 2.2 ms | 1.34 | 42 KB | 126.7 MB | — |
| loops | 61.5 ms | 79.1 ms | 1.3 ms | 1.29 | 89.4 MB | 0.74 MB | — |
| props | 53.8 ms | 51.7 ms | 0.3 ms | 0.96 | 41.7 MB | 0.75 MB | — |
| closures | 38.6 ms | 85.7 ms | 2.1 ms | **2.22** | 5.3 MB | 138.5 MB | — |
| strings | 2.80 ms | 5.75 ms | 0.02 ms | **2.05** | 4.5 MB | 4.4 MB | — |
| vectors | 86.3 ms | 87.6 ms | 1.5 ms | 1.02 | 68.2 MB | 226.6 MB | — |
| calc | 113.4 ms | 119.0 ms | 3.7 ms | 1.05 | 7.0 MB | 283.6 MB | — |
| qsort | 103.7 ms | 92.1 ms | 6.2 ms | 0.89 | 27.9 MB | 21.4 MB | — |
| throws | 32.4 ms | 9.1 ms | 0.02 ms | 0.28 | 14.3 MB | 26.3 MB | — |

First run (a compiler server and an unrelated test run, up to five cores, overlapped for about a minute): the same rows within a few percent, except react-ssr at 78.3 ms (1.40×, ± 16 ms), which is the noisiest row in both runs: its error is large because of garbage-collection pauses, and the 1.13–1.40 spread is the honest range.

**Reading it:**
- All three bundle rows are inside the floor (0.85–1.40× Jint).
- Rows above 2× are two kernels, `closures` (2.2×) and `strings` (2.05×), and `startup`.
- `startup` is engine creation: about 204 µs and 741 KB per realm, against about 1.6 µs for Jint. It matters only to hosts that create many short-lived engines. Realm intrinsics are built eagerly; lazy intrinsics are the fix.
- This engine allocates far more than Jint on call-heavy rows (a frame per call: `fib25`, `calc`, `closures`) and on the bundles (3–7×); peak RSS is about 30 MB higher on the bundles.

## Caveats

- Absolute numbers depend on the machine; the ratios on the same run are what to compare. The CPU's reported frequency scaling was 63–72 % during both runs.
- Fresh-engine-per-iteration means the first execution of every piece of code is measured, including JIT effects in the host; steady-state throughput of a long-lived engine is better than these rows suggest for both engines.
- The preact row uses a serializer of ours rather than a DOM renderer; the React row uses the legacy synchronous renderer, not the streaming one.
- Eight iterations and two warm-ups give wide errors on GC-heavy rows (`react-ssr`).
