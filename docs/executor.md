# Executor shape: closure tree or bytecode

Result of a measured spike (`spike/OneEcma.ExecutorSpike`). Two executors were built over the real syntax tree and
benchmarked against each other and against Jint 4.17.0 on the same sources.

## Recommendation

**Register bytecode with an explicit frame stack (shape B).** Speed does not decide it: both shapes are several times
faster than Jint on every script except string building, so the "no worse than about 2x Jint" floor is met with a large
margin (the closure tree is 1.0 to 1.6x faster than bytecode on call-heavy code; bytecode is 10 to 15x faster on
throw/catch). What decides it is the other requirements:

| Requirement | Closure tree (A) | Bytecode (B) |
|---|---|---|
| (a) no unbounded host-stack recursion | Every JavaScript call costs about 1 KB of host stack. Reachable depth was 854 calls on a 1 MB stack, 7,689 on 8 MB, 232,542 on 64 MB. The depth limit is therefore a property of the thread the host happens to call from, which breaks "the limit yields the same diagnostic every time". Deep expression trees recurse the same way and need a guard. | JavaScript calls do not touch the host stack: 400,000 deep on a 1 MB thread (the probe's cap). The limit is a counter and is identical on every host. Only native-to-script re-entry (a `sort` comparator) uses host stack, and that is counted. |
| (b) generators, async, await | No cheap way. Needs a second, resumable form of every node, or a lowering to a flat form, which is a bytecode compiler anyway. | A suspended frame is a copy of its register window plus a pc (see "Suspend"). Natural. |
| (c) fuel | A field decrement on loop back-edges and calls. | The same, in the back-edge jump instructions and in calls. No difference. |
| (d) lean run with spans only, traced replay with the same semantics | Hooks mean a second tree built by a decorating factory, and fused nodes (a register compared with a constant) have no child nodes to hook, so the traced tree must disable the fusions: two code paths that must be kept identical. Frames are pooled, so locals vanish on return. | One dispatch loop generic over a tracer struct. The lean instantiation has empty inlined hooks. The pc maps to a span through a table. Frames and registers are heap data that stay intact until overwritten. |
| (e) no IL, safe code, AOT | Fine. | Fine. The tracer generic is a struct type argument known at compile time, so NativeAOT compiles both instantiations (`spike/OneEcma.ExecutorSpike.AotCheck`). |

The cost of choosing B: a compiler with register allocation, jump patching and compile-time environment depth, a
dispatch loop that must be kept correct by tests, and 1.0 to 1.6x slower steady-state calls than a closure tree. Nothing
in the measurements suggests that cost matters for the floor.

A hybrid (tree for plain functions, bytecode for generators) is rejected: it doubles the semantic surface the
conformance suite has to cover.

## What was built

All of it is BCL only, safe code, no IL emission.

- Shared: `JsValue`, shapes and inline caches, arrays with a dense store, ropes for string building, a realm with
  intrinsics (`Math`, `String.fromCharCode`, a few array and string methods, `Error`), and an analysis pass that
  resolves identifiers to bindings, decides which bindings are captured and which can be read before initialisation.
- (A) `Closure/`: each AST node becomes an object with `Eval`/`Exec`. Frames are pooled by call depth. Operator chains of
  more than 32 links evaluate with a loop.
- (B) `Bytecode/`: 16-byte instructions `{op, a, b, c}`, a register machine over one shared register stack, a span table,
  a handler table for `try`, per-site inline caches.
- Subset: numbers, strings, booleans, undefined, null; objects with prototype chains; dense arrays; functions, closures,
  arrows, `this`, `new` on constructor functions; `var`/`let`/`const` with TDZ and per-iteration `let` environments;
  `if`/`for`/`while`/`do`, `break`/`continue` (unlabelled); `throw` and `try`/`catch`. Everything else (classes,
  destructuring, spread, generators, async, `finally`, `switch`, labels, getters/setters, regular expressions, `with`,
  `eval`, `arguments`, template literals, optional chaining, `delete`) throws `SpikeUnsupportedException`.
  Deliberate deviations: top-level `var`/function declarations are locals of the program, not global-object properties;
  sloppy `this` is `undefined`; no Annex B function-in-block var copy; number formatting is approximate.

Tests (`tests/OneEcma.Tests/ExecutorSpikeTests.cs`, 48 cases): the benchmark scripts against values computed in C#;
semantics checked on both executors (closure capture per iteration, TDZ, const assignment, error messages with the
callee text taken from the span, evaluation order, `try` through frames); fuel limits on four loop shapes and on
recursion; a 100,000-term sum and a 100,000-link member or call chain on a 256 KB thread; recursion depth on a thin
thread; the traced run.

## Value representation

`readonly struct JsValue { object? Ref; double Num; }`, 16 bytes. A number is `(NumberTag, d)`; undefined is
`Ref == null` so zeroed registers and slot arrays are already correct; null, true, false and the TDZ marker are tag
objects; strings are `string` or `Rope`; objects are `JsObject`. Numbers never allocate. Safe code only, no NaN boxing.

@@VALUEREP@@

Property keys are strings. Objects are a `Shape` (prototype included, so a shape hit implies the prototype) and a slot
array; property sites cache `shape -> slot` for own properties, `shape + holder shape -> slot` one prototype up, and
`shape -> shape` for adding a property. Attributes, accessors, deletion and dictionary mode are not in the spike; they
belong in the shape (flags per key) and in a dictionary-mode fallback.

## Frame and environment layout

- Analysis assigns every binding either a **register** of its function (when no nested function refers to it) or a slot
  in a heap **environment** (`Env { Parent, Slots }`) belonging to its scope. Only scopes with at least one captured
  binding materialise an environment; a closure captures the current `Env` pointer. Variable access is
  `(register)` or `(hops, slot)`, with hops counted at compile time over the scopes that have environments.
- `for (let ...)` with captured bindings copies the environment each iteration before the update expression.
- Bindings that can be read before initialisation (a reference earlier in the text, a reference inside the declarator's
  own initialiser, or any captured `let`/`const`/`class`) are filled with a TDZ marker on scope entry and checked on
  read; all others skip both.
- Bytecode frames: one `JsValue[]` register stack. A call puts the callee, `this` and the arguments in consecutive
  temporaries; the callee's registers start at the first argument, so arguments are passed without copying. Registers
  beyond the arguments are cleared on entry. A `Fr` record (function, pc, base, env, this, return register) is pooled
  per depth and grows on demand. The stack is bounded (64 Mi slots) and checked before growth.

## Fuel, depth and cancellation

- Fuel: `--fuel < 0` on every loop back-edge (the compiler emits distinct back-edge jump instructions, including a
  conditional jump that is always a back-edge) and on every call, script or native. Wall clock and cancellation are
  polled at the same places, every N charges, in the real engine. A `while (!done) {}` shape compiles to a back-edge
  too (tested).
- JS call depth: bytecode compares the frame index with `MaxDepth` at the call instruction. Closure tree: the same
  counter, plus `RuntimeHelpers.TryEnsureSufficientExecutionStack` every 8th call as a host guard, which makes the
  effective limit depend on the host thread.
- Compile-time recursion: the front end bounds nesting at 512 but accepts 100,000-link flat chains of binary
  operators, member accesses and calls (probe output below). Binary and logical chains are walked iteratively in both
  compilers. Member and call chains are not; every pass has a nesting counter (1,000) and a stack-space check that
  ends in a limit exception, tested at 100,000 links on a 256 KB thread. Without the stack-space check the counter
  alone overflowed at 169 levels in a debug build.
- The interpreter loop and the closure tree both rely on the parser having bounded the rest of the nesting.

## Suspend (generators, async, await)

**Bytecode (designed, not built).** A generator or async function owns a `GeneratorState { BcFunc, pc, JsValue[] saved,
Env, This, state }`. Calling it creates the state and runs nothing (generators) or runs to the first `await` (async).
`yield`/`await` instructions copy the frame's register window (NumRegs slots, typically 10 to 40) into `saved`, store
pc and env, pop the frame and return from the running `Run` invocation with a "suspended" result. `next(v)` pushes a
frame at the stack top, copies `saved` in, writes `v` into the register the yield names and continues at the saved pc.
`throw(e)` and `return(v)` resume at the same pc with a mode flag that a `Resume` instruction after each yield checks:
throw raises at that pc (so the handler table applies), return jumps to the function's cleanup path (finally blocks are
compiled as handlers that rethrow or continue a stored completion). `yield*` and `for await` are loops around the same
instructions. Promise reactions are host jobs that resume the state; an async function awaiting records the awaited
promise in its state, which is what stitches async stacks. Cost: a window copy per suspend and resume.

