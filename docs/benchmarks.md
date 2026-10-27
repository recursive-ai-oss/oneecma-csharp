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

One run on the current engine. Load average 4.1 before, 3.4 after, 3.2-3.5 during (sampled every 20 s); the only other work on the machine was single-core background jobs (a compiler server, a compressor and a few `dotnet` processes at about one core each), so the run was not overlapped by a heavy job. Time per fresh-engine run; ratio is this engine / Jint.

| Row | Jint mean | This engine mean | +/- error | Ratio | Jint alloc | This engine alloc | Peak RSS Jint / ours |
|---|---:|---:|---:|---:|---:|---:|---|
| lodash | 199.2 ms | 123.0 ms | 3.4 ms | **0.62** | 39.4 MB | 36.0 MB | 95 / 98 MB |
| preact | 21.6 ms | 23.3 ms | 0.3 ms | **1.08** | 12.7 MB | 28.8 MB | 73 / 93 MB |
| react-ssr | 55.7 ms | 41.5 ms | 0.2 ms | **0.74** | 17.8 MB | 23.5 MB | 80 / 84 MB |
| startup | 1.65 us | 98.0 us | 1.2 us | 59.5 | 9.7 KB | 272 KB | - |
| fib25 | 31.9 ms | 23.4 ms | 0.3 ms | 0.73 | 42 KB | 0.29 MB | - |
| loops | 64.2 ms | 49.9 ms | 0.3 ms | 0.78 | 89.4 MB | 0.28 MB | - |
| props | 56.7 ms | 46.9 ms | 0.5 ms | 0.83 | 41.7 MB | 0.29 MB | - |
| closures | 38.2 ms | 53.4 ms | 1.5 ms | 1.40 | 5.3 MB | 5.1 MB | - |
| strings | 2.97 ms | 3.71 ms | 0.10 ms | 1.25 | 4.5 MB | 2.9 MB | - |
| vectors | 93.2 ms | 60.8 ms | 0.3 ms | 0.65 | 68.2 MB | 27.8 MB | - |
| calc | 113.1 ms | 75.3 ms | 1.0 ms | 0.67 | 7.0 MB | 0.77 MB | - |
| qsort | 108.2 ms | 76.6 ms | 0.4 ms | 0.71 | 27.9 MB | 2.3 MB | - |
| throws | 34.1 ms | 9.0 ms | 0.2 ms | 0.26 | 14.3 MB | 22.8 MB | - |

**Reading it:**
- Every row except `startup` is inside the 2x floor; the slowest is `closures` at 1.40x, and all three bundle rows are 0.62-1.08x.
- `startup` is engine creation: about 98 us and 272 KB per realm against about 1.6 us for Jint, whose built-ins are created lazily. It matters only to hosts that create many short-lived engines, and it is reported as it is.
- Peak resident memory is within 4-20 MB of Jint on the bundles.
- History: an earlier run on the engine before frame reuse, lazy function prototypes and ropes had `closures` at 2.2x, `strings` at 2.05x, react-ssr at 1.1-1.4x and `startup` at 132x.

## Caveats

- Absolute numbers depend on the machine; the ratios on the same run are what to compare. The CPU's reported frequency scaling was about 70 % during the run.
- Fresh-engine-per-iteration means the first execution of every piece of code is measured, including JIT effects in the host; steady-state throughput of a long-lived engine is better than these rows suggest for both engines.
- The preact row uses a serializer of ours rather than a DOM renderer; the React row uses the legacy synchronous renderer, not the streaming one.
- Eight iterations and two warm-ups can give wide errors on GC-heavy rows.
