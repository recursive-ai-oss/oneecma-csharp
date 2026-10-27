# Runtime guide: adding a built-in area

This is the contract for code under `Builtins/`. You can write a built-in area (a constructor, its prototype and its static functions) from this page and the spec alone, without reading the VM. Everything here is `internal` to `RecursiveAI.OneEcma` unless marked public.

**Frozen** means: do not change the shape of it. Additions that old code ignores are fine. A change that breaks a caller needs the maintainers of the core to agree first.

| Frozen | Not frozen |
|---|---|
| `JsValue` (public constructors `FromX`, predicates, `AsNumber`/`AsObject`/`AsString`), `PropertyKey`, `PropertyDescriptor`, `PropFlags`, the virtual internal methods of `JsObject`, `NativeFn`, `Realm.Fn/Method/Getter/Value/Ctor`, `Errors.Type/Range/Reference/Syntax/Make`, `Agent.Tick/Charge/Alloc/EnqueueJob`, `IJsHost`, `EngineLimits` | `Shape` internals, `Bytecode`, `Vm`, `Frame`, `Generators`, `Modules`, the `Realm` named intrinsic fields (new ones may be added) |

## Values

`JsValue` is a 16-byte readonly struct: an object reference plus a double. Numbers never allocate. The reference says which kind it is.

| Kind | Make | Test | Read |
|---|---|---|---|
| undefined | `default`, `JsValue.Undefined` | `IsUndefined` | |
| null | `JsValue.Null` | `IsNull`, `IsNullish` (undefined or null) | |
| boolean | `JsValue.True/False`, `FromBoolean` | `IsBoolean` | `Ops.ToBoolean(v)` |
| number | `FromNumber(double)` | `IsNumber` | `AsNumber` |
| string | `FromString(string)` | `IsString` | `AsString` (flattens a rope) |
| symbol | `FromSymbol(JsSymbol)` (internal) | `IsSymbol` | `(JsSymbol)v.Ref` |
| bigint | `FromBigInt(BigInteger)` | `IsBigInt` | `(BigInteger)v.Ref` |
| object | `FromObject(JsObject)` | `IsObject` | `AsObject`, `v.Ref as JsObject` |

`Tdz` and `Hole` are internal sentinels for uninitialised bindings and array holes; they never reach a built-in as an argument. A string may be a rope internally; always use `AsString`. Equality is SameValue (`==` on `JsValue`); use `Ops.SameValueZero`, `Ops.StrictEquals`, `Ops.LooseEquals` for the other spec comparisons.

Inside `Ops` (static, in `Runtime/Ops.cs`) you find the abstract operations by their spec names: `ToPrimitive`, `ToNumber`, `ToNumeric`, `ToIntegerOrInfinity`, `ToInt32`, `ToUint32`, `ToLength`, `ToIndex`, `ToStr` (ToString), `ToObject`, `ToPropertyKey`, `RequireObjectCoercible`, `IsCallable`, `IsConstructor`, `IsArray`, `LengthOfArrayLike`, `Call`, `Invoke`, `Construct`, `GetMethod`, `GetV`, `SetV`, `OrdinaryHasInstance`, `InstanceOf`, `SpeciesConstructor`, `OrdinaryCreateFromConstructor`, `GetPrototypeFromConstructor`, `ListFromArrayLike`, `CreateArray`, `CreateIterResult`, `GetIterator`, `IteratorStepValue`, `IteratorClose`, `IterableToList`. Each performs the observable steps in spec order, so calling it is the correct way to do "Let x be ? ToString(y)". `Ops.Arg(args, i)` is the argument or undefined.

## Properties and objects

`PropertyKey` is a string or a `JsSymbol`. A `string` converts implicitly (`obj.Get("length")`). Array indices are ordinary string keys in the API; `key.TryGetIndex(out uint)` recognises canonical indices.

`PropFlags`: `Writable`, `Enumerable`, `Configurable`, `Accessor`, with the combinations `None` (all false), `Default` (W+E+C, what `CreateDataProperty` makes), `Hidden` (W+C, not enumerable: the usual flags for built-in methods), `Configurable` alone (what `length` and `name` have).