**Closure tree.** The options, none of them good: (1) a resumable second form of each node keeping its progress in
frame-side state slots and re-walking from the root on resume; roughly doubles the node set and slows both forms;
(2) a thread per generator, which breaks determinism and costs a thread per live generator; (3) lowering generator bodies
to a flat basic-block form, which is a bytecode compiler plus the interpreter for it, i.e. the hybrid.

## Replay and tracing

- Lean run: spans only. Bytecode keeps the pc-to-span table (two ints per instruction); the closure tree keeps start and
  end on each node.
- Traced run (bytecode): `Run<TTrace>` with `TTrace : struct, ITracer`; `After(fn, pc, op, stack, base, a)` is called
  once per instruction. `NoTrace` is empty and inlined away; `RecordingTrace` records `(span, value)` for every
  value-producing instruction (test: the value behind the span `o.a` is `1,2`). Provenance chains, frame locals and
  did-you-mean are built from this stream and from the live `Fr` records and register stack, which remain intact after an
  unwind until overwritten; the report is built at the throw point, before the handler search discards anything.
- The traced run is a replay: the record log supplies clock, randomness and host results, so the same instructions run in
  the same order. Because both instantiations come from one source, they cannot drift apart.
- Closure tree: a decorating node factory for the second tree; fused nodes need to be disabled in it (see the table).

