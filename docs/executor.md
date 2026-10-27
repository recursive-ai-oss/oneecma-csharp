# Executor shape: closure tree or bytecode

> **Status.** The engine's executor is a *stack* bytecode machine with an explicit frame stack and the 16-byte `JsValue`
> described here; it was built after this study and replaced the spike, which has been removed. Its speed
> against Jint is measured by `bench/OneEcma.Benchmarks` (`EngineBenchmarks`). The text below is kept for the reasoning behind
> the frame, depth, suspend and tracing decisions, which the engine follows; its timings are those of the study.


Result of a measured spike (a throwaway study project). Two executors were built over the real syntax tree and
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
| (e) no IL, safe code, AOT | Fine. | Fine. The tracer generic is a struct type argument known at compile time, so NativeAOT compiles both instantiations (an AOT check app). |

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

Tests (48 cases): the benchmark scripts against values computed in C#;
semantics checked on both executors (closure capture per iteration, TDZ, const assignment, error messages with the
callee text taken from the span, evaluation order, `try` through frames); fuel limits on four loop shapes and on
recursion; a 100,000-term sum and a 100,000-link member or call chain on a 256 KB thread; recursion depth on a thin
thread; the traced run.

## Value representation

`readonly struct JsValue { object? Ref; double Num; }`, 16 bytes. A number is `(NumberTag, d)`; undefined is
`Ref == null` so zeroed registers and slot arrays are already correct; null, true, false and the TDZ marker are tag
objects; strings are `string` or `Rope`; objects are `JsObject`. Numbers never allocate. Safe code only, no NaN boxing.

Measured on the same arithmetic loop (5,000,000 iterations, four operations each; BenchmarkDotNet, in-process, one run,
subject to the load noted below, ratios are robust to it):

| representation | mean | vs raw `double` | allocated |
|---|---:|---:|---:|
| raw `double` (floor) | 6.1 ms | 1.0x | 60 B |
| 16-byte struct (reference tag + double), the spike's choice | 44.5 ms | 7.3x | 768 B |
| heap object per number (`sealed class` hierarchy) | 197.9 ms | 32.5x | 480 MB |
| boxed `double` in an `object` slot | 121.4 ms | 19.9x | 480 MB |

The struct is 3 to 4.5 times faster than either heap form and allocates nothing; the heap forms would need a small-integer
cache and escape tricks to approach it, and still allocate for every non-integer result.

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
  by 35 to 45% on the scripts tried (fib 76 to 44 ms, loops 92 to 57 ms, informal local timing). It costs
  about 10 ms of JIT up front on a `1 + 1` script. NativeAOT has no such phase.
- The closure tree's many small methods stay in unoptimised code until the runtime's tiering delay passes (100 ms), so
  one-shot scripts shorter than that run unoptimised; marking the loop statements as aggressively optimised did not help.

## Measured

Sources: `SpikeScripts` (fib(25); 1,000x1,000 nested loops; property get/set; closures, `map`/`filter` written in script
and a counter factory; string building with `charCodeAt`; a vector class with `new` and prototype methods; a
recursive-descent calculator over a string; quicksort on 40,000 numbers; 10,000 throw/catch). Each iteration creates a fresh
realm or engine from an already parsed program, so realm set-up, compilation and execution are inside the number.
Ratios are mean time divided by Jint's. Allocation is managed bytes per operation.

### Steady state (BenchmarkDotNet 0.15.8, out-of-process, 3 warm-up and 10 measured iterations)

