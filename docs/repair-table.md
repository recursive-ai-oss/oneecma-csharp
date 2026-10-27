# Auto-repair table (v2)

Changes since v1: R012 added (rank 7; every later rank moves down one), R001 also removes a run of adjacent unmatched closers, R007 also erases the annotation of a `const`/`let`/`var` declaration whose first error is the missing initializer, and the machine-readable file is `repair-table.v2.json`.

Auto-repair is off by default and syntax-only. It runs only on source that fails to parse. A fix is applied only when it is the single plausible one and the repaired source parses; any fix that could change meaning is reported as a suggestion instead. Applied repairs are listed first in the report and the repaired source is returned.

**Ranking.** Entries are ranked by how often code written by LLMs makes each mistake. Entries marked `observed` have been seen in such code; entries marked `prior` have not been seen yet and are kept because they are cheap, unambiguous and commonly expected. Priors rank below observed entries and are revisited when evidence arrives. The machine-readable form is `testdata/repair-table.v2.json`; the test cases are under `testdata/golden/`.

## How a repair runs

Entry point: `SyntaxRepair.Repair(source, parseOptions, repairOptions, ct)` (also `ScriptEngine.CheckAndRepair`); the CLI is `oneecma check --repair [--out <path>] <file>`. Nothing in the parser repairs anything: `JsParser.Parse` and `ScriptEngine.Check` report errors only.

1. If the first or last non-blank line is only a fence (R003), the fence is stripped provisionally. A source that parsed is kept stripped only if the rest parses too.
2. While the text has errors, take the first one (by position) and ask every apply rule for candidate fixes near it (its line and the line before). Each candidate is applied to a copy and parsed. It is plausible when the copy parses or its first error lies beyond the edit and the token after it.
3. A candidate is applied only if it is the single distinct plausible result. Rival readings (R001 "insert an opener instead") count against it but are never applied. No candidate, or more than one, ends the search.
4. Repeat, to `MaxRepairs` (default 16) repairs and `MaxParses` (default 400) trial parses. Hitting either ends the search with a `repair-limit` warning.
5. All or nothing: if the final text still has an error, nothing is returned repaired; the result carries the original diagnostics with their suggestions. If the text parses, the result lists the repairs in the order applied (offsets in the original source) and the repaired source.

A repair is one rule application. Rules that fix a recurring mistake at once (R001 on a run of closers, R007 on a signature, R009, R010, R011) make all their edits in one repair; the report block points at the first. Block layout:

```
repair[R001]: removed an extra closing delimiter
 --> case.js:3:26
  |
3 | console.log(items.length));
  |                          ^ removed
  |
  = note: the repaired source parses and is returned with this report
```

The closing note is `RenderOptions.RepairOutcome`; a host that goes on to run the repaired source may word it differently. The library only parses.

Suggest-only entries are ordinary diagnostics: the explanation as `help:`, the reason as `= note: not repaired automatically: <reason>`, and where a mechanical edit exists a `SuggestedFix` in `Diagnostic.Fixes` (S-CLOSE, S-STRING, S-AWAIT, S-REDECL for `let`/`var`, S-PY, S-TRY, S-BACKTICK). S-TRY carries no note (the author writes the handler), S-RETURN and S-MODULE carry none because no edit is mechanical.

## Table