`PropertyDescriptor` has `HasValue/Value`, `HasWritable/Writable`, `HasGet/Get`, `HasSet/Set`, `HasEnumerable/Enumerable`, `HasConfigurable/Configurable`, `IsAccessor`, and `PropertyDescriptor.Data(value, flags)`.

`JsObject` implements the ordinary internal methods of ECMA-262 10.1 as virtuals. Exotic objects override exactly the ones the spec says they override, and nothing else:

| Spec method | C# | Notes |
|---|---|---|
| [[GetPrototypeOf]] / [[SetPrototypeOf]] | `GetPrototypeOf()` / `SetPrototypeOf(p)` | returns bool |
| [[IsExtensible]] / [[PreventExtensions]] | `IsExtensibleOp()` / `PreventExtensions()` | |
| [[GetOwnProperty]] | `GetOwnProperty(key, out desc)` | returns whether it exists |
| [[DefineOwnProperty]] | `DefineOwnProperty(key, in desc)` | returns bool; `ValidateAndApply` is ValidateAndApplyPropertyDescriptor |
| [[HasProperty]] | `HasProperty(key)` | |
| [[Get]] / [[Set]] | `Get(key, receiver)`, `Get(key)`; `Set(key, value, receiver)` | |
| [[Delete]] | `Delete(key)` | |
| [[OwnPropertyKeys]] | `OwnKeys(List<PropertyKey>)` | appends in spec order (indices ascending, strings in creation order, symbols) |
| [[Call]] / [[Construct]] | `Call(this, args)`, `Construct(args, newTarget)` | overridden by function objects |

Helpers on `JsObject` that are the spec's abstract operations: `CreateDataProperty`, `CreateDataPropertyOrThrow`, `DefinePropertyOrThrow`, `HasOwnProperty`, `SetOrThrow(key, v)`. For building a built-in's own properties use the *direct* definers, which skip the checks because the object is fresh: `DefineDirect(key, value, flags)` and `DefineAccessorDirect(key, getter, setter, flags)`.

To make an exotic object, subclass `JsObject` (or `JsArray` for array-likes that store elements densely), pass the prototype to the base constructor, override the internal methods your spec section lists, and set `ClassName`. Internal slots are plain C# fields on your subclass: an `IsMyThing(this)` check is `thisValue.Ref is MyThing`. Do not use shape slots for internal state.

Large objects are cheap to build one property at a time: a `Shape` indexes its keys only after it has been looked up several times, so a built-in prototype with dozens of `DefineDirect` calls does not index every intermediate shape. Code that holds a `Frame` or an `Env` past the call that created it must mark it (`Frame.Escaped`, `Env.MarkCaptured`), because finished frames and uncaptured environments are reused; see `executor.md`.

### Deferred own properties

An ordinary function's own `prototype` object (ECMA-262 10.2.5 MakeConstructor) is created the first time anything can observe it, because most closures never use it. `ScriptFunction` created with `lazyPrototype: true` has the shape from `Realm.FunctionShape(proto, 3)`: `length`, `name`, then `prototype` flagged as an accessor with an empty slot, and `JsObject.Lazy` is set. The flag marks the slot as not a data property, so no cached or direct slot read ever uses it. The function overrides `GetOwnProperty` and `TryGetOwn` (for the key `prototype`) and `DefineOwnProperty`, `Delete`, `OwnKeys`, `SetPrototypeOf` and `PreventExtensions` (always) to call `Realize()`, which creates the object, switches to the ordinary shape (`FunctionShape(proto, 1)`) and clears `Lazy`. The non-virtual storage primitives on `JsObject` (`AddProperty`, `ReplaceProperty`, `DefineDirect`, `DefineAccessorDirect`, `RemoveSlot`) realize first, so a property added to a function still comes after `prototype`. A new virtual that exposes or changes an object's own keys must do the same if it can be reached on a `Lazy` object; code that only reads `length` and `name` through the shape needs nothing.

