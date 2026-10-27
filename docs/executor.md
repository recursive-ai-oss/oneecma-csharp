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
- Traced run (bytecode): `Run<TTrace>` with `TTrace : struct, ITracer`; `OnOp(frame, instruction, pc, stack, sp)` is called
  before every instruction. `NoTrace` is empty and inlined away. `RecordingTrace` acts on the stores only (`InitSlot`,
  `StoreSlot`, `StoreSlotChecked`, `InitGlobalLexical`, `StoreGlobal`, `DefineField`): the compiler keeps a side table
  (`FunctionCode.ValueSpans`, instruction index to the span of the value written) and the tracer records, per binding
  (environment and slot, or global name) and per object-literal property, the span the value was written from; an object
  that is stored again keeps the chain of earlier places (cap 8). The explainer (`Diagnostics/Explain.cs`) reads that
  state and the live environments of the failing frames (captured on `StackFrameInfo` while the error is created) to build
  labels, "did you mean" and locals. It reads data properties only and never runs script.
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


## Call frames and scope environments are reused

The engine (not the study above) reuses the objects a plain call allocates, because a recursive kernel otherwise allocates
about 500 bytes per call (a `Frame`, its stack array, the scope `Env` and its slot array).

- **Frames.** When a function returns normally, `Op.Return` hands the finished `Frame` to `Frame.Recycle`, which puts it on a
  free list kept on its `FunctionCode` (up to 64 per function, so recursion to that depth allocates nothing). `PrepareFrame`
  takes from that list through `Frame.Obtain`. The stack array is cleared on release, so a reused frame starts with every
  local `undefined` exactly like a new one.
- **Environments.** `PushScope` takes an `Env` from a free list on its `ScopeInfo` (`Env.Obtain`); on return, `Env.Release`
  puts back the environments the call created, from the innermost outwards, stopping at the first one that was captured.