| script | Jint mean (ms) | Jint alloc | A closure mean (ms) | A alloc | A / Jint | B bytecode mean (ms) | B alloc | B / Jint | B / A |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| startup | 0.001 | 8 KB | 0.002 | 12 KB | 1.43 | 0.002 | 17 KB | 1.83 | 1.28 |
| fib25 | 30.158 | 37 KB | 4.874 | 25 KB | 0.16 | 7.962 | 25 KB | 0.26 | 1.63 |
| loops | 66.312 | 89.4 MB | 12.616 | 19 KB | 0.19 | 18.492 | 24 KB | 0.28 | 1.47 |
| props | 55.497 | 41.7 MB | 16.269 | 24 KB | 0.29 | 22.380 | 34 KB | 0.40 | 1.38 |
| closures | 37.566 | 5.3 MB | 10.509 | 4.8 MB | 0.28 | 15.695 | 4.8 MB | 0.42 | 1.49 |
| strings | 2.941 | 4.5 MB | 2.565 | 3.7 MB | 0.87 | 2.810 | 3.7 MB | 0.96 | 1.10 |
| vectors | 88.959 | 68.2 MB | 15.202 | 24.4 MB | 0.17 | 23.285 | 24.5 MB | 0.26 | 1.53 |
| calc | 112.767 | 6.9 MB | 21.578 | 431 KB | 0.19 | 35.341 | 446 KB | 0.31 | 1.64 |
| qsort | 107.468 | 27.9 MB | 22.868 | 2.0 MB | 0.21 | 23.728 | 2.0 MB | 0.22 | 1.04 |
| throws | 31.840 | 14.3 MB | 17.621 | 6.0 MB | 0.55 | 1.400 | 1.3 MB | 0.04 | 0.08 |

The `startup` row (`1 + 1`) is realm construction: 2 microseconds for both executors, against Jint's 1.

An earlier run of the same harness (before the dispatch loop was marked aggressively optimised and the frame pools
became lazy) gave ratios to Jint within 0.1 of these on every script (closure tree 0.15 to 0.97, bytecode 0.21 to 1.01,
except `throws` 0.52 and 0.04 and `startup`), so the conclusions do not depend on the noise below.

### Cold, one script per fresh process (parse included, median of 5, ms and peak resident set in MB)

| script | Jint ms | Jint peak RSS MB | A ms | A RSS MB | B ms | B RSS MB |
|---|---:|---:|---:|---:|---:|---:|
| startup | 64 | 47 | 15 | 33 | 28 | 40 |
| fib25 | 255 | 50 | 59 | 36 | 44 | 43 |
| loops | 441 | 98 | 173 | 36 | 57 | 43 |
| props | 324 | 95 | 126 | 37 | 90 | 43 |
| closures | 318 | 56 | 105 | 42 | 63 | 48 |
| strings | 117 | 55 | 40 | 40 | 47 | 46 |
| vectors | 415 | 98 | 118 | 61 | 78 | 67 |
| calc | 449 | 60 | 190 | 38 | 119 | 43 |
| qsort | 425 | 80 | 169 | 38 | 82 | 45 |
| throws | 195 | 72 | 76 | 51 | 44 | 44 |

A fresh process pays JIT time: the closure tree runs most of a short script in unoptimised code, the bytecode loop (marked
aggressively optimised) pays about 10 ms up front and then runs optimised. Under NativeAOT there is no such phase.

### Reachable call depth (host thread stack, `MaxDepth` raised to 1,000,000)

| host stack | closure tree | bytecode |
|---|---:|---:|
| 1 MB | 854 | 400,000 (probe cap) |
| 8 MB | 7,689 | 400,000 (probe cap) |
| 64 MB | 232,542 | 400,000 (probe cap) |

Front-end acceptance of 100,000-link chains on a 256 KB thread: binary operators, member accesses and calls parse; a chain of
100,000 nested assignments stops with the parser's own nesting limit.

### Method and caveats

- Machine: AMD Ryzen 9 7950X3D, 16 cores / 32 threads, Linux x86-64, .NET SDK 10.0.402, runtime 10.0.13, BenchmarkDotNet
  0.15.8, Jint 4.17.0, Release builds with default tiering and dynamic PGO. The runs were serial and single-threaded.
- Load: the machine was shared and other jobs ran during every run (load average between 4 and 14 on 32 threads), so
  these numbers are indicative, not publishable; repeat on an idle machine before quoting absolute times. Reproduce with
  `bench/OneEcma.Benchmarks` (`--filter '*ExecutorBenchmarks*' '*ValueRepBenchmarks*'`, then `cold <jint|closure|bytecode> <script>`
  five times per pair).
- Within-run noise is visible in the differences between the two runs (up to 25% on one executor for a script); the
  Jint column moved by a similar amount, which is why only ratios are used for conclusions.
- The closure tree has pooled frames, fused register/constant nodes and inline caches; the bytecode has inline caches,
  fused compare-and-jump and window-passed arguments. Neither is tuned to the end.


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
