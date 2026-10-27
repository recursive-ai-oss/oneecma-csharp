# OneEcma

**An ECMAScript interpreter for .NET whose product is its error reports.** When a script fails, OneEcma says what went wrong, where the failing value came from and how to fix it, in a compact report that fits an LLM's context. `err.message` and `err.stack` stay exactly what V8 prints, so code that parses them keeps working. Pure C# on `net10.0` with no dependency beyond the .NET base class library; NativeAOT- and trim-compatible; every script is treated as hostile and runs under limits.

The package name `RecursiveAI.OneEcma` is provisional. The prefix lives in one property, `PackagePrefix` in `Directory.Build.props`.

## A failing script and its report

`checkout.js`:

```js
async function loadOrder(id) {
  return { id, lineItems: [{ price: 4 }, { price: 6 }], total: 10 };
}

async function main() {
  const order = await loadOrder(7);
  const total = order.items.reduce((sum, i) => sum + i.price, 0);
  return total;
}

main();
```

`oneecma run checkout.js` exits with status 1 and prints this on stderr:

```
error[TypeError]: Cannot read properties of undefined (reading 'reduce')
 --> checkout.js:7:23
  |
6 |   const order = await loadOrder(7);
  |                 ------------------ `order` comes from here: { id, lineItems, total }
7 |   const total = order.items.reduce((sum, i) => sum + i.price, 0);
  |                       ^^^^^ `order.items` is undefined
  |
  = help: did you mean `order.lineItems`? (Array, 2 items)
  = stack: main (checkout.js:7)
```

The headline is V8's wording. The labelled spans, the origin of `order`, the suggestion (drawn only from what the object actually holds) and the stack come from replaying the run with tracing on, so a run that succeeds pays nothing for them. Syntax errors are reported all at once:

```
$ oneecma check two.js
error[SyntaxError]: Identifier 'a' has already been declared
 --> two.js:2:5
  |
1 | let a = 1;
  |     ----- first declared here
2 | let a = 2;
  |     ^^^^^ declared again here
  |
  = help: rename one of them, or assign with `a = 2` if `a` should change
  = note: not repaired automatically: every fix changes what the program means
  = note: a second error follows: both are reported from one parse

error[SyntaxError]: Unexpected token ';'
 --> two.js:3:9
  |
3 | let b = ;
  |         ^ expected an expression before `;`
  |
  = help: give `b` a value, or remove the declaration

error: aborting due to 2 syntax errors
```

The same reports are available as versioned JSON ([docs/diagnostic-json.md](docs/diagnostic-json.md)).

## Install

```
dotnet add package RecursiveAI.OneEcma              # the engine, Intl included
dotnet add package RecursiveAI.OneEcma.Generators   # [JsExport] host bindings (analyzer only)
dotnet tool install --global RecursiveAI.OneEcma.Cli   # the `oneecma` command
```

The packages are not published yet; until they are, build from source (below) and reference the projects.

## Use the library

Evaluate a script, under limits, and print the report when it throws:

```csharp
using RecursiveAI.OneEcma;
using RecursiveAI.OneEcma.Diagnostics;

var engine = new ScriptEngine(new ScriptEngineOptions
{
    Limits = new EngineLimits { Fuel = 50_000_000, WallClockMs = 2_000, MaxCallDepth = 2_000 },
});
try
{
    JsValue result = engine.Evaluate("[1, 2, 3].map(n => n * 2).join('-')", "sum.js");
    Console.WriteLine(engine.ToDisplayString(result));            // 2-4-6
}
catch (JsException e)
{
    Console.WriteLine(DiagnosticRenderer.Render(e.Report!));      // e.Report is the structured report; e.Message is the short form
}
```

Check source without running it (every syntax and early error, not just the first):

```csharp
using RecursiveAI.OneEcma.Syntax;

foreach (Diagnostic d in ScriptEngine.Check("let a = 1;\nlet a = 2;", new ParseOptions { FileName = "two.js" }))
{
    Console.WriteLine(d.Headline);                                // Identifier 'a' has already been declared
}
```

### Host bindings with `[JsExport]`

Mark .NET types and a source generator writes the marshalling at build time (no reflection at run time, so it works under NativeAOT). Only exported members exist in script. A mistake at the boundary names the .NET member, the argument path and the expected and actual types:

