# Record log (format version 1)

A record log holds everything a run took from outside the engine, in order, so the run can be replayed in a fresh engine and give the same values, exceptions and report bytes. Recording is on by default (`ScriptEngine.RecordLog`, or pass your own through `ScriptEngineOptions.Log`); it costs a few appends per host interaction and nothing per instruction.

## What is recorded

| Event | Recorded when | Payload |
|---|---|---|
| `Eval` (1) | `ScriptEngine.Evaluate` (outermost call) | source, file name |
| `Module` (2) | `EvaluateModule` | specifier |
| `Jobs` (3) | `RunJobs` | none |
| `Clock` (4) | `IJsHost.CurrentTimeMs` | kind byte; 0 = zigzag varint difference from the previous reading, 1 = raw double |
| `Mono` (5) | `IJsHost.MonotonicMs` read by script (`performance.now`) | double. Limit polling is not recorded: how often it runs depends on fuel, not on the script. |
| `Random` (6) | `IJsHost.NextRandom` | double |
| `ZoneId` (7), `Locale` (8) | first read of `TimeZoneId`, `DefaultLocale` | string |
| `ZoneOffset` (9) | `TryGetTimeZoneOffset` | zigzag varint offset in ms, flags byte (1 known, 2 daylight) |
| `Resolve` (10) | `ResolveModule` | byte 0 none, or 1 then module id and source |
| `HostCall` (11) | a host function returned or threw | member name, outcome |
| `Settle` (12) | a host task settled | call id, outcome |
| `Weak` (13) | a weak-reference collection point | count, ids |
| `EntryEnd` (14) | an entry call ended | 0, or 1 and the headline of the error |
| `DefineFn` (15) | `DefineGlobalFunction` | name, length |
| `AsyncBody` (16) | `ScriptEngine.EvaluateAsyncBody` (outermost call) | source, file name |
| `Await` (17) | `ScriptEngine.Await(Async)` on the promise an earlier entry call returned | index of that entry call (the first is 0) |

Outcome byte of `HostCall` and `Settle`: 0 value, 1 .NET exception (type name, full type name, message, frame count, then function, file, line per frame), 2 thrown script value, 3 opaque (the result is not data: a function, a host object), 4 task started (call id; `HostCall` only).

Values are copied by structure: undefined, null, booleans, numbers (double), strings (UTF-8), BigInts (decimal text), arrays (length, then index and value of each element, so holes survive) and plain objects (key and value pairs in property order). An object met twice is written once and referred to by its creation index, so cycles and sharing survive. Anything else makes the result opaque; replay then calls the live host function, which must be defined (in `ScriptEngineOptions.Configure`).

## Encoding

Header: the four bytes `OERL`, then the version byte. Events follow back to back: one tag byte, then the payload. Integers are LEB128 varints (zigzag for signed), doubles are 8 bytes little-endian, strings are a varint (byte count shifted left by one, low bit set when the string holds a lone surrogate) and UTF-8, or WTF-8 (each lone surrogate as its own three bytes) when the low bit is set. A reader refuses another version, unknown tags (tags 16 and 17 were added without a version change, so a reader from before them refuses logs that use them), truncation and values nested deeper than 64 levels with `FormatException`.

## Replay

`ScriptEngine.Replay(log, options)` builds a fresh engine whose host answers from the log and never touches the real host, then performs the recorded entry calls in order. Host functions are not executed: their recorded results are returned, so side effects of the .NET code do not happen again. The replay checks every event it reads against what the script asked for and stops with `ReplayResult.Divergence` when they differ; it also compares the outcome of each entry call with the recorded one.

A task started by a host function (`JsMarshal.FromTask`) becomes a pending promise in the replay; its `Settle` event, found between entry calls, settles it, so continuations run at the recorded place in the job order. The `Settle` event always follows the `HostCall` event that started the task, also when the task was already complete (a completed, faulted or cancelled task, or one that throws before it returns) and settled inside the call.

`Await` marks the awaited promise as handled in the replay, as the wait did in the recording, so the job queue treats a rejection the same way in both. A rejection the host sees through `Await`/`AwaitAsync` is explained from a replay of the whole log; when the promise was not returned by an entry call, the report says so. Timers are host-side: a host that fires timers by evaluating a script (`engine.Evaluate("fire()")`) replays exactly, because each firing is an entry call.

## Not replayable

The log is marked not replayable (`RecordLog.IsReplayable`, `NotReplayableReason`) when the run used `ScriptEngine.Call`, `JsRealm.Call`, `JsRealm.Construct`, `SetProperty`, a second realm, a host task that settled with a non-data value or another promise, or when the log outgrew `MaxBytes` (default 8 MiB). A host that builds objects the script uses (data, `[JsExport]` installs) must do it in `Configure` so a replay rebuilds the same world.

## Size

A run with no host interaction logs its script source plus about 20 bytes. `Date.now()` in a loop costs 2 to 3 bytes a reading, `Math.random()` 9 bytes, a host call 3 to 12 bytes plus its data.