- **What is never reused.**
  - generator, async and module frames (they are suspended and resumed);
  - entry frames (script, eval, calls from native code);
  - eval frames, which run in their caller's environment and so do not own it;
  - a frame whose `Escaped` flag is set: creating an arrow function stores its defining frame (the arrow reads `this`,
    `new.target` and `super` through it), so the frame and its `this` frame are marked;
  - an `Env` whose `Captured` flag is set: the constructor of `ScriptFunction` and the creation of a mapped `arguments` object
    mark the environment and every ancestor. Holding an environment holds its parents, so the invariant is "an ancestor of a
    captured environment is captured".
  - frames that leave by an exception, and the frame of a tail call (its arguments still sit in the old frame's stack): they are
    simply left to the collector.
- **Traced runs** (`RecordingTrace`) never recycle, because the explainer keeps environments and frames alive for the report.
- Anything new that stores a `Frame` or an `Env` beyond the call must set `Escaped` / call `MarkCaptured`.

Measured effect on `fib(25)` (about 243,000 calls): allocation 139 MB to 0.3 MB per run (what remains is realm setup), and 42 ms to 27 ms.

BenchmarkDotNet 0.15.8, .NET 10.0.13, Jint 4.17.0, AMD Ryzen 9 7950X3D (16 cores, 32 threads), machine
load average 4 to 12 from other jobs (so the times are indicative; the closures, loops and qsort rows were run again, twice each
way, to get past noise). Each run builds a fresh engine, so setup, parse and compile are inside the number. "Before" is the
same bench project on the commit before this change.

| row | before ms | before alloc | after ms | after alloc | Jint ms | Jint alloc |
|---|---:|---:|---:|---:|---:|---:|
| fib25 | 42.1 | 139,216 KB | 27.5 | 307 KB | 31.4 to 38.2 | 42 KB |
| loops | 93.8 (re-run 87.2, 85.2) | 757 KB | 85.8 (re-run 87.2, 107.8 noisy) | 298 KB | 63.8 to 68.9 | 91,551 KB |
| props | 57.6 | 768 KB | 58.6 | 308 KB | 57.6 to 60.3 | 42,752 KB |
| closures | 91.9 (re-run 92.1, 97.3) | 153,546 KB | 111.9 noisy (re-run 78.2, 76.5) | 5,310 KB | 39.9 | 5,412 KB |
| strings | 6.41 | 4,504 KB | 6.15 | 4,044 KB | 3.14 | 4,568 KB |
| vectors | 102.6 | 247,661 KB | 65.2 | 28,452 KB | 91.9 to 100.4 | 69,873 KB |
| calc | 128.8 | 312,418 KB | 88.6 | 800 KB | 117.9 to 120.2 | 7,120 KB |
| qsort | 93.6 (re-run 105.0, 94.9) | 23,159 KB | 122.1 noisy (re-run 93.2, 92.1) | 2,396 KB | 117 to 152 | 28,579 KB |
| throws | 12.3 | 28,894 KB | 9.8 | 23,357 KB | 33.9 to 36.3 | 14,630 KB |
| startup | 0.315 | 742 KB | 0.116 | 282 KB | 0.002 | 10 KB |

No row is slower beyond its measured error; the rows that look slower in one run (closures, qsort, loops) have error bars of
20 to 50 ms there and are faster or equal when repeated.

## Strings, closures and one-step loop sequences

The `strings` and `closures` kernels sat at about 2x Jint after frame reuse. What cost the time, in the order it was found:

- **`Array.prototype.push` listed the keys of every prototype on each call.** The dense fast path asks whether an index key anywhere on
  the prototype chain could intercept the new element; it answered by collecting the keys of `Array.prototype` and
  `Object.prototype` (about 60) per push. `Shape.DigitKeys` now counts keys that start with a digit, kept up to date as keys are
  added and removed, so the answer is a field read per prototype. 300,000 pushes: 101 ms to 37 ms.
- **Function objects built their `prototype` object eagerly.** It is now created on first use (see "Deferred own properties" in
  `runtime.md`); a closure that is only called costs a function object and an exactly sized three-slot array. Creating 300,000 closures:
  57 ms to 42 ms. Built-in functions also get exactly sized slot arrays (realm creation allocates 15 KB less).
- **Rope flattening** fills the result from the end and follows the left spine without a stack; `String.prototype` method lookups on a
  string primitive are cached per site, and `length` does not flatten a rope; `string + number` skips `ToPrimitive`.
- **Loop sequences.** Variables live in scope environments, so a counting loop spent most of its time in `LoadSlot`/`PushInt`/`Lt`/
  `JmpIfFalse`-style instruction chains. The compiler marks the first instruction of three sequences (`Instr.Fused`): a slot compared
  with an integer constant and a branch, `a++` as a statement, and `a += n` as a statement. The untraced dispatch loop runs a marked
  sequence in one step when the slot holds a number and otherwise executes the instructions one by one; the instructions after the
  first are unchanged and remain jump targets, so positions, handlers and behaviour are the same and a traced run never looks at the
  mark. An empty 1,000,000-iteration loop: 50 ms to 13 ms.

One defect turned up by the replay fuzzer is fixed in `Agent`: describing a memory limit lists frame locals, previewing a rope local
flattens it, and flattening is charged to the budget that had just run out, which raised the limit again inside its own description
until the host stack was gone. Charges made while a limit is being described are recorded but do not raise.

Measured in one locked job on the benchmark machine (AMD Ryzen 9 7950X3D, 32 threads; load average 8 to 11 from other projects'
unlocked jobs before, during and after; .NET 10.0.402, Jint 4.17.0, BenchmarkDotNet 0.15.8, same bench project and command for
both trees). Each tree ran twice, interleaved; the "before" tree is the commit before this change. Times are means in ms, and
the two runs of a tree agreed within 3% except where noted.

| row | before ms | after ms | Jint ms | after / Jint | alloc before to after, KB |
|---|---:|---:|---:|---:|---|
| fib25 | 28.6 | 24.7 | 33.7 | 0.73 | 313 to 297 |
| loops | 85.1 | 53.7 | 65.8 | 0.82 | 303 to 287 |
| props | 59.1 | 49.0 | 58.9 | 0.83 | 313 to 298 |
| closures | 80.2 | 42.5 | 40.5 | 1.05 | 5,315 to 5,268 |
| strings | 6.78 | 3.81 | 3.24 | 1.18 | 4,050 to 3,010 |
| vectors | 67.1 | 63.4 | 94.8 | 0.67 | 28,457 to 28,442 |
| calc | 88.7 | 84.5 | 120.1 | 0.70 | 806 to 790 |
| qsort | 94.5 | 79.2 | 114.2 | 0.69 | 2,402 to 2,387 |
| throws | 10.8 | 9.4 | 34.4 | 0.27 | 23,362 to 23,347 |
| startup (realm) | 0.111 | 0.107 | 0.002 | 60 | 287 to 272 |

No row is slower. Startup time is unchanged within noise; only its allocation moved. An earlier locked run of the tree before
the loop-sequence change gave closures 48.5 and 50.5 ms and strings 5.3 ms, so the first three changes account for most of the
closures row and the loop sequences for most of the strings row.

## Realm creation

A new realm defines roughly 1,500 built-in functions. Two costs dominated it and are fixed in `Shape`:

- A shape with more than 8 keys built a key index (a dictionary) the first time it was looked up. Defining a built-in
  prototype adds keys one at a time and checks each shape once, so every intermediate shape built an index (quadratic work and
  allocation). A large shape now walks its key chain for its first four lookups and builds the index only when it is looked up
  repeatedly.
- Every shape allocated a transition dictionary for its successors. Most shapes have exactly one, so the first successor is kept in a
  field and the dictionary is created for a second.

Realm creation went from about 210 us and 737 KB to about 84 us and 276 KB (timing loop of 2,000 engines in one process). Making
intrinsics lazy was considered and not done: the remaining cost is spread over all areas, and a lazy global would need a check on
every global and slot read to stay unobservable.

## Long-lived process

`dotnet run -c Release --project bench/OneEcma.Benchmarks -- longrun [rounds]` runs every kernel in sequence in one process, with a
fresh engine per run and then with one engine reused, and prints a column per kernel per round. Run in this form, in forward
and reverse kernel order, with tiered PGO on and off, and with each kernel alone, times were flat after the first (JIT) round and
equal to the isolated runs, so there is no slowdown that grows with process age. A 24-round run on the benchmark machine stayed
within about 4% from round 1 to round 23 for every kernel, with a Jint control column in the same process that moved with the
machine (a short step of 10 to 15% in all columns during an earlier run coincided with other jobs on the machine). The commit
before this change behaved the same way when run in these forms, and so did the earlier engine core, so an earlier report of
later kernels being slower in one process was a measurement artefact. The record log is bounded (8 MiB), and a reused
engine's log grew by 3 to 26 KB per kernel run with no change in time.

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