```csharp
[JsExport]
public record Item(string Sku, int Quantity, decimal Price);   // copied by value: script passes plain objects

[JsExport]
public class Cart
{
    public decimal Total(Item item) => item.Quantity * item.Price;
}

var host = new ScriptEngine();
JsRealm realm = host.MainRealm;
JsExports.InstallAll(realm);                                   // generated
realm.DefineData(realm.Global, "cart", realm.Wrap(new Cart()));

host.Evaluate("cart.total({ sku: 'pen', quantity: 3, price: 1.5 })", "a.js");     // 4.5
host.Evaluate("cart.total({ sku: 'pen', quantity: '3', price: 1.5 })", "bad.js");
```

The second call throws a `JsException` whose report is:

```
error[TypeError]: Cart.Total: argument 'item.quantity' must be an integer (System.Int32), got string "3"
 --> bad.js:1:1
  |
1 | cart.total({ sku: 'pen', quantity: '3', price: 1.5 })
  | ^^^^^^^^^^ `item.quantity` is string "3", expected an integer (System.Int32)
  |
  = help: convert the string first: Number("3")
  = note: host function: Cart.Total(Item item)
  = stack: Cart.Total [.NET] ← <top-level> (bad.js:1)
```

Exceptions cross in both directions: a .NET exception inside a host call reaches script as an `Error` that carries the .NET type, and a script throw reaching C# is a `JsException` with the full report; reports interleave script and .NET frames. [samples/OrderHost](samples/OrderHost) is a larger worked example, and [docs/js-export.md](docs/js-export.md) lists what can be exported and the generator's diagnostics.

### Tools from a JSON Schema

`JsonSchemaTools` registers a host function whose argument is checked against a JSON Schema before your handler runs, with the same error form. This is how an agent host exposes its tools to scripts:

```csharp
JsObject fn = JsonSchemaTools.Define(realm, "createOrder",
    """{ "type": "object", "required": ["id"], "properties": { "id": { "type": "integer" } } }""",
    args => args);
realm.DefineData(realm.Global, "createOrder", JsValue.FromObject(fn), JsAttributes.Method);
// createOrder({ id: 'x' })  ->  TypeError: createOrder: argument 'args.id' must be an integer, got string "x"
```

## The command line

```
oneecma run [--module] [--locals] [--compact] <file>
                                         run a script; report on stderr and exit status 1 on an uncaught error;
                                         --locals adds the bindings of each frame (`= locals in f: a = 1, ...`);
                                         --compact prints the short report
oneecma check [--module] [--compact] [--format text|json] [--source-map <map>] [--repair [--out <path>]] <file>
oneecma repl                             multi-line input, top-level await, .exit
```

`check` parses and runs the early errors only. Colour is used only on a terminal and never when `NO_COLOR` is set; `--format json` writes the JSON report to stdout.

`--compact` (also `RenderOptions.Compact`) is for readers with limited context, such as language models: headline, `-->` location, the source line with its underline and label, the origin label when the value's source is known, the single most useful `help:`, and a one-line stack only when there is more than one frame. Blank gutter lines, `...` separators, extra helps and notes, locals and source-line context are dropped, and long lines are cut to about 100 columns.

## Two API layers

- **Low level:** `JsValue` (a 16-byte struct, no boxing of numbers), `JsObject`, `JsRealm`, property and function callbacks, promises, modules, `IJsHost` for everything environmental (clock, randomness, locale, modules, output) and the job queue. Everything else is built from these public pieces. See [docs/host-api.md](docs/host-api.md).
- **Happy path:** `ScriptEngine.Evaluate` and `ScriptEngine.Check`, `[JsExport]`, `JsonSchemaTools`, and the `oneecma` command.

## What is supported

- The ES2026 language and built-ins (ECMA-262, 17th edition): strict and sloppy mode, modules and dynamic import, generators, async functions and async generators, classes with private members, BigInt, `Proxy` and `Reflect`, typed arrays, `Atomics` (single agent), `Date`, iterator helpers, and Annex B. Temporal is a later edition and is not included.
- Our own backtracking RegExp engine with the `u` and `v` flags, lookbehind, named groups and Unicode properties; every backtracking step is charged to the fuel limit.
- Source maps (ECMA-426): reports about minified or generated code point at the original source.
- Intl (ECMA-402) for `en-US`, `en-GB`, `es` (including `es-US`), `fr`, `de` and `pt-BR`, Gregorian calendar and Latin digits, generated from CLDR 48.0 and Unicode 17.0.0. Unsupported locales fall back to the default locale. Adding a locale is a data change ([docs/locale-data.md](docs/locale-data.md)).

