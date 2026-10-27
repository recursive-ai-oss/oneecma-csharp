# Collections, weak references, Reflect and Proxy

Normative notes for the places where ECMA-262 leaves room or where the engine chooses a mechanism.

## Map and Set

- Keys are compared with SameValueZero; `-0` is stored as `+0`. Iteration order is insertion order.
- The entry list is a linked list. A deleted entry is marked removed and keeps its back link; an iterator parked on a
  removed entry walks back to a live entry and continues after it. So `delete`, `clear` and re-adding during iteration
  behave as the specification's "empty slot" list does (an entry deleted and re-added is visited again), and no step is
  O(n). `clear()` marks every entry removed; entries added afterwards are visited by parked iterators.
- `Map.groupBy` and `Object.groupBy` share one grouping helper (`Builtins.GroupBy`): first-seen key order, the callback
  result goes through ToPropertyKey for objects and `-0` becomes `+0` for maps, the iterator is closed if the callback
  or the key coercion throws.
- The ES2025 Set methods follow the algorithm text step by step, including GetSetRecord's observable order (`size`,
  `has`, `keys`) and the size comparison that picks which side is iterated. Loops over `this` walk the live list.
- ES2026 upsert: `Map.prototype.getOrInsert`, `getOrInsertComputed`, and the WeakMap versions.

## Weak references

`WeakMap` and `WeakSet` use `ConditionalWeakTable`, an ephemeron table: a value that refers to its own key does not keep
the key alive, and nothing leaks. Their contents cannot be observed, so the collector may act on them whenever it likes.

`WeakRef` and `FinalizationRegistry` are observable, so they must not depend on when the .NET collector runs:

- A target is held strongly from the moment it is registered. Nothing dies by itself.
- A **collection point** is a host decision. The conformance host's `$262.gc()` is one; an embedding host reaches it
  through the same engine operation. At a collection point the engine releases every target that is not in the kept list
  to the .NET collector, runs a full collection, marks the targets that were reclaimed as dead, pins the survivors again,
  and queues one cleanup job per registry that now has dead cells.
- Kept objects (9.10): a `WeakRef` construction or successful `deref()` keeps the target until the current synchronous
  run ends. `ClearKeptObjects` runs when a script finishes and after every job on the queue, and from
  `$262.clearKeptObjects()`.
- Cleanup callbacks run as ordinary jobs on the engine's job queue, one job per registry, dead cells in registration
  order. A callback that throws leaves the remaining dead cells to a follow-up job.
- Record and replay: each weak target gets an id in creation order. The host interface has two default members.
  `WeakTargetsCollected(ids)` is called at every collection point with the ids found dead (ascending); a recording host
  logs them. `ReplayWeakCollection()` returns, for a replaying host, the recorded ids; the engine then does not touch
  the collector and applies exactly that outcome. A run therefore reproduces even though .NET reachability alone would
  not.
- Limitation: references the interpreter itself still holds (a finished frame's stack array that a running frame
  references, a value in a live local) keep a target reachable. Garbage built by one script run and collected from a
  later run or job is reliably dead.
- The engine never calls the collector on its own; a host that creates many `WeakRef` targets should request collection
  points. Targets are held strongly until then.

## Proxy

- Every internal method follows ECMA-262 10.5 including all invariant checks. A revoked proxy throws a TypeError naming
  the operation (`Cannot perform 'get' on a proxy that has been revoked`). `IsArray` looks through proxies iteratively.
- Callable and constructor status are fixed when the proxy is created from the target, and stay after revocation
  (`typeof` of a revoked function proxy is `"function"`).
- Nesting limit: at most 512 proxy operations may be nested on the host stack; the next throws
  `RangeError: Maximum call stack size exceeded`. A chain of 100,000 proxies therefore ends in a RangeError, never in a
  host stack overflow. Traps implemented by script are bounded additionally by the call and native-depth limits.
- `Object.prototype.toString` on a revoked proxy says `Cannot perform 'Object.prototype.toString' on a proxy that has
  been revoked`.
- When a trap returns false and the caller must throw (strict assignment, strict `delete`, `Object.defineProperty`,
  `Object.setPrototypeOf`, `Object.preventExtensions`), the message is `'<trap>' on proxy: trap returned falsish for
  property '<key>'`, as V8 prints it.

## Messages learned from Node v24.22.1

Each wording below was observed by running Node and recording the output.

| Situation | `err.message` |
|---|---|
| `Map()` and the other collection constructors without `new` | `Constructor Map requires 'new'` |
| Wrong receiver | `Method Map.prototype.get called on incompatible receiver #<Object>` (`get Map.prototype.size` for the accessor) |
| `new Map(5)` | `number 5 is not iterable (cannot read property Symbol(Symbol.iterator))` (`object`, `function`, `symbol`, `boolean true` for other kinds) |
| `new Map([1])` | `Iterator value 1 is not an entry object` |
| Replaced `set`/`add` not callable | `'1' returned for property 'set' of object '#<Map>' is not a function` |
| `forEach(1)` | `number 1 is not a function` |
| `Map.groupBy([], 1)` | `1 is not a function`; `Map.groupBy null` gives `Map.groupBy called on null or undefined` |
| Bad WeakMap / WeakSet key | `Invalid value used as weak map key` / `Invalid value used in weak set` |
| `new WeakRef(1)` | `WeakRef: invalid target` |
| `FinalizationRegistry` | `FinalizationRegistry: cleanup must be callable`, `FinalizationRegistry.prototype.register: invalid target`, `FinalizationRegistry.prototype.register: target and holdings must not be same`, `Invalid unregisterToken ('1')` |
| Set methods | `Set.prototype.union argument must be an object`, `The .size property is NaN`, `'-1' is an invalid size` (RangeError), `string "has" is not a function`, `Result of the keys method is not an object` |
| Reflect with a non-object target | `Reflect.get called on non-object` |
| `Reflect.apply(1)` | `Function.prototype.apply was called on 1, which is a number and not a function` |
| `new Proxy(1, {})` | `Cannot create proxy with a non-object as target or handler` |
| Proxy trap not callable | `'1' returned for property 'get' of object '#<Object>' is not a function`; for `apply` and `construct`: `(intermediate value) is not a function` |
| Invariant violations | `'get' on proxy: property 'x' is a read-only and non-configurable data property on the proxy target but the proxy did not return its actual value (expected '1' but got '2')` and the sibling texts in `Builtins/Proxy.cs` |

The upsert methods (`getOrInsert`, `getOrInsertComputed`) are not in Node v24, so their errors reuse the neighbouring
wording above.

## Known differences from Node

- A `with` statement over a proxy calls `has` again when it reads the binding (`GetBindingValue`, 9.1.1.2.6) and when it
  assigns (`SetMutableBinding`); Node skips the second `has` on reads. The specification's order is followed.
