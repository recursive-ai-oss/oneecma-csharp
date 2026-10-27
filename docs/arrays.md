# Array and iterator built-ins

Implementation notes for `Array`, the array iterators, `Iterator` and the iterator helpers. The algorithms follow ECMA-262 (17th edition) step by step; this page records only what the specification leaves open or what differs from it on purpose.

## Where the specification leaves room

- **Cyclic `join`/`toString`/`toLocaleString`.** An array that is already being joined contributes the empty string (`a = [1]; a.push(a); String(a) === "1,"`), the behaviour of Node. Nesting deeper than 1000 levels, or a host stack running low, is a `RangeError: Maximum call stack size exceeded`.
- **`sort` comparator order.** The sort is a stable bottom-up merge sort. A comparison passes the element from the right-hand run first, so a degenerate comparator such as `() => -1` reverses the input and `() => 1` keeps it, as in Node. The result for a comparator that contradicts itself is always a permutation of the input.
- **`toLocaleString`.** Each element's `toLocaleString` is called with exactly two arguments, `locales` and `options` (ECMA-402 behaviour). The base-language test that expects no arguments only holds in a build without `Intl`.
- **`flat` and `flatMap`.** Nesting is walked with an explicit stack that is capped at the call-depth limit; a cyclic array flattened with `Infinity` ends in a `RangeError`.
- **`take`/`drop`.** A finite limit above 2^53 - 1 is a `RangeError`, as the conformance suite requires (the edition text only rejects `NaN` and negative limits).
- **Dense fast paths.** `push`, `unshift`, `shift`, `splice`, `reverse` and `slice` copy a dense array's storage directly only when the result is the same as the specification's element-by-element algorithm: every property is a default data property, the array is extensible with a writable length, and no object on the prototype chain can hold or intercept an integer key. Anything else takes the generic path.

## Huge and sparse lengths

Loops charge one unit of fuel per index, so a loop over `length: 2**53 - 1` ends in a fuel limit report. For `indexOf`, `lastIndexOf`, `includes` (with a defined search value), `sort` and the deletion of leftover slots after `sort`, a range longer than 65536 indices on an ordinary object or array whose prototype chain holds no integer keys visits only the keys that exist. If a getter changes the object during such a visit, indices added after the walk started are not seen; this differs from the specification only for objects that mutate themselves from accessors on a length above 65536.

Results that must be materialised (`toSorted`, `toReversed`, `toSpliced`, `with`) charge their size to the allocation budget before they allocate, and a length above 2^32 - 1 is a `RangeError` (`Invalid array length`).

## V8 wording

| Script | `err.message` in Node v24.22.1 |
|---|---|
| `[].map(1)` | `number 1 is not a function` |
| `[].map({})` | `object is not a function` |
| `[].forEach('x')` | `string "x" is not a function` |
| `[].filter(null)` | `object null is not a function` |
| `[].some(undefined)` | `undefined is not a function` |
| `[].reduce((a, b) => a)` | `Reduce of empty array with no initial value` |
| `Array.from(null)` | `object null is not iterable (cannot read property Symbol(Symbol.iterator))` |
| `new Array(-1)`, `Array(2**32)` | `RangeError: Invalid array length` |
| `[1].sort(1)` | `The comparison function must be either a function or undefined: 1` |
| `[1].with(5, 1)` | `RangeError: Invalid index : 5` |
| `Array.prototype.map.call(undefined, f)` | `Array.prototype.map called on null or undefined` (also `concat`, `indexOf`, `forEach`, `filter`, `every`, `some`, `reduce`, `reduceRight`, `find`, `findIndex`, `findLast`, `findLastIndex`) |
| `Array.prototype.join.call(null)` | `Cannot convert undefined or null to object` (all other methods) |
| `Array.prototype.push.call({length: 2**53 - 1}, 1)` | `Pushing 1 elements on an array-like of length 9007199254740991 is disallowed, as the total surpasses 2**53-1` |
| `Object.freeze([1]).pop()` | `Cannot delete property '0' of [object Array]` |
| `Object.freeze([]).push(1)` | `Cannot add property 0, object is not extensible` |
| `Object.defineProperty([], 'length', {writable: false}).push(1)` | `Cannot assign to read only property 'length' of object '[object Array]'` |
| `[][Symbol.iterator]().next.call({})` | `Method Array Iterator.prototype.next called on incompatible receiver #<Object>` |
| `Iterator()` | `Constructor Iterator requires 'new'` |
| `new Iterator()` | `Abstract class Iterator not directly constructable` |
| `Iterator.prototype.map.call(1, f)` | `Iterator.prototype.map called on non-object` |
| `Iterator.prototype.take.call({}, -1)` | `RangeError: -1 must be positive` |
| `Iterator.from(1)` | `Iterator.from called on non-object` |
| `Iterator.prototype.reduce.call({next() { return {done: true}; }}, f)` | `Reduce of a done iterator with no initial value` |
| a running helper's `next()` called re-entrantly | `Generator is already running` |

Known differences from Node: `Object.freeze([1]).sort()` throws a `TypeError` here (the specification stores the element back) where Node returns the array; the message for a non-callable `Symbol.iterator` on `Iterator.from` is the engine's general `is not a function` wording.