`Shape.DigitKeys` counts the keys that start with a decimal digit (kept in step by `Add` and `Remove`, also in dictionary mode). `ArrayAlgo.ChainIndexFree` uses it, so `push` and similar fast paths decide in constant time per prototype whether an index key could be intercepted.

### Strings built by concatenation

`a + b` of two strings whose joined length is 24 or more makes a `Rope` node and charges 48 bytes. The characters are charged when the rope is first observed (`AsString`, `Rope.Flatten`), which copies once from the end of the result, following the left spine without a stack and keeping a small stack only for right children that are themselves ropes; flattening never recurses on the host stack. A flattened node keeps its string and drops its children. `length` on a string primitive and a `String.prototype` method lookup (`GetStringProp` in the VM) do not flatten. A value that a host function receives is always a flat `string`. While a limit report is being described, memory charges are recorded but do not raise a second limit (describing a frame previews its string locals, which flattens ropes).

## Native functions

```csharp
internal delegate JsValue NativeFn(JsValue thisValue, ReadOnlySpan<JsValue> args, JsObject? newTarget);
```

`newTarget` is null for a call and the constructor for `new`/`Reflect.construct`. A function that is not a constructor ignores it. Arguments beyond `args.Length` are undefined: always read with `Ops.Arg(args, i)`. Do not keep `args` beyond the call (it is a span over the VM stack).

`Realm` registers things. Inside `Builtins.SetupXxx(Realm r)`:

```csharp
JsObject proto = new JsObject(r.ObjectPrototype);              // the prototype object
NativeFunction ctor = r.Ctor("Thing", 1, (t, a, nt) => {       // defines global Thing, %Thing%, .prototype, .constructor
    if (nt is null) throw Errors.Type("Constructor Thing requires 'new'");
    JsObject o = Ops.OrdinaryCreateFromConstructor(nt, static x => x.ObjectPrototype); // honours newTarget.prototype
    return JsValue.FromObject(o);
}, proto);
r.Method(proto, "size", 0, (t, a, nt) => ...);                 // writable, configurable, non-enumerable; name and length set
r.Method(proto, WellKnown.Iterator, "[Symbol.iterator]", 0, fn);
r.Getter(proto, "size", "size", (t, a, nt) => ...);            // accessor, name "get size"
Realm.Value(ctor, "MAX", JsValue.FromNumber(7), PropFlags.None);
r.Intrinsics["%ThingPrototype%"] = proto;                      // for other areas and for realm-aware lookup
```

