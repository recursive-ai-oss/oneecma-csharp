# `err.stack` and the frames of a report

`err.stack` follows V8's format and the engine matches Node's output on the differential corpus (`testdata/errors/cases.txt`, 213 snippets; `expected.json` is what Node v24.22.1 printed, recorded by `tools/errors-diff/record.js`). The report's `= stack:` line and the JSON `stack` array come from the same captured frames, so they never disagree with `err.stack`.

## Format

```
<Name>: <message>
    at <frame>
    at <frame>
```

- The header is `Name: message` (just `Name` when the message is empty). It is formatted when `stack` is first read, so a `name` or `message` set after construction shows, as in V8.
- At most `Error.stackTraceLimit` frames (default 10) are kept, innermost first, captured when the error object is created.
- A frame line is one of:
  - `at fn (file:line:col)` for a named function;
  - `at Type.method (file:line:col)` for a method call whose receiver has a class, where `Type` is the name of the constructor the receiver inherits (`String`, `Number`, `Boolean`, `Symbol`, `BigInt`, `Function`, a class name, `Object` for a plain object unless it has a `Symbol.toStringTag`);
  - `at new Fn (file:line:col)` for a construct call;
  - `at file:line:col` for top-level code and anonymous functions;
  - `at async fn (file:line:col)` for an async function that is suspended at an `await` behind the running frames;
  - `at Array.map (<anonymous>)` for a built-in function between script frames;
  - `at eval (eval at f (file:line:col), <anonymous>:line:col)` for code made by `eval` or `new Function`.
- The error constructors, `Array` and `Proxy` are left out of the trace of the error they create; frames of a subclass constructor are skipped up to the `new.target` of the construction.
- Columns count UTF-16 code units, 1-based.

## Positions

V8 reports a position inside the expression, not the start of the statement:

| Construct | Position |
|---|---|
| property read or method call `a.b` / `a.b()` | the property name `b` |
| call `f()` | the start of the callee `f` |
| call of another expression `(x)()`, `a()()` | the operator that follows the callee, skipping `)` and comments |
| binary operation, assignment, compound assignment | the operator |
| `new C()` | the `new` keyword |
| tagged template | the template |
| `yield` | the start of its operand |
| destructuring a value that cannot be unpacked | the first property key of the pattern |

Each bytecode instruction carries one such position (`FunctionCode.Pos`); the frame's span (used for the report's underline) is the span of the failing expression and is kept separately.

## Frames of a report

- Built-in frames (`Array.map`) appear in `err.stack` but not in the report; the report points at the script code that called them.
- A repeated frame (same function, source and line, consecutive) collapses to `fn (file:line) ×N` in the report line; `err.stack` and the JSON keep every frame.
- Top-level code is `<top-level>` in the report line and has `IsTopLevel` set; an anonymous function is `<anonymous>`.
- Async frames: when an async function is suspended at an `await` of a promise made by another async function, the awaiting function is stitched behind the bottom frame (`async main`). Only direct awaits are stitched.
- Proper tail calls are off by default so the caller stays in the trace (as in V8); `ScriptEngineOptions.ProperTailCalls` turns them on (the test262 runner does).

## Differences from Node (documented, `testdata/errors/documented.txt`)

1. The frame at the call-depth limit differs: V8's limit depends on native stack left, ours is a fixed depth (10,000).
2. A derived constructor that returns without calling `super()`: V8 points at the parameter list, the engine at the body.
3. Class field initializers run inline (frame named after the field); V8 shows a synthetic `<instance_members_initializer>` frame.
4. `new Function` with a parameter list that closes early: V8's message is "Arg string terminates parameters early".
5. `await Promise.all(...)`: V8 stitches an `async Promise.all (index n)` frame; the engine does not.
6. A `for await` loop is reported at its binding by V8, at the `for` statement by the engine.
7. Assigning to `undefined` in strict code names the receiver `[object Object]` in V8's vm context, `#<Object>` in the engine.
