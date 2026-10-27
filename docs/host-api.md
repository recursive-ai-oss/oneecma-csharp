# Host API (low level)

The host API is the base layer: `[JsExport]` bindings, the JSON-Schema tool helper and `ScriptEngine.Evaluate` are all written
with only the public types below, so a host can build its own happy path the same way. Nothing here uses reflection or emits code;
it is safe under NativeAOT and trimming.

All calls run on the thread that owns the `ScriptEngine` (an engine is not thread-safe). A host callback runs on that thread; a task
returned by a host method resumes script on that thread when the engine is pumped (see "Tasks").

## Types

| Type | Role |
|---|---|
| `ScriptEngine` | One agent: realm, job queue, limits. `Evaluate`, `EvaluateAsync`, `EvaluateAsyncBody`, `CheckAsyncBody`, `CheckAndRepairAsyncBody`, `EvaluateModule`, `RunJobs`, `RunUntilIdle(Async)`, `Await(Async)`, `Call`, `GetProperty`, `SetProperty`, `DefineGlobalFunction`, `CreateRealm`, `MainRealm`, `Install`. |
| `JsRealm` | A global object with its own built-ins. Creates and inspects values, defines properties, calls functions, makes promises, wraps .NET objects. `engine.MainRealm`, `engine.CreateRealm()`. |
| `JsValue` | 16-byte struct: undefined, null, boolean, number, string, symbol, BigInt, object. `FromNumber/FromString/FromBoolean/FromBigInt/FromObject/FromSymbol`, `IsX` tests, `AsNumber/AsString/AsBoolean/AsBigInt/AsSymbol/AsObject`. |
| `JsObject` | Any object, opaque; use `JsRealm` to read and write it. `JsHostObject` is the object that stands for a .NET object (`Target`). |
| `JsAttributes` | `Writable`, `Enumerable`, `Configurable`, `All`, `Method` (writable and configurable, hidden: the default for host members). |
| `JsPromise` | A promise made in C#: `Value`, `State`, `Result`, `Resolve`, `Reject`, `OnSettled`. |
| `HostFunction` / `HostConstructor` | Delegates for host functions `(this, args) -> value` and constructors `(args, newTarget) -> value`. |
| `JsException` | A thrown script value. `new JsException(value)` throws a script value from a host function; `Report` is the `Diagnostic`; `InnerException` is the original .NET exception when the error came from one. |
| `JsLimitException` | A limit (fuel, memory, time, depth) ended the run. |
| `IJsHost` | Clock, randomness, locale, modules, printing: everything environmental. `DefaultHost` is the system implementation; `InMemoryModuleHost` serves modules from strings. |

## Values, properties and functions

```csharp
var engine = new ScriptEngine();
JsRealm realm = engine.MainRealm;

JsObject cart = realm.NewObject();
realm.DefineData(cart, "total", JsValue.FromNumber(12.5));                         // enumerable, writable, configurable
realm.DefineData(cart, "id", JsValue.FromString("c1"), JsAttributes.Enumerable);   // read-only
realm.DefineAccessor(cart, "double",
    realm.NewFunction("get double", 0, (_, _) => JsValue.FromNumber(realm.ToNumber(realm.Get(cart, "total")) * 2)),
    setter: null, JsAttributes.Enumerable);
realm.DefineData(realm.Global, "cart", JsValue.FromObject(cart));

JsObject items = realm.NewArray([JsValue.FromString("pen"), JsValue.FromNumber(3)]);
foreach (JsValue key in realm.OwnKeys(cart))                  // spec order: integer keys, strings, then symbols if asked
    Console.WriteLine(realm.ToDisplayString(key));

JsObject math = realm.Get(realm.Global, "Math").AsObject!;
JsValue max = realm.Call(realm.Get(math, "max"), JsValue.Undefined, JsValue.FromNumber(1), JsValue.FromNumber(9));
```

- `NewFunction(name, length, fn)` makes a function; a .NET exception thrown inside it reaches script as an `Error` (below).
- `NewConstructor(name, length, construct, out prototype, basePrototype)` makes a class: calling without `new` is a TypeError, a script
  `class X extends Host` works, and `PrototypeFor(newTarget, fallback)` gives subclasses their prototype.
