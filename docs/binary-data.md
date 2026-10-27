# Binary data: ArrayBuffer, SharedArrayBuffer, TypedArrays, DataView, Atomics

Normative notes for the binary-data built-ins (ECMA-262 17th edition, clauses 23.2, 23.3, 25.1-25.4). Where the specification leaves room, this file says what the engine does.

## Representation

- A buffer is one `byte[]` holding exactly the current byte length. Resizing (`resize`, `grow`) replaces the array and copies; views refer to the buffer object, so this is unobservable. Detaching replaces it with an empty array and sets a flag.
- Element access is `BinaryPrimitives` over a span of that array: little-endian for typed arrays (the host byte order is not observable), explicit endianness for `DataView`. Numbers never box; a BigInt element allocates only the `BigInteger`.
- A typed array stores the buffer, the byte offset and a fixed length, or "auto" for a length-tracking view of a resizable buffer. Length and out-of-bounds status are recomputed on every access, so a resize is seen at once.
- Integer-indexed keys never reach property storage and never consult the prototype chain (10.4.5). A Number key (`ta[i]`) takes a fast path in the interpreter; string keys go through `CanonicalNumericIndexString`.
- `Float16Array`, `DataView.prototype.getFloat16`/`setFloat16` and `Math.f16round` use `System.Half`, which rounds to nearest even from a double.

## Limits

- The largest buffer is 2,147,483,591 bytes (the .NET array limit). A larger request, or a `maxByteLength` above it, is `RangeError: Array buffer allocation failed`. A length that fails `ToIndex` is `RangeError: Invalid array buffer length` (`Invalid typed array length: N` for typed arrays).
- Every allocation (buffer creation, `resize`, `grow`, `transfer`, `slice`, `toSorted`, `toReversed`, `with`, `Uint8Array.from*`) is charged to the engine's allocation budget before it happens. Over budget ends the run with a memory limit, which script cannot catch. A resizable buffer is charged for its current length, not its `maxByteLength`.
- Loops proportional to length charge fuel: one step per eight elements for native loops (`fill`, `indexOf`, `reverse`, `set`, copies, base64 and hex), one per element for loops that call script or read from array-likes (`map`, `forEach`, `from`, constructor from an array-like, `set` from an array-like). Sorting with a comparator ticks per comparison.
- Result strings (`join`, `toBase64`, `toHex`) above the engine's string limit are `RangeError: Invalid string length`.

## Host-defined behaviour

- `IJsHost.CanBlock` (default `true`) is the agent's `[[CanBlock]]`. With `false`, `Atomics.wait` throws `TypeError`.
- The engine has one agent. A permitted `Atomics.wait` on a matching value can only time out; it returns `"timed-out"` at once and never sleeps. With an infinite timeout nothing could ever wake it, so it throws `TypeError`.
- `Atomics.waitAsync` waiters live in the agent. `Atomics.notify` resolves them with `"ok"` in FIFO order through a queued job. A waiter with a finite timeout times out on virtual time: when the job queue drains, the waiter with the earliest deadline (ties in creation order) resolves `"timed-out"` and the loop continues. A waiter with no timeout stays pending. A run therefore never waits on the wall clock and replays identically.
- `Atomics.isLockFree(n)` is true for 1, 2, 4 and 8.
- The conformance host's `$262.detachArrayBuffer` detaches an `ArrayBuffer` and returns `null`. `$262.agent` does not exist; test262 cases that start further agents are not run.
- `Atomics.pause` is part of the next edition and is not provided.

## Deviations and choices

- `DataView.prototype.byteLength`/`byteOffset` on a detached or out-of-bounds view throw `TypeError`, as the 17th edition and Node do. The pinned test262 has tests written for a later text that returns 0; they are listed as documented failures.
- `Object.freeze`, `Object.preventExtensions` and `Reflect.preventExtensions` fail for views that can change size (length-tracking, or over a resizable `ArrayBuffer`), as 10.4.5.1 requires.
- `%TypedArray%.prototype.toLocaleString` passes its first two arguments on to each element's `toLocaleString`, as ECMA-402 specifies.
- Messages for `fromBase64`, `fromHex`, `setFromBase64` and `setFromHex` are the engine's own (Node 24 does not ship these methods): `SyntaxError` with "The input is not valid base64", "The base64 input ends with an incomplete chunk", "The base64 input has non-zero padding bits", "The hex input has an odd length" or "The hex input contains a character that is not a hexadecimal digit".