| rank | id | mistake | action | evidence | seed cases |
|---:|---|---|---|---|---|
| 1 | S-BACKTICK | backtick inside a template literal (for example in a comment inside a CSS or HTML template) | suggest | observed | `syn-027-backtick-in-template` |
| 2 | S-AWAIT | top-level `await` in a script | suggest | observed | `syn-017-top-level-await` |
| 3 | S-REDECL | redeclared `let`/`const`/`class` at top level (often across repeated evaluations in one global scope) | suggest | observed | `syn-022-redeclared-const` |
| 4 | R001 | extra closing delimiter `)` `]` `}` after a complete expression or statement, alone or as a run (`)))`, `}}}`) | apply | observed | `syn-001-extra-close-paren`, `syn-002-extra-close-bracket`, `syn-037-doubled-extra-closer` |
| 5 | S-CLOSE | missing `)` / `]` / `}` in the middle of the source | suggest | observed | `syn-003-missing-close-paren` |
| 6 | R007 | TypeScript type syntax in JavaScript: parameter/variable annotations, `as T`, `interface`/`type` declarations | apply | observed | `syn-019-ts-parameter-types`, `syn-020-ts-as-cast`, `syn-021-ts-interface`, `syn-036-ts-variable-annotation` |
| 7 | R012 | `)` or `]` missing right before a `;` that ends the line (`foo(1;`) | apply | observed | `syn-038-missing-paren-before-semicolon`, `syn-039-two-missing-parens-before-semicolon` |
| 8 | R002 | closing brackets missing at the very end of the input (truncated output) | apply | prior | `syn-004-unclosed-brace-at-end` |
| 9 | R003 | markdown code fence lines around the program | apply | prior | `syn-034-closing-fence-only`, `syn-007-fenced-block-runs-as-templates` |
| 10 | R004 | typographic quotes used as string delimiters | apply | prior | `syn-008-smart-double-quotes`, `syn-009-smart-single-quotes` |
| 11 | R005 | missing comma between elements, properties or arguments on separate lines | apply | prior | `syn-010-missing-comma-object`, `syn-011-missing-comma-array`, `syn-012-missing-comma-arguments` |
| 12 | R006 | Python keyword or operator in place of the JavaScript one: `elif`, `and`, `or` | apply | prior | `syn-013-python-elif`, `syn-016-python-and-or` |
| 13 | R008 | comma between class members | apply | prior | `syn-026-comma-between-methods` |
| 14 | R009 | backslash before a quote outside a string (JSON-escaped code pasted raw) | apply | prior | `syn-028-escaped-quotes` |
| 15 | R010 | HTML entities in code: `&amp;`, `&lt;`, `&gt;`, `&quot;` | apply | prior | `syn-030-html-entities` |
| 16 | R011 | literal backslash-n between statements | apply | prior | `syn-029-literal-backslash-n` |
| 17 | S-RETURN | top-level `return` | suggest | prior | `syn-018-top-level-return` |
| 18 | S-STRING | unterminated string, or a literal line break inside a string | suggest | prior | `syn-005-unclosed-string`, `syn-006-newline-in-string` |
| 19 | S-PY | other Python-isms: `def`, `for x in range()`, f-strings, `None/True/False` | suggest | prior | `syn-014-python-def`, `syn-015-python-fstring`, `syn-032-python-for-in-range`, `run-006-python-true` |
| 20 | S-MODULE | `import`/`export` in a script | suggest | prior | `syn-023-import-in-script` |
| 21 | S-TRY | `try` without `catch`/`finally` | suggest | prior | `syn-031-try-without-catch` |

## Rules and guards

### S-BACKTICK (suggest)

- Mistake: backtick inside a template literal (for example in a comment inside a CSS or HTML template).
- Rule: escape the inner backtick.
- Guard: the missing delimiter could instead be a closing backtick.
- Observation: an unescaped backtick inside a template literal.

### S-AWAIT (suggest)

- Mistake: top-level `await` in a script.
- Rule: wrap the code in an async function and call it, or run it as a module.
- Guard: wrapping changes the scope of every declaration and the completion value; a host may instead choose async-function-body mode for the whole call.
- Observation: top-level await is the most frequent syntax-level mistake.

### S-REDECL (suggest)

- Mistake: redeclared `let`/`const`/`class` at top level (often across repeated evaluations in one global scope).
- Rule: rename, assign instead of redeclaring, or evaluate each call in a fresh scope.
- Guard: every fix changes meaning.
- Observation: redeclaring let/const between evaluations in one global scope.