`r.Fn(name, length, fn)` makes an anonymous-parent function value without defining it anywhere. Register the area by adding a `SetupThing(r)` call to `Builtins.Setup` (order matters only when your area needs another's prototype: call yours after it). Use `r.Intrinsics` for anything another area or the VM must find by name. Add a named field on `Realm` only when the VM needs it on a hot path.

`Agent.Current` is the running engine on this thread; `Ops.CurrentRealm` is its current realm (the realm of the running native function). Built-ins that run when the realm differs from the creating realm (cross-realm `newTarget`) use `Ops.GetPrototypeFromConstructor`, which does the spec's GetFunctionRealm fallback.

## Throwing

Throw `Errors.Type(message)`, `Errors.Range`, `Errors.Reference`, `Errors.Syntax`, or `Errors.Make("URIError", message)`. They return a `JsException` to `throw`. They create the Error object in the current realm and capture the stack. The `message` is the headline text exactly as Node prints it in `err.message`: take it from running Node on the failing call, never invent it. Use `Ops.DisplayValue(v)` where V8 interpolates a value (`Object prototype may only be an Object or null: undefined`) and `Ops.ObjectLabel(o)` for `#<Object>`.

```csharp
if (thisValue.Ref is not JsThing self)
    throw Errors.Type("Method Thing.prototype.size called on incompatible receiver " + Ops.DisplayValue(thisValue));
```

Never throw another .NET exception type on purpose; an unexpected one becomes an internal-error report. Limits are thrown by the agent (below), not by you.

## Fuel, memory and time

`Agent.Current.Tick()` spends one unit of fuel; `Charge(n)` spends n. At the end of each 4096-unit chunk the agent polls the cancellation token and the wall clock, and throws `JsLimitException` when fuel, time or cancellation says stop. Script cannot catch it.

Rule: **every native loop whose trip count depends on script data calls `Tick()` once per iteration** (or `Charge(n)` with n the unit count for a bulk operation), including loops that only call into non-charging code. That covers `sort`, `join`, `repeat`, `padStart`, `flat`, `fill`, `indexOf`, `copyWithin`, `splice`, string building, iteration helpers, and RegExp backtracking. Calls into script (`Ops.Call`) are charged by the VM already. A loop of a few constant iterations needs nothing.

Before allocating something sized by script data (a string result, an array of length n, a buffer), call `Agent.Current.Alloc(bytes)` first. It counts bytes *requested* cumulatively (not live bytes) against `EngineLimits.MaxAllocatedBytes`. Strings longer than `EngineLimits.MaxStringLength` must throw `Errors.Range("Invalid string length")`. Arrays of length over 2^32-1 throw `RangeError: Invalid array length`.

## No host recursion that follows script depth

The VM runs JS-to-JS calls on an explicit frame stack, so JS recursion does not consume the .NET stack. Native code that calls back into script (`Ops.Call`, `Construct`, a getter reached by `Get`, a Proxy trap) does consume it; the VM counts these re-entries and ends the run past `EngineLimits.MaxNativeDepth` (default 120). So:

- Calling script from a native is fine; the count is automatic.
- Native code that recurses on its own structure when that structure is script-controlled (deep arrays in `flat`, nested objects in `JSON.stringify`/`parse`, the proto chain in `OrdinaryHasInstance`, Proxy chains) must use an explicit stack or carry a depth counter and throw `Errors.Range("Maximum call stack size exceeded")` at a fixed bound (e.g. 1000 for data recursion). Prefer iteration.
- Never `lock`, block, or call `.Result` on anything; the engine is single-threaded per agent and re-entrancy is the model.

## The host

`IJsHost` (public, frozen) is the only way out. A built-in that needs the time, randomness, the locale, the zone, a module or output asks `Agent.Current.Host`: `CurrentTimeMs()` (Date), `MonotonicMs()` (limits, `performance.now`), `NextRandom()` (`Math.random`), `DefaultLocale`, `TimeZoneId`, `ResolveModule`, `Print`, `JobEnqueued`, `PromiseRejectionTracker`, `InitializeImportMeta`, `WantsWeakCollection`. Never call `DateTime.Now`, `Stopwatch`, `Random` or the environment yourself; `HostDefaults.cs` is the one file that may. Anything new that is nondeterministic becomes a new `IJsHost` member with a default in `DefaultHost` (additive, so existing hosts keep compiling) and a line here.

Promise jobs go through `Agent.EnqueueJob(Action)`; never run a reaction synchronously. `WeakRef` and `FinalizationRegistry` observations are scheduled by the engine through the job queue, never by GC timing.

### Wording that names an object or a value

Every row below was recorded from Node v24 (`testdata/errors/cases.txt`, ids `iter-*` and `fnstr-*`) and is compared with the engine on each test run.

- **Object label** (`Ops.ObjectLabel`), used by `Cannot delete property 'k' of L`, `Cannot set property k of L which has only a getter` and, inside quotes, `Cannot assign to read only property 'k' of object 'L'`:
  a function is its `Function.prototype.toString` text (and the message says `function '...'` instead of `object '...'`); an Error is `Name: message`; an array, String/Number/Boolean/Symbol/BigInt wrapper, Date, RegExp or typed array is `[object Class]`; anything else is `#<C>` where C is the name of the constructor function found by reading `constructor` along the prototype chain without running script (a script function's own declared name wins over a redefined `name`), or `[object Object]` when there is none (null prototype, `constructor` not a function). A Proxy is `#<Object>`.
- **Store to a string primitive** (strict): `length` and indices below the length give `Cannot assign to read only property 'k' of string 's'`; any other key gives `Cannot create property 'k' on string 's'`.
- **`Object.defineProperty` on a non-extensible object** adding a new key: `Cannot define property k, object is not extensible`; any other refusal: `Cannot redefine property: k`.
- **Value kind** (`Ops.KindText`): `undefined`, `object null`, `symbol`, `bigint`, `function`, `object`, `string "s"`, `number 5`, `boolean true`. Used by `X is not iterable (cannot read property Symbol(Symbol.iterator))` from built-ins, `yield*`, array-pattern parameters and assignment patterns, and by `X is not a function` when `GetMethod` finds a non-callable (`Symbol.toPrimitive`, `Symbol.hasInstance`, `Symbol.asyncIterator`).
- **Source text** in `X is not iterable` for `for-of`, spread and declaration initializers: an identifier, member chain, `null`, `true`/`false`, a number, a regular expression literal, `{}` (`{(intermediate value)}` with properties), `this`. A declaration whose initializer is any other expression, and every nested or assignment pattern, uses the value kind instead. A nullish spread argument in a call reads `X is not iterable (cannot read property undefined)` (`null` for null); a non-iterable non-nullish one reads `Spread syntax requires ...iterable[Symbol.iterator] to be a function`.
- **`Array.from`** with a `Symbol.iterator` that is neither nullish nor callable: `%Array%.from requires that the property of the first argument, items[Symbol.iterator], when exists, be a function`.
- **`Function.prototype.toString`**: script functions give their source; native functions `function name() { [native code] }` with the name they were created with (a later `name` redefinition is not shown; getters read `get size`, symbol-named ones `[Symbol.replace]`); bound functions, Proxies with a callable target and the revoker give `function () { [native code] }`; a Proxy with a non-callable target throws TypeError. The `at` frame of a call on a Proxy receiver names the type `Proxy`.

### Weak-reference collection points

Every `WeakRef` and `FinalizationRegistry` target is held strongly until a collection point; only there do unreachable targets die. Two things create one:

- A script calling `$262.gc()` (the conformance host).
- The host, through `IJsHost.WantsWeakCollection()`. The engine asks it once per job-queue drain (`Agent.RunJobs`, which ends every `Evaluate`, `EvaluateModule` and `RunJobs` entry call), after the last job and the idle hook, and only if a weak target has ever been created. On true it runs a collection point, then drains any cleanup jobs that queued. The interface default is false, so a custom host that does nothing keeps the old behaviour.

`DefaultHost` (so the CLI too) answers true once `GC.GetTotalMemory(false)` has risen by `WeakCollectionGrowthBytes` (protected virtual, default 8 MiB) above the lowest level seen at an earlier question; after a yes it measures again from the next level. A long run that creates weak targets therefore releases them as memory grows and holds them while it does not allocate. The figure is the host's, so it lives in `HostDefaults.cs`.

Replay: the question is not logged. A yes is followed by the usual `Weak` event (written by the collection point itself, with the dead ids), and nothing but the tag of the next entry call follows a drain, so a replaying host answers yes exactly when the next event is `Weak`. The log format is unchanged (version 1).

`EngineLimits` (public, frozen): `Fuel` (default 4e9 steps), `MaxAllocatedBytes` (2 GiB), `WallClockMs` (none), `MaxCallDepth` (10,000, catchable RangeError), `MaxNativeDepth` (120, uncatchable), `MaxStringLength`. A new limit is a new property with a default.

## Tests for a built-in area

Scripts run through `ScriptEngine.Evaluate`. Write a handful of xUnit tests under `tests/OneEcma.Tests/` for behaviour that could plausibly break, and run the matching test262 directory through `tools/OneEcma.Test262` (see `tools/test262/`). Failures you do not fix go into `tools/test262/documented-engine.txt` under your area name, with the reason.
