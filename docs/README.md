# Documentation index

These files describe behaviour the specifications leave open, the formats the engine writes, and the choices behind them. They are written so a port to another language can reproduce the behaviour from the text.

## Using the library

- [host-api.md](host-api.md): the low-level host API (`ScriptEngine`, `JsRealm`, `JsValue`, promises, modules, limits, errors across the boundary).
- [js-export.md](js-export.md): `[JsExport]` bindings, boundary errors, generator diagnostics and the JSON-Schema tool helper.
- [repair-table.md](repair-table.md): the auto-repair table (version 1): each rule, its example and when it applies.
- [source-maps.md](source-maps.md): ECMA-426 source maps and how reports use them.

## Reports and diagnostics

- [diagnostic-json.md](diagnostic-json.md) and [diagnostic-report.schema.json](diagnostic-report.schema.json): the versioned JSON report and its schema.
- [stack-traces.md](stack-traces.md): the `err.stack` format and the frames of a report.
- [syntax-messages.md](syntax-messages.md): the wording of every syntax and early error, with the script that produces it.
- [record-log.md](record-log.md): the record log (version 1) that makes runs replayable.

## Language and built-ins

- [ast.md](ast.md): the syntax tree, scopes and the parser's error behaviour.
- [executor.md](executor.md): why the executor is a bytecode machine, with the measurements.
- [runtime.md](runtime.md): the contract for adding a built-in area, limits and the host interface.
- [arrays.md](arrays.md): `Array`, iterators and iterator helpers.
- [binary-data.md](binary-data.md): `ArrayBuffer`, typed arrays, `DataView` and `Atomics`.
- [builtins-string-number-json.md](builtins-string-number-json.md): `String`, `Number`, `Math`, `JSON`, `BigInt` and the URI functions.
- [collections-proxy.md](collections-proxy.md): `Map`, `Set`, weak references, `Reflect` and `Proxy`.
- [date.md](date.md): `Date` parsing, formatting and time zones.
- [regexp.md](regexp.md): the RegExp engine, its limits and Unicode data.

## Intl and Unicode

- [intl.md](intl.md): the Intl services and the time zone source.
- [locale-data.md](locale-data.md): where locale data comes from, the embedded format and how to add a locale.
- [text-services.md](text-services.md): normalization, collation and segmentation.