### R001 (apply)

- Mistake: extra closing delimiter `)` `]` `}` after a complete expression or statement, alone or as a run of several (`)))`, `}}}`).
- Rule: delete the one closing token the parser reports as unexpected; when several unmatched closers follow it directly, delete the run in one repair (`console.log(1)));` -> `console.log(1);`).
- Guard: apply only if (a) the token is a closer with no matching opener, (b) deleting it parses and the first error moves past it, and (c) no rival reading parses: inserting the matching opener at the start of the statement (for `)` only when the wrapping would change the meaning, since parenthesising a whole expression statement means the same thing; for `]` always) or, for `}`, where a body without braces could begin (after `)`, `else`, `=>`, `do`, `try`, `finally`); otherwise suggest. For a run, each length is tried and only the length that makes the source parse is plausible; the opener rival is checked for the first closer only, so a run of `]` still falls back to a suggestion
- Example: `console.log(items.length));` -> `console.log(items.length);`
- Observation: extra closing delimiters are the one syntax mistake seen repeatedly; only some are fixed by deleting the reported token.

### S-CLOSE (suggest)

- Mistake: missing `)` / `]` / `}` in the middle of the source.
- Rule: insert the closer where the indentation or the next statement suggests.
- Guard: more than one position parses.
- Observation: a missing closer in the middle of a call.

### R007 (apply)

- Mistake: TypeScript type syntax in JavaScript: parameter/variable annotations, `as T`, `interface`/`type` declarations.
- Rule: erase the type syntax (annotations, `as` casts, interface and type-alias declarations); runtime meaning is unchanged.
- Guard: only for the erasable subset (no enum, valueful namespace, parameter properties, decorators); apply only if the erased source parses; the annotations of one parameter list and its return type are one repair, and a chain `x as A as B` is one repair; code that is meant to be TypeScript is out of scope
- Example: `function add(a: number, b: number) {` -> `function add(a, b) {`
- Observation: type annotations appear in code meant for a plain JavaScript host.

### R012 (apply)

- Mistake: `)` or `]` missing right before a `;` that ends the line (`foo(1;`).
- Rule: insert the closers of every group opened on that line and still open at the `;`, innermost first, directly after the last token before the `;`.
- Guard: apply only if the `;` is the last thing on its line (a trailing comment is fine), every group to close was opened on that same line and is a `(` or `[` that is not a `for (` header, and the result parses. Text after the `;` on the line, or an opener on an earlier line, means the closer position is a guess; the mistake then stays a suggestion (S-CLOSE).
- Example: `const result = double(21;` -> `const result = double(21);`; `console.log(double(21;` -> `console.log(double(21));`
- Observation: the narrow, unambiguous case of the observed missing `)` in the middle of a call.

### R002 (apply)

- Mistake: closing brackets missing at the very end of the input (truncated output).
- Rule: append the closers for the open-bracket stack, innermost first, at end of input.
- Guard: apply only if every parse error is at the end of the input (end of input inside open brackets, including a call or array cut off mid-list) and the stack is non-empty; never close an open string or template literal; indentation is evidence: if a later line is indented no deeper than the line of an open `{` and is not itself a closer, that `{` was left open in the middle of the file and nothing is applied; closers go innermost first, a `}` on its own line at the indentation of its opener's line, `)` and `]` directly after the last token
- Example: `function f() { ⏎   return 1;` -> `function f() { ⏎   return 1; ⏎ }`

### R003 (apply)

- Mistake: markdown code fence lines around the program.
- Rule: remove lines that consist only of ``` or ```<language> at the start or end of the input.
- Guard: a fully fenced block already parses (the backticks form template literals), so this check runs before parsing: if the first or last non-blank line is only a fence, strip it; apply only if the result parses (when the source did not parse to begin with, the other rules may finish the job and the whole repair must parse)
- Example: ````js ⏎ const n = 5; ⏎ ```` -> `const n = 5;`

