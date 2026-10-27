# Help lines for runtime errors

The `= help:` line of a runtime report names the fix. It reads live values from a traced replay and never runs script.

## "Is not a function" on a missing method

When `recv.name(...)` fails because the receiver has no such member, the help is chosen in this order:

1. The receiver is a Promise: `` `x` is a Promise; `await` it first``.
2. **Idiom table** (below): a name other languages or libraries use that JavaScript's built-ins do not have. The help gives the JavaScript form.
3. A primitive of the other kind has the method (`Number(s).toFixed`).
4. A property holding the thing being called (`result.items.map`).
5. Edit distance: `did you mean recv.name2? (function)`, from members the receiver really has.

A spelling near-miss never outranks the idiom table, and the edit-distance rule is strict for short names: a name of up to four characters may differ by one edit only (`sum` no longer suggests `some`, `size` no longer suggests `slice`); longer names allow `max(2, length / 3)` edits. A name that contains another counts as close only when the shorter has five or more characters or is at least two thirds of the longer (`items` in `lineItems`, but not `sum` in `summary`). Names that differ only by case, `_` or `-` (`createdAt`, `created_at`) are the closest match of all.

### Idiom table

| Receiver | Name | Help |
|---|---|---|
| Array | `sum`, `total` | `a.reduce((a, b) => a + b, 0)` |
| Array | `max`, `min` | `Math.max(...a)`, `Math.min(...a)` |
| Array | `average`, `avg`, `mean` | `a.reduce((a, b) => a + b, 0) / a.length` |
| Array | `contains`, `has` | `a.includes(value)` |
| Array | `size`, `len` | the property `a.length` |
| Array | `count` | `a.length`, or `a.filter(item => ...).length` |
| Array | `first`, `last` | `a[0]`, `a.at(-1)` |
| Array | `isEmpty`, `empty` | `a.length === 0` |
| Array | `append`, `add` | `a.push(value)` |
| Array | `remove`, `delete` | `a.splice(index, 1)`, or `a.filter(...)` for a copy |
| Array, String | `length()` called | `length` is a property: `a.length` |
| Map, Set | `size()` called | `size` is a property |
| String | `contains` | `s.includes(text)` |
| String | `len`, `size` | the property `s.length` |
| String | `strip`, `lstrip`, `rstrip` | `trim()`, `trimStart()`, `trimEnd()` |
| String | `toUpper`, `upper`, `toLower`, `lower` | `toUpperCase()`, `toLowerCase()` |
| String | `format` | a template literal |
| plain Object | `keys`, `values`, `entries` called as methods | `Object.keys(o)` and so on |
| plain Object | `items` | `Object.entries(o)` |
| plain Object | `has`, `hasKey`, `contains` | `key in o`, `Object.hasOwn(o, key)` |
| Map, Set | `map`, `filter`, `reduce`, `some`, `every`, `find` | `[...m].map(...)` |
| Map, Set | `contains` | `m.has(key)` |
| Map, Set | `length`, `count`, `len` | the property `m.size` |

The entries apply only when the receiver does not have a member of that name (the `length()` and `size()` rows are the reverse: the member exists and is a number). An own property of the same name always wins.

## Invalid Date

When `Invalid time value` is thrown on a Date made by `new Date(x)` in the traced run, and `x` was an `undefined` read from a property, the report labels the call (`` `new Date(e.createdAt)`: `e.createdAt` is undefined, so the Date is invalid``) and gives the missing-property help for the object that was read (`did you mean e.created_at?`, with camelCase and snake_case counted as the same word). Otherwise the help is the guard `if (Number.isNaN(d.getTime()))`.

## NaN made from an undefined

In the traced run, arithmetic, unary `+`/`-`, `++`/`--` and calls to built-ins (`Number(x)`, `Math.round(x)`) that are handed an undefined from a property read, a call or a `find` produce a NaN that remembers that undefined (the id of its origin record travels in the NaN's payload bits; nothing outside a traced replay sees it, and every NaN test treats it as NaN). Where a built-in rejects such a NaN (`new Array(n)`: `Invalid array length`; `BigInt(n)`: `The number NaN cannot be converted to a BigInt because it is not an integer`), the primary label says `` `x.count * 2` is NaN, computed from an undefined`` and the missing-property label and did-you-mean for the read follow, as for any other undefined. A Date made from such a NaN is explained like a Date made from the undefined itself. A NaN that no undefined produced (`0 / 0`) gets no origin, and nothing is reported for a NaN that never reaches an error.

## Iterating the result of a call

`for (const x of obj.items())`, `[...obj.items()]` and `const [a] = f()` fail with V8's one wording for both causes: `obj.items is not a function or its return value is not iterable`. The report looks at the callee: when it is not a function it is explained as a failed call (did you mean, what it is), and when it is a function the result is blamed (`` `obj.items(...)` returned something that is not iterable``) with the help to return an array or to loop over `Object.keys`/`values`/`entries`. Which operand shapes use this wording follows what Node prints (`iter-*` rows of `testdata/errors/cases.txt`): the callee text of a call or `new` in `for-of` and array spread (a comma expression is spelled `(0 , f)`, calls in callee position lose their `(...)`), `for await` (`obj.o(...) is not a function or its return value is not async iterable`), a declaration initializer whose callee is a plain name (`const [a] = f()`; a member callee gives the value-kind wording), and `yield*` of anything but a name (`yield* (intermediate value) is not iterable`). A spread argument in a call keeps `Spread syntax requires ...iterable[Symbol.iterator] to be a function`.
