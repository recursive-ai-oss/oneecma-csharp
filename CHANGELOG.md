# Changelog

All notable changes are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [1.0.0] - 2026-10-27

First release. OneEcma is an interpreter for .NET implementing ECMA-262 / ECMAScript®, whose product is its error reports. It is not affiliated with or endorsed by Ecma International.

### Added

- **Language and built-ins:** the ES2026 language (ECMA-262, 17th edition) in strict and sloppy mode, with Annex B, modules and dynamic import, generators, async functions and async generators, classes with private members, BigInt, `Proxy` and `Reflect`, typed arrays, `Atomics` (single agent), `Date` and iterator helpers. Pass rates on the pinned test262 are in the README; every failing test is documented with its reason. Temporal is a later edition and is not included.
- **RegExp:** our own backtracking engine with the `u` and `v` flags, lookbehind, named groups and Unicode properties, charged to the fuel limit at every backtracking step.
- **Intl (ECMA-402)** for `en-US`, `en-GB`, `es` (including `es-US`), `fr`, `de` and `pt-BR`, Gregorian calendar and Latin digits, generated from CLDR 48.0 and Unicode 17.0.0. Unsupported locales fall back to the default locale.
- **Source maps (ECMA-426):** reports about minified or generated code point at the original source.
- **Error reports:** rustc-style text reports and versioned JSON reports. The headline, `err.message` and `err.stack` are V8's wording and format. A run that fails is replayed with tracing to add where the failing value came from, the locals of each frame, "did you mean" suggestions drawn from what the object or scope actually holds, and async stacks stitched across `await`. A successful run pays nothing for this. A compact rendering is available for readers with limited context.
- **Record and replay:** every source of nondeterminism (clock, randomness, host-call results, job order, weak-reference observations) enters through `IJsHost` and is recorded, so a replay produces the same values, exceptions and report bytes.
- **Auto-repair:** off by default and syntax-only. A fix is applied only when it is the single plausible one and the result parses; fixes that could change meaning are suggestions. Applied repairs are listed first in the report and the repaired source is returned. See `docs/repair-table.md`.
- **Host API:** a low-level public API (`JsValue`, `JsObject`, `JsRealm`, callbacks, promises, modules, the job queue), the `[JsExport]` source generator for build-time marshalling that names the .NET member, argument path and types in boundary errors, and `JsonSchemaTools` for registering host functions from a JSON Schema. Exceptions cross in both directions with interleaved script and .NET frames.
- **Limits:** fuel, memory, wall clock, call depth, string length and cancellation, each ending in a report like any other error. The engine has no file, network, process or environment access of its own.
- **Command line:** `oneecma run`, `check` (every syntax and early error, optional `--repair`) and `repl`, with `--format json`, `--compact` and `--locals`.
- **Packages:** `RecursiveAI.OneEcma` (the engine, Intl included, no dependencies beyond the .NET base class library), `RecursiveAI.OneEcma.Generators` (analyzer-only) and `RecursiveAI.OneEcma.Cli` (the `oneecma` dotnet tool). NativeAOT binaries of `oneecma` for win-x64, linux-x64, linux-arm64 and osx-arm64 are attached to the GitHub release.

[Unreleased]: https://github.com/recursive-ai-oss/oneecma-csharp/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/recursive-ai-oss/oneecma-csharp/releases/tag/v1.0.0
