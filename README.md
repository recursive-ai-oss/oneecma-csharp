# OneEcma

An ECMAScript interpreter for .NET (`net10.0`, NativeAOT- and trim-compatible, no dependencies beyond the BCL) whose product is its error reports: when a script fails it says what went wrong, where the failing value came from and how to fix it, while `err.message` and `err.stack` stay exactly what V8 prints.

**Status: under construction.** The solution builds and the engine is a placeholder; nothing runs scripts yet.

## Build

```sh
dotnet build OneEcma.slnx
dotnet test --solution OneEcma.slnx
```

Build output goes to `artifacts/` (ignored). Set `ONEECMA_ARTIFACTS` to redirect it elsewhere, for example when building several worktrees side by side.

The NativeAOT/trim check publishes the smoke app and fails on any warning:

```sh
dotnet publish tests/OneEcma.AotSmoke -c Release -r linux-x64   # use your RID
```

## Layout

| Path | Contents |
|---|---|
| `src/RecursiveAI.OneEcma` | the engine |
| `src/RecursiveAI.OneEcma.Generators` | Roslyn source generator for host bindings (analyzer-only package) |
| `src/OneEcma.Cli` | the `oneecma` command-line tool |
| `tests/` | unit tests and the NativeAOT smoke app |
| `fuzz/`, `bench/` | fuzz harness and BenchmarkDotNet suite (Jint is referenced only in `bench/`, as a comparison target) |
| `docs/` | normative specifications of implementation-defined behavior |

The package prefix lives in one property, `PackagePrefix` in `Directory.Build.props`.

## Licence

`MIT OR Apache-2.0`, Copyright (c) Recursive AI, LLC. See `LICENSE-MIT`, `LICENSE-APACHE` and `THIRD-PARTY-NOTICES.md`.