## Host-JIT behaviour worth carrying into the real engine

- The dispatch loop is one very long-running method. Without `MethodImplOptions.AggressiveOptimization` it spends its
  first calls in unoptimised code and on-stack replacement; with it, a single cold run of the bytecode executor dropped
  by 35 to 45% on the scripts tried (fib 76 to 44 ms, loops 92 to 57 ms, local timing before the worker runs). It costs
  about 10 ms of JIT up front on a `1 + 1` script. NativeAOT has no such phase.
- The closure tree's many small methods stay in unoptimised code until the runtime's tiering delay passes (100 ms), so
  one-shot scripts shorter than that run unoptimised; marking the loop statements as aggressively optimised did not help.

@@MEASURED@@

## Open points for the engine core

- Native-to-script re-entry (`Array.prototype.sort` calling a script comparator, `Proxy` traps, getters) re-enters `Run`
  with a counter; the real core needs a cap on that nesting and a decision for what a generator does when resumed inside
  a re-entry.
- Member and call chains of 100,000 links are accepted by the parser; the bytecode compiler can walk them with a loop
  (not done in the spike), the closure compiler cannot without flattening.
- `JsValue` at 16 bytes costs write barriers on every register store; a split register file (numbers in a `double[]`,
  references in an `object[]`) is a possible later optimisation that needs the benchmark to justify it.
- Superinstructions and cached-global shortcuts were only done for compare-and-jump; the call path (callee lookup through
  an environment slot, window clearing) is where bytecode loses to the tree and is the first place to tune.