## Limits and sandbox

A script reaches only what the host passes in: the engine has no file, network, process or environment access of its own. Every source of nondeterminism (clock, randomness, host-call results, job order) enters through `IJsHost` and is recorded, so a failed run is replayed exactly to build its report.

`EngineLimits` bounds fuel (steps charged at calls, loop back-edges, RegExp backtracking and long-running built-ins), allocated memory, wall-clock time, call depth and string length. A limit ends the run with a report like any other error. Cancellation is a `CancellationToken` on every computing entry point. Parsing and the executor use explicit stacks or depth counters, so deep nesting in a script cannot overflow the .NET stack. Defaults are in the XML docs of `EngineLimits` and in [docs/runtime.md](docs/runtime.md).

## Auto-repair

Off by default and syntax-only. `ScriptEngine.CheckAndRepair` (or `oneecma check --repair`) acts only on source that fails to parse, applies a fix only when it is the single plausible one and the result parses, and turns every fix that could change meaning into a suggestion. Applied repairs are listed first in the report and the repaired source is returned:

```
$ oneecma check --repair extra.js
repair[R001]: removed an extra closing delimiter
 --> extra.js:2:38
  |
2 | const doubled = items.map(x => x * 2));
  |                                      ^ removed
  |
  = note: the repaired source parses and is returned with this report
const items = [1, 2, 3];
const doubled = items.map(x => x * 2);
print(doubled);
```

Each rule, with examples, is in [docs/repair-table.md](docs/repair-table.md).

## Conformance

Pass rates on the [test262](https://github.com/tc39/test262) suite pinned in `tools/test262/pin.txt`, run by `tools/OneEcma.Test262` in strict and sloppy variants. Every failing test is listed with its reason in `tools/test262/documented-engine.txt`; documented failures stay in the denominator.

| Directory | Pass | Documented failures | Unexpected failures |
|---|---:|---:|---:|
| `language` | 22,973 | 31 | 0 |
| `built-ins` | 18,337 | 146 | 0 |
| `annexB` | 1,057 | 3 | 0 |
| `intl402` (six locales) | 1,104 | 17 | 0 |

Speed against Jint, including lodash, preact and a React server render checked byte for byte against Node, is in [docs/benchmarks.md](docs/benchmarks.md).

`err.message` and `err.stack` are compared with Node on a differential corpus ([docs/stack-traces.md](docs/stack-traces.md)). Tests that need features of a later edition (Temporal and others) are skipped with a reason in `tools/test262/scope.txt`.

## Documentation

[docs/README.md](docs/README.md) indexes the behaviour documents: the host API, `[JsExport]`, the JSON report, record log, repair table, syntax-message wording, RegExp, Date, Intl and locale data. Every public type and member has an XML doc comment stating defaults, limits and what it throws.

## Build and test

Requires the .NET 10 SDK.

```sh
dotnet build OneEcma.slnx
dotnet test --solution OneEcma.slnx
dotnet pack src/RecursiveAI.OneEcma -c Release
dotnet publish tests/OneEcma.AotSmoke -c Release -r linux-x64   # NativeAOT/trim check; use your RID
```

Build output goes to `artifacts/` (ignored). Set `ONEECMA_ARTIFACTS` to redirect it.

| Path | Contents |
|---|---|
| `src/RecursiveAI.OneEcma` | the engine |
| `src/RecursiveAI.OneEcma.Generators` | the `[JsExport]` source generator (analyzer-only package) |
| `src/OneEcma.Cli` | the `oneecma` tool |
| `tests/` | unit tests, generator tests, the NativeAOT smoke app |
| `samples/` | a worked host example |
| `fuzz/`, `bench/` | fuzz targets and the BenchmarkDotNet suite (Jint is referenced only in `bench/`, as a comparison target) |
| `tools/` | the test262 runner, differentials against Node, data generators |
| `docs/` | behaviour documents |

## Licence

Licensed under either of [MIT](LICENSE-MIT) or [Apache-2.0](LICENSE-APACHE) at your option. Copyright Recursive AI, LLC. Third-party data and test suites are listed with their licences in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
