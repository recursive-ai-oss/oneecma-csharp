# Built-in behaviour recorded from the reference engine

Each `<name>.js` here was run with Node v24.22.1 (full ICU) and its output saved as `<name>.expected`. The test
`BuiltinsDifferentialTests` runs the same script in this engine, with `console.log` printing its arguments joined by spaces,
and requires identical output. They cover the V8 wording of errors raised by String, Number, BigInt, Math, JSON and the URI
functions, JSON parse error positions and snippets, the circular-structure message, and the digits produced by `toFixed`,
`toExponential`, `toPrecision`, `toString(radix)`, case mapping and normalization.

To re-record after a deliberate change: run the script with the reference Node and replace the `.expected` file.
Known differences from Node are listed in `docs/builtins-string-number-json.md`; scripts avoid them.
