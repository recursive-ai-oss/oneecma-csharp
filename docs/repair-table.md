# Auto-repair table (v0)

Auto-repair is off by default and syntax-only. It runs only on source that fails to parse. A fix is applied only when it is the single plausible one and the repaired source parses; any fix that could change meaning is reported as a suggestion instead. Applied repairs are listed first in the report and the repaired source is returned.

**Ranking.** Entries are ranked by how often code written by LLMs makes each mistake. Entries marked `observed` have been seen in such code; entries marked `prior` have not been seen yet and are kept because they are cheap, unambiguous and commonly expected. Priors rank below observed entries and are revisited when evidence arrives. The machine-readable form is `testdata/repair-table.v0.json`; the test cases are under `testdata/golden/`.

| rank | id | mistake | action | evidence | seed cases |
|---:|---|---|---|---|---|
| 1 | S-BACKTICK | backtick inside a template literal (for example in a comment inside a CSS or HTML template) | suggest | observed | `syn-027-backtick-in-template` |
| 2 | S-AWAIT | top-level `await` in a script | suggest | observed | `syn-017-top-level-await` |
| 3 | S-REDECL | redeclared `let`/`const`/`class` at top level (often across repeated evaluations in one global scope) | suggest | observed | `syn-022-redeclared-const` |
| 4 | R001 | extra closing delimiter `)` `]` `}` after a complete expression or statement | apply | observed | `syn-001-extra-close-paren`, `syn-002-extra-close-bracket` |
| 5 | S-CLOSE | missing `)` / `]` / `}` in the middle of the source | suggest | observed | `syn-003-missing-close-paren` |
| 6 | R007 | TypeScript type syntax in JavaScript: parameter/variable annotations, `as T`, `interface`/`type` declarations | apply | observed | `syn-019-ts-parameter-types`, `syn-020-ts-as-cast`, `syn-021-ts-interface` |
| 7 | R002 | closing brackets missing at the very end of the input (truncated output) | apply | prior | `syn-004-unclosed-brace-at-end` |
| 8 | R003 | markdown code fence lines around the program | apply | prior | `syn-034-closing-fence-only`, `syn-007-fenced-block-runs-as-templates` |
| 9 | R004 | typographic quotes used as string delimiters | apply | prior | `syn-008-smart-double-quotes`, `syn-009-smart-single-quotes` |
| 10 | R005 | missing comma between elements, properties or arguments on separate lines | apply | prior | `syn-010-missing-comma-object`, `syn-011-missing-comma-array`, `syn-012-missing-comma-arguments` |
| 11 | R006 | Python keyword or operator in place of the JavaScript one: `elif`, `and`, `or` | apply | prior | `syn-013-python-elif`, `syn-016-python-and-or` |
| 12 | R008 | comma between class members | apply | prior | `syn-026-comma-between-methods` |
| 13 | R009 | backslash before a quote outside a string (JSON-escaped code pasted raw) | apply | prior | `syn-028-escaped-quotes` |
| 14 | R010 | HTML entities in code: `&amp;`, `&lt;`, `&gt;`, `&quot;` | apply | prior | `syn-030-html-entities` |
| 15 | R011 | literal backslash-n between statements | apply | prior | `syn-029-literal-backslash-n` |
| 16 | S-RETURN | top-level `return` | suggest | prior | `syn-018-top-level-return` |
| 17 | S-STRING | unterminated string, or a literal line break inside a string | suggest | prior | `syn-005-unclosed-string`, `syn-006-newline-in-string` |
| 18 | S-PY | other Python-isms: `def`, `for x in range()`, f-strings, `None/True/False` | suggest | prior | `syn-014-python-def`, `syn-015-python-fstring`, `syn-032-python-for-in-range`, `run-006-python-true` |
| 19 | S-MODULE | `import`/`export` in a script | suggest | prior | `syn-023-import-in-script` |
| 20 | S-TRY | `try` without `catch`/`finally` | suggest | prior | `syn-031-try-without-catch` |

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

- Mistake: extra closing delimiter `)` `]` `}` after a complete expression or statement.
- Rule: delete the one closing token the parser reports as unexpected.
- Guard: apply only if (a) the token is a closer with no matching opener, (b) deleting it parses, and (c) no other single-token edit (inserting an opener earlier) also parses; otherwise suggest.
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
- Guard: only for the erasable subset (no enum, valueful namespace, parameter properties, decorators); apply only if the erased source parses; report each erased span; code that is meant to be TypeScript is out of scope.
- Example: `function add(a: number, b: number) {` -> `function add(a, b) {`
- Observation: type annotations appear in code meant for a plain JavaScript host.

### R002 (apply)

- Mistake: closing brackets missing at the very end of the input (truncated output).
- Rule: append the closers for the open-bracket stack, innermost first, at end of input.
- Guard: apply only if every parse error is "end of input inside open brackets" and the stack is non-empty; indentation of the last lines is evidence (a dedent to column 0 that is followed by nothing means the closer belongs there); never close an open string or template literal.
- Example: `function f() { ⏎   return 1;` -> `function f() { ⏎   return 1; ⏎ }`

### R003 (apply)

- Mistake: markdown code fence lines around the program.
- Rule: remove lines that consist only of ``` or ```<language> at the start or end of the input.
- Guard: a fully fenced block already parses (the backticks form template literals), so this check runs before parsing: if the first or last non-blank line is only a fence, strip it and parse the rest; apply only if the result parses.
- Example: ````js ⏎ const n = 5; ⏎ ```` -> `const n = 5;`

### R004 (apply)

- Mistake: typographic quotes used as string delimiters.
- Rule: replace U+2018/U+2019 with ' and U+201C/U+201D with " when they open or close a string where the parser expects a token.
- Guard: only outside strings, comments, template literals and regular expressions ; apply only if the result parses; pairs must match (open/close).
- Example: `console.log(“hello”);` -> `console.log("hello");`

### R005 (apply)

- Mistake: missing comma between elements, properties or arguments on separate lines.
- Rule: insert `,` after the previous element when the next line starts a new element.
- Guard: inside an array literal, object literal or argument list; previous token ends a value, next token starts a value or property; inserting there parses and no other position does.
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
- Guard: only when the backslash precedes a quote at token position, and the result parses.
- Example: `console.log(\"hi\");` -> `console.log("hi");`

### R010 (apply)

- Mistake: HTML entities in code: `&amp;`, `&lt;`, `&gt;`, `&quot;`.
- Rule: decode the entity.
- Guard: only outside strings and comments; apply only if the decoded source parses.
- Example: `a &amp;&amp; b` -> `a && b`

### R011 (apply)

- Mistake: literal backslash-n between statements.
- Rule: replace the two characters `\n` with a line break.
- Guard: only outside strings, where `\` cannot start a token; result parses.
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
