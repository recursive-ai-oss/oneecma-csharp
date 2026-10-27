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

`IJsHost` (public, frozen) is the only way out. A built-in that needs the time, randomness, the locale, the zone, a module or output asks `Agent.Current.Host`: `CurrentTimeMs()` (Date), `MonotonicMs()` (limits, `performance.now`), `NextRandom()` (`Math.random`), `DefaultLocale`, `TimeZoneId`, `ResolveModule`, `Print`, `JobEnqueued`, `PromiseRejectionTracker`, `InitializeImportMeta`. Never call `DateTime.Now`, `Stopwatch`, `Random` or the environment yourself; `HostDefaults.cs` is the one file that may. Anything new that is nondeterministic becomes a new `IJsHost` member with a default in `DefaultHost` (additive, so existing hosts keep compiling) and a line here.

Promise jobs go through `Agent.EnqueueJob(Action)`; never run a reaction synchronously. `WeakRef` and `FinalizationRegistry` observations are scheduled by the engine through the job queue, never by GC timing.

`EngineLimits` (public, frozen): `Fuel` (default 4e9 steps), `MaxAllocatedBytes` (2 GiB), `WallClockMs` (none), `MaxCallDepth` (10,000, catchable RangeError), `MaxNativeDepth` (120, uncatchable), `MaxStringLength`. A new limit is a new property with a default.

## Tests for a built-in area

Scripts run through `ScriptEngine.Evaluate`. Write a handful of xUnit tests under `tests/OneEcma.Tests/` for behaviour that could plausibly break, and run the matching test262 directory through `tools/OneEcma.Test262` (see `tools/test262/`). Failures you do not fix go into `tools/test262/documented-engine.txt` under your area name, with the reason.
