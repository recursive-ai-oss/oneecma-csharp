# String, Number, Math, JSON, BigInt and the URI functions

What these built-ins do where ECMA-262 leaves room, the wording of the errors they raise, and where they differ from the reference engine (Node v24.22.1). The recorded evidence is in `testdata/builtins/` (scripts plus the reference output; `BuiltinsDifferentialTests` replays them).

## Implementation-defined choices

| Area | Behaviour |
|---|---|
| Case mapping | `toUpperCase`/`toLowerCase` use the Unicode 17.0.0 full mappings: `UnicodeData.txt` simple mappings overridden by the unconditional entries of `SpecialCasing.txt`, plus the Final_Sigma rule (Cased, Case_Ignorable from `DerivedCoreProperties.txt`). Language-sensitive entries (`tr`, `az`, `lt`) are not applied. `toLocaleUpperCase`/`toLocaleLowerCase` ignore their arguments until the Intl layer exists. Data: `src/RecursiveAI.OneEcma/Unicode/CaseData.g.cs`, produced by `tools/gen-case-data.py <ucd-dir> <output>`. |
| `localeCompare` | The CLDR root collation at tertiary strength, with no locale argument handling. Canonically equivalent strings compare equal. |
| `normalize` | The engine's own UAX #15 implementation (Unicode 17.0.0). |
| `toString(radix)` for a Number | Radix 10 is `Number::toString`. Other radices print the exact integer part and the shortest fractional digits that round-trip. |
| `parseInt` | Digits are accumulated exactly and rounded once, for every radix. |
| `toFixed`, `toExponential`, `toPrecision` | Exact integer arithmetic on the double's mantissa and exponent; ties pick the larger `n`, as the spec says. |
| `Math` functions | The BCL's `Math.*` for sin, cos, tan, asin, acos, atan, atan2, exp, log, log10, log2, sinh, cosh, tanh, asinh, acosh, atanh; `expm1`, `log1p`, `hypot` (scaled, compensated), `cbrt` (one corrected Newton step) are written in this engine. Results are within 2 ulp of Node's. |
| `Math.f16round`, `Math.sumPrecise` | `f16round` (ES2025) rounds to binary16 with ties to even. `sumPrecise` sums exactly (the sum is kept as an integer multiple of 2^-1074) and rounds once. |
| `JSON.stringify` depth | No limit; the serializer keeps its own stack. Nesting is bounded by memory, the string-length limit and fuel. |
| `JSON.parse` depth | No limit; the parser and the reviver walk keep their own stacks. |

## Limits

- Every built-in that builds a string checks the result length against `EngineLimits.MaxStringLength` before allocating and throws `RangeError: Invalid string length`: `repeat`, `padStart`, `padEnd`, `concat`, `String.raw`, the HTML methods, `toUpperCase`, `normalize`, `escape`, `encodeURI*`, `JSON.stringify`.
- Loops over input charge fuel in proportion to its length (one unit per 32 code units for scans and case mapping, one per element for lists and JSON values). Strings and arrays built by `repeat`, `padStart`, `JSON.parse` and `BigInt.prototype.toString` are also charged to the allocation budget before they are built.
- `BigInt.asIntN`/`asUintN` with a width over 2^30 bits and a value that would need that width throw `RangeError: Maximum BigInt size exceeded`.

## Error wording (V8, recorded from Node v24.22.1)

| Script | Error |
|---|---|
| `String.prototype.trim.call(null)` | `TypeError: String.prototype.trim called on null or undefined` (same pattern for every method; `[Symbol.iterator]` prints without the dot) |
| `'a'.repeat(-1)` / `repeat(Infinity)` / `repeat(-1.5)` | `RangeError: Invalid count value: -1` / `Infinity` / `-1.5` |
| `'abc'.repeat(2**30)`, `'a'.padStart(2**30)` | `RangeError: Invalid string length` |
| `'a'.normalize('x')` | `RangeError: The normalization form should be one of NFC, NFD, NFKC, NFKD.` |
| `String.fromCodePoint(-1)`, `(1.5)`, `('x')` | `RangeError: Invalid code point -1`, `1.5`, `NaN` |
| `'a'.startsWith(/a/)` (also `includes`, `endsWith`) | `TypeError: First argument to String.prototype.startsWith must not be a regular expression` |
| `String.prototype.toString.call(1)` | `TypeError: String.prototype.toString requires that 'this' be a String` (same for `valueOf`, `Number.prototype.*`, `BigInt.prototype.*`) |
| `String.raw()` | `TypeError: Cannot convert undefined or null to object` |
| `(1).toFixed(101)` | `RangeError: toFixed() digits argument must be between 0 and 100` |
| `(1).toExponential(-1)` | `RangeError: toExponential() argument must be between 0 and 100` |
| `(1).toPrecision(0)` | `RangeError: toPrecision() argument must be between 1 and 100` |
| `(1).toString(1)`, `1n.toString(37)` | `RangeError: toString() radix argument must be between 2 and 36` |
| `BigInt(1.5)`, `BigInt(NaN)` | `RangeError: The number 1.5 cannot be converted to a BigInt because it is not an integer` |
| `BigInt('x')` | `SyntaxError: Cannot convert x to a BigInt` |
| `BigInt(Symbol())`, `BigInt(undefined)`, `BigInt.asIntN(1, 1)` | `TypeError: Cannot convert Symbol() to a BigInt`, `... undefined ...`, `... 1 ...` |
| `new BigInt(1)` | `TypeError: BigInt is not a constructor` |
| `BigInt.asIntN(-1, 1n)` | `RangeError: Invalid value: not (convertible to) a safe integer` |
| `2n ** -1n` | `RangeError: undefined must be positive` (as printed by the reference engine) |
| `decodeURI('%')`, `encodeURI('\ud800')` | `URIError: URI malformed` |