- `NewSymbol(description)`, `WellKnownSymbol("iterator")`, `NewError("RangeError", message)` create the values they say.
- `Get`, `Set`, `Has`, `Delete`, `DefineData`, `DefineAccessor`, `OwnKeys`, `GetPrototypeOf`, `Call`, `Construct` run the spec's operations,
  so getters, setters and proxy traps run and may throw `JsException`.
- `Describe(value)` gives the short text used in boundary errors (`number 5`, `an array of 3 items`, `an object with keys id, total`).

## Realms

`engine.CreateRealm()` returns a second realm with its own `Object`, `Array`, … and global. `realm.Evaluate(source, name)` runs a script
in it. Values move between realms freely; each function runs in the realm it was created in. Limits and the job queue are the engine's.

## Promises, jobs, tasks

```csharp
JsPromise p = realm.NewPromise();
realm.DefineData(realm.Global, "ready", p.Value);
engine.Evaluate("ready.then(v => print(v))");
p.Resolve(JsValue.FromString("go"));
engine.RunJobs();                                 // runs the reaction

JsValue v = engine.Await(engine.Evaluate("fetchThing()"));       // pumps jobs until settled; throws JsException on rejection
JsValue w = await engine.EvaluateAsync("(async () => 1 + 1)()");  // also waits for host tasks without blocking a thread
```

- `Evaluate`, `RunJobs`, `Call` drain the job queue. `RunUntilIdle` additionally waits for host tasks (below) to finish and runs what they
  resume; `RunUntilIdleAsync` does so without blocking the thread.
- `Await(value)` pumps until the promise settles and returns its value, or throws `JsException` with the reason. A promise that can
  never settle (nothing queued, no host task running) is an error, not a hang.
- A task returned by a host function becomes a promise (`JsMarshal.FromTask`). While the host method runs, a synchronization context
  routes its `await` continuations back to the engine's thread, so the callbacks it makes into script are safe; they run when the
  engine is pumped. The host that calls `Evaluate` (not `EvaluateAsync`) must pump with `RunUntilIdle`/`Await`.
- Unhandled rejections end a run with the rejection's `JsException` unless `ScriptEngineOptions.ThrowOnUnhandledRejection` is false.
- A host task may be complete when it reaches the engine (`Task.FromResult`, a cached task, an `async` method with a synchronous fast path), faulted
  or cancelled, or throw before it returns a task. All of them become a promise that is settled (fulfilled or rejected with the .NET exception as
  a script `Error`) before the call returns, and all of them record and replay exactly like a task that finishes later.
- When the engine explains a failure it replays the recorded run. If that is not possible the plain report stays and gains a note,
  `= note: explanation unavailable: <reason>`: the run cannot be replayed (with the reason from `RecordLog.NotReplayableReason`), the replay
  diverged from the recorded run (with what differed), the replay did not fail the same way, or the rejected promise was not the result
  of an entry call. The error and its headline are never replaced.

## Running code as an async function body

Hosts that run code written by someone else (an LLM, a user) usually want `await` and `return` at the top level, a fresh scope per call, and
positions that point into the text they were given. `ScriptEngine.EvaluateAsyncBody` does that without a wrapper:

```csharp
JsValue p = engine.EvaluateAsyncBody("const o = await api.getOrder(id);\nreturn o.items.length;", "checkout.js");
JsValue n = await engine.AwaitAsync(p);           // throws JsException with the explained report if the body failed
```

- **Goal.** The text is parsed with the goal of an `AsyncFunctionBody` (`ParseGoal.AsyncFunctionBody`), the one `new AsyncFunction(body)` uses
  (ECMA-262 15.8), as the body of an anonymous `async` function with no parameters. `await` and `return` are valid at the top level; `yield`,
  `super` and `import`/`export` are not; `new.target` is allowed and is undefined. Sloppy unless the text starts with `"use strict"`.
- **Scope.** Every `let`, `const`, `var`, function and class declaration belongs to the call and disappears with it, so two calls never collide
  and the global object gains nothing (an assignment to an undeclared name still creates a global in sloppy code, as anywhere).
  Free names resolve against the global object and the host's globals; `this` is the global object, in strict bodies too.