## V8 message wording

Recorded by running Node v24.22.1 (full ICU) on each script; the engine produces the same `err.message`.

| Script | Error |
|---|---|
| `new ArrayBuffer(-1)` | `RangeError: Invalid array buffer length` |
| `ArrayBuffer(1)` | `TypeError: Constructor ArrayBuffer requires 'new'` |
| `new ArrayBuffer(2**40)` | `RangeError: Array buffer allocation failed` |
| `new ArrayBuffer(1, {maxByteLength: 0})` | `RangeError: Invalid array buffer max length` |
| `ArrayBuffer.prototype.slice.call({})` | `TypeError: Method ArrayBuffer.prototype.slice called on incompatible receiver #<Object>` |
| `buffer.transfer(); buffer.slice()` | `TypeError: Cannot perform ArrayBuffer.prototype.slice on a detached ArrayBuffer` |
| `rab.resize(3)` above `maxByteLength` | `RangeError: ArrayBuffer.prototype.resize: Invalid length parameter` |
| `sab.grow(3)` above `maxByteLength` | `RangeError: SharedArrayBuffer.prototype.grow: Invalid length parameter` |
| `new Uint8Array(-1)` | `RangeError: Invalid typed array length: -1` |
| `new Uint8Array(buf, 5)` past the end | `RangeError: Start offset 5 is outside the bounds of the buffer` |
| `new Uint32Array(new ArrayBuffer(5))` | `RangeError: byte length of Uint32Array should be a multiple of 4` |
| `new Uint32Array(new ArrayBuffer(4), 1)` | `RangeError: start offset of Uint32Array should be a multiple of 4` |
| `new Uint32Array(new ArrayBuffer(8), 0, 3)` | `RangeError: Invalid typed array length: 3` |
| `new BigInt64Array(new Uint8Array(1))` | `TypeError: Cannot mix BigInt and other types, use explicit conversions` |
| `new Uint8Array(detachedBuffer)` | `TypeError: Cannot perform Construct on a detached ArrayBuffer` |
| `detached.fill(0)` | `TypeError: Cannot perform %TypedArray%.prototype.fill on a detached ArrayBuffer` |
| `Uint8Array.prototype.fill.call([])` | `TypeError: this is not a typed array.` |
| `Object.getPrototypeOf(Uint8Array)()` | `TypeError: Abstract class TypedArray not directly constructable` |
| `ta.set([1,2,3])` into a 2-element array | `RangeError: offset is out of bounds` |
| `ta.with(5, 1)` | `RangeError: Invalid typed array index` |
| `new BigInt64Array(1).fill(1)` | `TypeError: Cannot convert 1 to a BigInt` |
| `ta.sort(1)` | `TypeError: The comparison function must be either a function or undefined: 1` |
| `ta.map(1)` | `TypeError: 1 is not a function` |
| `ta.findLast(1)` | `TypeError: number 1 is not a function` |
| `Uint8Array.of.call(function(){ return new Uint8Array(0) }, 1, 2)` | `TypeError: Derived TypedArray constructor created an array which was too small` |
| `new DataView(1)` | `TypeError: First argument to DataView constructor must be an ArrayBuffer` |
| `new DataView(new ArrayBuffer(1), 0, 2)` | `RangeError: Invalid DataView length 2` |
| `new DataView(new ArrayBuffer(8)).getInt32(6)` | `RangeError: Offset is outside the bounds of the DataView` |
| `Atomics.add(new Float64Array(1), 0, 1)` | `TypeError: [object Float64Array] is not an integer typed array.` |
| `Atomics.add(new Int8Array(1), 1, 1)` | `RangeError: Invalid atomic access index` |
| `Atomics.wait(new Int32Array(4), 0, 0, 0)` | `TypeError: [object Int32Array] is not a shared typed array.` |
| `Atomics.wait(new Int8Array(new SharedArrayBuffer(16)), 0, 0, 0)` | `TypeError: [object Int8Array] is not an int32 or BigInt64 typed array.` |
