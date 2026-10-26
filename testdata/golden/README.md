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

`expected.json` (the versioned JSON report) is added next to `expected.txt` once the JSON schema exists.
