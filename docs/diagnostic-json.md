# Diagnostic JSON report (version 1)

The JSON form of a report. It carries everything the text report shows, as data, so a host, an editor or an agent tool can read it without parsing text. The schema is `docs/diagnostic-report.schema.json` (JSON Schema 2020-12, `$id` `urn:oneecma:diagnostic-report:1`). The writer is `DiagnosticJson`; the CLI prints it with `oneecma check --format json <file>` (on standard output; the text form goes to standard error).

## Rules

- **Versioned.** The top-level `"version"` is `1`. Within a version, fields are only ever added. A consumer ignores properties it does not know. A change that removes, renames or re-types a field, or changes what a value means, is a new version.
- **Offsets and positions.** `start` and `end` are offsets in UTF-16 code units into the source text, half-open (`end` is one past the last code unit). `line` and `column` are 1-based; **columns count UTF-16 code units** (a character outside the Basic Multilingual Plane is two columns), the same unit V8 uses in `err.stack`. `endLine` and `endColumn` are the position of `end`. A line ends at LF, CR, CRLF, U+2028 or U+2029. An empty span has `start == end`.
- **Strings.** Output is UTF-8. A lone UTF-16 surrogate in the script source (legal in JavaScript, not representable in JSON) is written as U+FFFD. Offsets still count the original code units.
- **Absent versus empty.** Core arrays (`secondary`, `notes`, `help`, `fixes`, `recovery`) are always present and may be empty. Optional members (the ones marked *optional* below) are left out when there is nothing to say, never written as `null` or an empty array.
- **Deterministic.** The same diagnostics give the same bytes. Property order is as listed here. Compact by default (one line); `JsonRenderOptions.Indented` gives two-space indentation (used by the corpus files).

## Document

```json
{
  "version": 1,
  "format": "oneecma.report",
  "summary": { "errors": 1, "warnings": 0, "repairs": 0 },
  "repairs": [ ... ],          // optional: applied repairs, first to last
  "repairedSource": "...",     // optional: present exactly when "repairs" is
  "diagnostics": [ ... ]
}
```

`summary.errors` and `summary.warnings` count the diagnostics by severity; `summary.repairs` counts the applied repairs. Repairs are listed before diagnostics because a repaired run's report starts with what was changed.

### Diagnostic

| field | type | meaning |
|---|---|---|
| `kind` | string | error constructor name (`SyntaxError`, ...), or `SourceMap` for a problem with a source map |
| `code` | string | stable machine-readable id such as `unexpected-token`; never reworded once published |
| `severity` | `"error"` \| `"warning"` | |
| `headline` | string | word for word what V8 puts in `err.message` for the same mistake (for `SourceMap` diagnostics, our own wording) |
| `source` | `{ name, length }` | the source every span below refers to; `length` in code units |
| `span` | span | the primary span; the `-->` line of the text report is its start |
| `label` | string | the text beside the primary underline; may be empty |
| `secondary` | `[{ span, message }]` | secondary labelled spans (underlined with `-` in text) |
| `notes` | string[] | the `= note:` lines |
| `help` | string[] | the `= help:` lines |
| `fixes` | `[{ description, edits }]` | suggested changes; none is applied. An edit is `{ start, length, line, column, text }`: the range `[start, start+length)` becomes `text` |
| `recovery` | `[{ action, span, text }]` | what the parser assumed (`insertedToken`, `text` is the token) or skipped (`skippedTokens`) to continue |
| `mapping` | object, *optional* | present when a source map was applied, see below |

A span is `{ start, end, line, column, endLine, endColumn }`.

### Repair

`{ id, title, label, source, span, edits }`: the rule id from the repair table (`R001`), what it did, the underline text, the original source, the first place it touched and every edit it made (offsets into the original source). Applying all repairs' edits in the order listed to the original source gives `repairedSource`.

### `mapping` (optional)

Added by `SourceMapReports` when a mapping exists for the primary span:

```json
"mapping": {
  "spansAreOriginal": true,
  "original":  { "source": "src/app.ts", "line": 5, "column": 42, "endLine": 5, "endColumn": 43, "name": "greet" },
  "generated": { "source": "dist/app.js", "line": 5, "column": 34, "endLine": 5, "endColumn": 35 },
  "generatedSpan": { "start": 107, "end": 108 }
}
```

- `spansAreOriginal: true`: the original text was available, so `source` and every span of the diagnostic are in the **original** source; `generated` and `generatedSpan` say where the problem is in the generated code. Suggested fixes and recovery steps (offsets into the generated text) are dropped.
- `spansAreOriginal: false`: the map has an entry but no text for the original source; `source` and spans still refer to the generated code, and `original` says where the problem is in the original file (the text report adds an `original position:` note).
- `name` (optional) is the identifier the map names at that position.

### Slots for later report features (optional, documented now)

These members are defined so a version 1 consumer keeps working when runtime reports add them. Nothing writes them yet; when one is written it is present only if non-empty. Shapes are in the schema.

| field | content |
|---|---|
| `stack` | frames, innermost first: `{ function, source, span, async, locals: [{ name, value }] }` |
| `provenance` | where the failing value came from, nearest step first: `{ source, span, description }` |
| `didYouMean` | candidates drawn from what the object or scope actually holds, best first: `{ name, description }` |
| `asyncStack` | stack segments stitched across `await` and promise reactions: an array of frame arrays |

## Files

`testdata/golden/<case>/expected.json` is the report for each `syn-*` case as `DiagnosticJson.Render(diagnostics, Indented)` plus a newline, and for `map-001-*` the remapped report. A test reads each back and checks its headlines, spans and counts against `expected.txt` and against an independent line/column count.