- **Positions.** The source is not wrapped or shifted: line and column in reports, `err.stack` and `Diagnostic` spans are those of the text
  passed in, so no source map is needed. The body's frame reads `<top-level>` in reports and has no function name in `err.stack`
  (`at checkout.js:2:10`); a frame stitched across an `await` reads `async <top-level>`.
- **Result.** Returns the promise of the body's completion: fulfilled with the `return` value (undefined when none), rejected with a thrown
  error. The call drains the job queue first, so a body that never waits on a host task has already settled. A body waiting on a host task
  returns a pending promise; wait with `Await`/`AwaitAsync`, or pump with `RunUntilIdle(Async)`. With the default
  `ThrowOnUnhandledRejection`, a body that rejected without anyone handling it makes `EvaluateAsyncBody` (or the pump that ran the failing
  continuation) throw `JsException`; a host that awaits the promise it was given gets the same exception from `Await`.
- **Syntax errors.** `EvaluateAsyncBody` throws `JsException` (a `SyntaxError` report) for the first error. `ScriptEngine.CheckAsyncBody(source, fileName)`
  returns every syntax and early error without running, and `CheckAndRepairAsyncBody` applies the syntax-only repair table with the same
  goal and returns the repaired source to pass to `EvaluateAsyncBody`.
- **Record and replay.** The call is an entry call of the log (`AsyncBody`), and `Await`/`AwaitAsync` on its promise is logged, so a failure is
  explained like any other: provenance of the failing value, did-you-mean, locals and the async stack, across any host task that was awaited.

## Modules

Modules come from `IJsHost.ResolveModule(specifier, referrerId, attributes)`. `engine.EvaluateModule("main.js")` resolves the entry,
loads its imports through the same call and returns the namespace object. `InMemoryModuleHost` resolves `./` and `../` specifiers against
the importer's slash-separated id:

```csharp
var host = new InMemoryModuleHost().Add("lib/util.js", "export const twice = x => x * 2;")
                                   .Add("main.js", "import { twice } from './lib/util.js'; export const answer = twice(21);");
var engine = new ScriptEngine(new ScriptEngineOptions { Host = host });
JsValue ns = engine.EvaluateModule("main.js");
```

## .NET objects in script

`JsRealm.Install(JsHostType)` creates a class for an exported type (the generator's `XJsBinding.JsType`; `JsExports.InstallAll(realm)` installs
every exported type of the assembly). `realm.Wrap(obj)` returns the script object for a .NET object: the same object each time, with the
members the type exports on its prototype. Only exported members exist in script; `Wrap` of an object whose class was not installed throws
`InvalidOperationException` that says what to install. `NewHostObject(target, prototype)` is the primitive underneath.

## Errors across the boundary

| Direction | What happens |
|---|---|
| .NET exception in a host function | Script sees `Error` with message `<TypeName>: <message>` and a non-enumerable `dotnetType` (the full type name). It is catchable. If it reaches C# uncaught it is `JsException` whose `InnerException` is the original and whose `Report` lists the .NET frames. |
| Script throw reaching C# | `JsException` with `Value`, `Report`, `Frames`. |
| Argument conversion failure | `TypeError`, message `<Member>: argument '<path>' must be <expected>, got <actual>` (see `js-export.md`). |
| A `JsException` thrown by a host function | Passes through unchanged (`throw new JsException(realm.NewError("RangeError", "…"))`). |

Reports interleave frames: `StackFrameInfo.IsHost` marks a .NET frame (`Function` is the member, `HostLine` its line when the runtime knows it).
Text shows `Member (File.cs:38)` or `Member [.NET]` when there is no line (NativeAOT keeps method names only). JSON adds `host: true` and
`hostLine`. Frames from the engine, the generated bindings and the BCL are left out.

## Limits

Script that calls a host function spends fuel for the call like any other call; a loop that calls .NET is bounded by the step limit.
`NewObject(prototype)`, `NewArray` and host-object creation are charged to the memory limit. A script callback invoked from .NET runs
under the same limits and can end the run with `JsLimitException`. The time and cost of the .NET code itself is the host's to bound.

## What changed in other types

`JsValue` gained `AsBoolean`, `AsBigInt`, `AsSymbol`, and `AsString`/`FromSymbol` became public. `JsException` has a public constructor.
`StackFrameInfo` gained `IsHost` and `HostLine`. `ScriptEngine` is `partial`; its new members live in `Hosting/`.
