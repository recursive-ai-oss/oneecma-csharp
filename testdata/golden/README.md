# Error golden corpus (v0)

Each case is a directory `<id>/`:

| file | content |
|---|---|
| `case.js` | the script, run as a classic script named `case.js` |
| `expected.txt` | the text report, in the layout of the reference report: `error[Kind]: <headline>`, ` --> case.js:line:col` (start of the primary span), labelled spans (`^^^` primary, `---` secondary), `= help:`, `= note:`, `= stack:` |
| `meta.json` | `id`, `category` (taxonomy id), `kind` (`syntax`, `runtime`, `limit`), `repair` (repair id or null), `node` (what Node printed: `name`, `message`, 1-based `line`/`column` of its first position, for runtime errors the first two stack lines) or null when Node has no counterpart |
| `repaired.js`, `expected.repair.txt` | only for cases a repair applies to: the repaired source, and the report with repairs enabled (the `repair[Rxxx]` block first, then nothing if the repaired program runs clean) |

Rules the files follow:

- The headline is word for word Node's `err.message` (recorded in `meta.json`). Only the spans, labels, help and notes are ours.
- Source lines are shown with the gutter width of the largest line number shown; lines that are two or more apart are separated by `...`, a single skipped line is shown.
- `= stack:` lists the script frames of V8's `err.stack` as `name (file:line)`, innermost first, joined with ` ← `; top-level code is `<top-level>`, async frames keep their `async` prefix, repeated frames collapse to `×N`.
- Limit cases have no Node counterpart; their headline and numbers are provisional.
- The `node` data (`name`, `message`, positions, stack head) comes from running Node v22.22.1 as a black box on each `case.js`; no engine source was consulted. The cases are synthetic scripts written for this corpus and are edited by hand.
- `index.json` lists the cases and the Node version that produced `meta.json`.

## `expected.json`

Every `syn-*` case has an `expected.json` next to `expected.txt`: the versioned JSON report (`docs/diagnostic-json.md`, schema `docs/diagnostic-report.schema.json`) for the same `case.js`, written indented with a trailing newline. It comes from the engine, so spans, labels, notes and fixes are what the parser produces; a test checks that its headline, primary span and error count agree with `expected.txt`, and that line/column match an independent count over `case.js`. Regenerate after a deliberate change with `ONEECMA_UPDATE_GOLDEN=1 dotnet test --solution OneEcma.slnx --filter-class '*DiagnosticJsonTests'` and review the diff.

## `map-*` cases (source maps)

A `map-*` case reports an error in generated code at its original location. Files: `case.js` (the generated script, ending in a `//# sourceMappingURL=case.js.map` comment), `case.js.map` (the source map), `original.ts` (the source the author wrote, also embedded in the map's `sourcesContent`), `expected.txt` (the text report for `oneecma check case.js`), `expected.json` (the same as JSON, with a `mapping` object), `meta.json` (`node` is what Node prints for the generated file: its position is in the generated code, which the report moves to the original). The map is written by a short script from a list of token pairs, not by a bundler.