### JSON.parse

`SyntaxError` messages, in the order the parser meets them. `position` is the UTF-16 index, `line`/`column` are 1-based and count `\n` only.

| Condition | Message |
|---|---|
| input ends where a value is expected | `Unexpected end of JSON input` |
| a character that cannot start a value, or a literal that stops matching | `Unexpected token 'x', "<snippet>" is not valid JSON` |
| the whole text is `undefined`, `NaN` or `Infinity` | `"undefined" is not valid JSON` |
| after `{` something other than a string or `}` | `Expected property name or '}' in JSON at position N (line L column C)` |
| after `,` in an object something other than a string | `Expected double-quoted property name in JSON at position N (line L column C)` |
| after a key something other than `:` | `Expected ':' after property name in JSON at position N (line L column C)` |
| after an object value | `Expected ',' or '}' after property value in JSON at position N (line L column C)` |
| after an array element | `Expected ',' or ']' after array element in JSON at position N (line L column C)` |
| text after the value | `Unexpected non-whitespace character after JSON at position N (line L column C)` |
| `-` without digits | `No number after minus sign in JSON at position N (...)` |
| a digit after a leading `0` | `Unexpected number in JSON at position N (...)` |
| `1.` without digits | `Unterminated fractional number in JSON at position N (...)` |
| `1e` / `1e+` without digits | `Exponent part is missing a number in JSON at position N (...)` |
| string not closed | `Unterminated string in JSON at position N (...)` (position = text length) |
| raw control character in a string | `Bad control character in string literal in JSON at position N (...)` |
| `\x` | `Bad escaped character in JSON at position N (...)` (N = the character after the backslash) |
| `\u` with a non-hex digit | `Bad Unicode escape in JSON at position N (...)` (N = the first bad digit) |

Expected-token messages are used even at the end of input; only the "value expected" state reports `Unexpected end of JSON input`.

Snippet: the whole text in double quotes when it is shorter than 21 code units; otherwise 10 code units before the token up to 10 after, quoted, with `...` before when the window does not start at 0 and after when it does not end at the end.

### JSON.stringify

`TypeError: Do not know how to serialize a BigInt`. A cycle gives

```
Converting circular structure to JSON
    --> starting at object with constructor 'Object'
    |     property 'k0' -> object with constructor 'Object'
    |     ...
    |     property 'k3' -> object with constructor 'Object'
    --- property 'back' closes the circle
```

One `|` line per step from the first object of the cycle; when there are more than three, the first two, `...`, and the last. A step through an array reads `index 0`. The constructor name is the `name` of the data property `constructor` found on the prototype chain, `Object` when there is none.

## Differences from the reference engine

- `Number.prototype.toString(radix)` for radices that are not powers of two prints every digit of an integer above 2^53; Node prints zeros after the first 53 bits (`(1e21).toString(7)`).
- `parseInt(s, radix)` for radices other than 2, 4, 8, 10, 16, 32 with more than 10 digits is correctly rounded; Node approximates.
- `JSON.stringify` of very deep structures succeeds; Node throws `RangeError: Maximum call stack size exceeded` at about 5000 levels.
- Transcendental `Math` functions can differ from Node in the last digit (measured over 3000 random arguments, mismatching in the last digit: expm1 22%, log1p 20%, asinh 17%, atanh 15%, atan2 13%, cbrt 8%, exp 4.5%, cos/tan/sin/atan/log/acos/asin about 1-2%).
- `toLocaleUpperCase('x_y')` does not validate the locale (Node: `RangeError: Incorrect locale information provided`); validation comes with the Intl layer.
- `Math.sumPrecise` exists (ES2026); Node v24 does not have it.
