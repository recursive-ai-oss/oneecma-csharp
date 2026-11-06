# Changelog

All notable changes are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- **`ScriptEngine.StripTypes`** (with `TypeStripOptions` and `TypeStripResult`) turns TypeScript that uses only erasable syntax into JavaScript. Erased text becomes spaces and every line break and other character stays in place, so a line and column in the result is the same in the `.ts` source and reports need no source map. It erases annotations, type parameters and arguments, `as`/`as const`/`satisfies`/`!`, `interface`, `type` and `declare` declarations, overload signatures, index signatures, `implements`, access modifiers, `abstract` members, and `import type`/`export type`/inline `type` specifiers (an `import { type A }` keeps its side-effect import). `enum`, namespaces with values, constructor parameter properties, `import x = require()`, `export =`, `<T>x` casts and parameter decorators are refused, each with a report that names the construct and suggests the plain-JavaScript form. See `docs/typescript.md`.

### Security

- **Four more ways past the sandbox limits are closed.**
  - **Symbol registry:** `Symbol.for` charges each new registry entry (the key and the symbol) to the allocation budget before inserting it.
  - **Job queue:** every queued job and every promise reaction, with the result capability it keeps alive, is charged when it is made and given back when it runs; promises are charged when created. A loop such as `for (;;) Promise.resolve().then(() => {})` now ends in a memory-limit report.
  - **BigInt:** multiplication, division and remainder, `**`, shifts, bitwise operators, `toString(radix)`, `toLocaleString`, `BigInt(string)`, `parseInt` of long digit strings, `asIntN`/`asUintN` and BigInt-to-Number conversion now charge fuel in proportion to operand size before they run, and charge their results to the allocation budget. An operation that cannot fit in the remaining fuel never starts. Multiplication and `**` run in blocks and poll cancellation and `WallClockMs` between them, so a squaring loop ends within a fraction of a second of the wall-clock limit; division and conversion to digits of small numbers cannot be interrupted, so one of them above about 1.5 seconds of estimated work ends the run with a step-limit report instead.
  - **Closures and environments:** script and bound function objects (including classes and the lazily created `prototype` object), environment records, call frames that are not reused (arrow functions, generators and async functions keep theirs), arguments objects, generator objects, error objects with their captured stack, `WeakRef`, `Proxy`, RegExp compilation of script-built patterns and Intl objects are charged at estimates of their real size. A closure-heavy loop stops within about 1.5 times `MaxAllocatedBytes`.
  - Code that makes many short-lived closures, errors or promises now spends the allocation budget faster than before, since the budget counts bytes requested, not bytes alive; see `docs/runtime.md` for the estimates.

### Documentation

- Corrected the 1.0.0 description of limits: fuel, memory and wall clock end the run with a report like any other error; call depth and string length throw a `RangeError` the script can catch (reported like any other error if it is not caught); cancellation works through a `CancellationToken`, and the call throws `OperationCanceledException` with no report.
- The README's `[JsExport]` example now sets up the host in `ScriptEngineOptions.Configure`, so the replay that explains a failure sees the same globals; `docs/js-export.md` shows top-level frames as `<top-level>` and notes that a `DefineAsync` handler's `JsonElement` is valid only until its first `await`.

## [1.0.1] - 2026-11-05

### Security

- **The allocation limit now bounds memory.** `EngineLimits.MaxAllocatedBytes` did not count array element storage, so scripts such as `new Array(1e9).fill(0)`, `Array.from({ length: 1e9 })` or an unbounded `push` loop could take gigabytes, or fail with a .NET `OutOfMemoryException`, before the limit fired. Growth that a script controls is now charged before the allocation happens, so one huge request ends in the usual memory-limit report: array storage (growth, `Array(n)`, `length`, `push`, `splice`, spread, rest, `concat`, `slice`, `flat` and the copying methods), typed-array and `ArrayBuffer` creation, resize, copy and sort, iterator results gathered into lists, `FinalizationRegistry` registrations, string building (`join` and `JSON.stringify` while they grow), and the property storage of objects that grow past 32 properties. An allocation .NET cannot satisfy is a `RangeError` or a memory-limit report, never an `OutOfMemoryException`. The limit still counts bytes requested, not bytes alive; see `docs/runtime.md`.

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

[Unreleased]: https://github.com/recursive-ai-oss/oneecma-csharp/compare/v1.0.1...HEAD
[1.0.1]: https://github.com/recursive-ai-oss/oneecma-csharp/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/recursive-ai-oss/oneecma-csharp/releases/tag/v1.0.0
