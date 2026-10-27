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

## `run-*` and `ref-*` cases: what the report says

`GoldenRunTests` runs each `run-*` case and compares the whole report text with `expected.txt` (the headline is also checked against Node's in `meta.json`). `ref-001-checkout` is the reference report of the design, run as `checkout.js` with `api.getOrder` as a host function; the test compares it with the design's text after two documented changes: the headline keeps V8's capital (`Cannot read properties…`), and `-->` gives the start of the primary span (12:23, the property name `items`). Regenerate after a deliberate change with `ONEECMA_UPDATE_GOLDEN=1` and review the diff.

The prose of a runtime report comes from the traced replay (value provenance, "did you mean" from what the object or scope holds, the underline on the property that produced the missing value). Rules the generated text follows:

- A read or write of `undefined`/`null` underlines the property name that produced it (`items` of `order.items`), and the label names the whole expression.
- A binding's origin is labelled `` `x` comes from here: { keys } `` at the expression that was written to it; a nullish value is `` `x` is set to null here ``. Objects stored again show the earlier places, up to 8.
- A call that fails inside a built-in with one argument (`JSON.parse`, `Object.keys`, `new Array`) underlines the argument.
- A property write is an origin too: `cart.items = saved;` is labelled `` `items` is set to undefined here `` when `cart.items` is read later.
- `Reduce of empty array with no initial value` underlines the callee, says the array is empty, shows where it was made and suggests the initial value.
- `Cannot mix BigInt and other types` names which operand is the BigInt and which is not, with both origins and the `BigInt(x)` / `Number(x)` rewrites (`BigInt(x)` only when `x` is a whole number).
- `Cannot destructure property` on a path (`const { host } = config.db`) shows the parent's origin and what the parent does hold; on a parameter pattern it marks the call site and suggests a `= {}` default.
- `Invalid time value` shows where the Date was made and from what text, and says what is wrong with an ISO-looking text.
- `x.lenght is not a function` / `title.toUppercase` look at every member the receiver answers to, own or inherited; a near-miss data property is offered with a note that it is not called. A plain object asked for an array method gets the `Object.values(o).map(...)` form.
- A `JSON.parse` error is located by parsing the text again; when the text is a string literal in the source (directly, or through the binding it was set from) the underline sits on the offending character, otherwise a note shows the text before it.
- Cases whose error the engine still has no explanation for (string length) show the plain report: headline, location, underline and stack.

### Expected files rewritten for the explained report

| files | why |
|---|---|
| `host-001`…`host-010` | top-level code is shown as `<top-level>` (was `<anonymous>`) in the stack line |
| `run-001`, `run-002`, `run-013` | the underline is the property name that produced the missing value (`items`, `tag`, `user`); `run-002` and `run-012` help is the guard `x?.member` / `x?.[0]` |
| `run-003`, `run-004`, `run-016`, `run-019`, `run-025` | labels and help come from the object's real contents and type (`is an Object, so it has no map`, `is a Promise; await it first`, `Number(price).toFixed`) |
| `run-005` | the suggestion carries what the binding holds (`(number)`) |
| `run-007` | the help names the real alternatives instead of a rewritten destructuring |
| `run-008`, `run-009` | the declaration label underlines the name, not the whole declarator |
| `run-010`, `run-011`, `run-017`, `run-022` | the primary underline is the argument the built-in rejected, with its origin |
| `run-012` | origin label reads `matches is set to null here` |
| `run-014`, `run-015` | shorter labels generated from the callee's kind |
| `run-018` | repeated frames collapse to `down (case.js:2) ×10`; the label says the function keeps calling itself |
| `run-020`, `run-021`, `run-023`, `run-024` | explained: empty reduce, BigInt mixing, destructured parameter, Invalid Date |
| `run-026` | no explanation exists for this error yet; the report is the plain one |
| `run-027`…`run-035` | added with the explanations above (method typos, a property write as origin, JSON text, a missing destructured property, a global typo, a plain object used as an array, an empty reduce after `filter`) |
| `syn-036`…`syn-039` | repair cases: a `const` annotation, a run of extra closers, one and two missing `)` before a `;` |
| every case with a `= stack:` line and no help or note | a `|` line now separates the source from `= stack:`, as in the reference report |
| `syn-012`, `syn-027` | the original expectation drew an underline longer than the token (`---` under one character, six dashes under a backtick); the engine's one-character underline is right |