### R004 (apply)

- Mistake: typographic quotes used as string delimiters.
- Rule: replace U+2018/U+2019 with ' and U+201C/U+201D with " when they open or close a string where the parser expects a token.
- Guard: only outside strings, comments, template literals and regular expressions ; apply only if the result parses; pairs must match (open/close).
- Example: `console.log(“hello”);` -> `console.log("hello");`

### R005 (apply)

- Mistake: missing comma between elements, properties or arguments on separate lines.
- Rule: insert `,` after the previous element when the next line starts a new element.
- Guard: the innermost open bracket is `[`, the `(` of a call or the `{` of an object literal (not a block); the previous token ends a value and the next one, on a new line, starts a value or property; inserting there makes the first error move on
- Example: `host: "x" ⏎   port: 1` -> `host: "x", ⏎   port: 1`

### R006 (apply)

- Mistake: Python keyword or operator in place of the JavaScript one: `elif`, `and`, `or`.
- Rule: `elif` -> `else if`; `and` -> `&&`; `or` -> `||`.
- Guard: only when the word is the reported unexpected identifier, sits where an operator or `else if` belongs, and the result parses; `not`, `def`, `in range`, `:` blocks, `None/True/False` are not mechanical (see S-PY).
- Example: `} elif (n > 1) {` -> `} else if (n > 1) {`

### R008 (apply)

- Mistake: comma between class members.
- Rule: delete the comma.
- Guard: only directly between two class elements; result parses.
- Example: `inc() {}, ⏎   dec() {}` -> `inc() {} ⏎   dec() {}`

### R009 (apply)

- Mistake: backslash before a quote outside a string (JSON-escaped code pasted raw).
- Rule: remove the backslash.
- Guard: only when the lexer rejects a backslash that precedes a quote; every escaped quote from there to the end of that line is unescaped in one repair; apply only if the result parses
- Example: `console.log(\"hi\");` -> `console.log("hi");`

### R010 (apply)

- Mistake: HTML entities in code: `&amp;`, `&lt;`, `&gt;`, `&quot;`.
- Rule: decode the entity.
- Guard: only outside strings, comments and template text; every entity in code is decoded in one repair; apply only if the decoded source parses
- Example: `a &amp;&amp; b` -> `a && b`

### R011 (apply)

- Mistake: literal backslash-n between statements.
- Rule: replace the two characters `\n` with a line break.
- Guard: only outside strings, templates, regular expressions and comments, where `\` cannot start a token; every such `\n` is replaced in one repair; apply only if the result parses
- Example: `const a = 1;\nconst b = 2;` -> `const a = 1; ⏎ const b = 2;`

### S-RETURN (suggest)

- Mistake: top-level `return`.
- Rule: put the code in a function, or end with the value as an expression.
- Guard: the intended result depends on the host.

### S-STRING (suggest)

- Mistake: unterminated string, or a literal line break inside a string.
- Rule: close the string, use `\n`, or use a template literal.
- Guard: the fix choice changes the string.

### S-PY (suggest)

- Mistake: other Python-isms: `def`, `for x in range()`, f-strings, `None/True/False`.
- Rule: translate by hand.
- Guard: no mechanical rewrite keeps the meaning; `None/True/False` parse and fail at run time as undefined names.

### S-MODULE (suggest)

- Mistake: `import`/`export` in a script.
- Rule: run as a module.
- Guard: module mode changes strictness and `this`.

### S-TRY (suggest)

- Mistake: `try` without `catch`/`finally`.
- Rule: add a catch clause.
- Guard: the handler body is the author's.

## Notes

- Indentation is admissible evidence for where a missing closing bracket goes (R002, S-CLOSE): a dedent to the column of the opener marks the position.
- Top-level `await`, top-level `return` and redeclaration between evaluations are better handled by a host evaluation mode (async function body, fresh scope per call) than by repair; they stay suggestions.
