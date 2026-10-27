# RegExp engine

Normative description of the pattern parser, compiler, matcher, limits and data tables in `src/RecursiveAI.OneEcma/RegExp/`. A port reproduces the behaviour from this page, ECMA-262 (17th edition) §22.2 and Annex B.1.2, and the V8 wording table at the end.

The engine never uses a host regular-expression library. It implements the pattern grammar and semantics of ECMA-262 22.2 (flags `d g i m s u v y`, lookbehind, named groups including repeated names in different alternatives, modifier groups `(?ims-ims:…)`, the `v` flag with set notation and properties of strings, Unicode property escapes) plus the legacy grammar of Annex B.1.2 for patterns without `u`/`v`. The RegExp constructor, `lastIndex`, `exec` result objects and the `String.prototype` methods are the built-in's job and sit on top of this API.

## API

- `RegExpProgram.Compile(pattern, flags)` returns a program or diagnostics. It never throws for bad patterns or flags.
- `program.Exec(input, startIndex, sticky, fuel, limits)` returns `NoMatch`, `Match` (with `RegExpMatch`: start and end per group in UTF-16 code units, `-1` for an unmatched group) or `LimitExceeded` with the kind (`Fuel`, `Memory`). The `d` flag needs nothing extra: the indices are always there. `sticky` is the `y` flag; the caller owns `g` and `lastIndex`.
- `program.GroupNames` lists named groups in order of first appearance; a repeated name maps to several group numbers and the caller reports the one that participated. When several matched in one result (only reachable through the nested form below) the highest-numbered one wins.
- `RegExpValidator.Validate(body, flags)` and `ValidateLiteral(body, flags, literalStart)` return diagnostics only. They are the early-error hook for a parser that finds a regular-expression literal.
- A start index inside a surrogate pair of a `u`/`v` pattern moves back to the pair's first half.
- `Exec` allocates scratch per program and reuses it between calls; programs are immutable and safe to share across threads.

### Diagnostics

A `RegExpDiagnostic` is `(Start, Length, Reason, Message, InFlags)`. `Start`/`Length` are zero-based UTF-16 offsets into the pattern (or into the flags string when `InFlags`). The span is the offending token: the `(` of an unterminated group, the quantifier of "Nothing to repeat", the name of a duplicate group, the range of "Range out of order in character class". One diagnostic is reported per pattern: the parser stops at the first error, as V8 does, and which error comes first follows V8's order (see below). `Message` is the complete V8 text `Invalid regular expression: /<pattern>/<flags>: <Reason>` with the flags in the fixed order `dgimsuvy`. Flag errors read `Invalid flags supplied to RegExp constructor '<flags>'`; through the literal hook they read `Invalid regular expression flags`.

## Semantics notes

- Case: without `u`/`v`, Canonicalize is the toUppercase mapping restricted to single code units that do not map a non-ASCII unit to ASCII; with `u`/`v`, simple case folding (`CaseFolding.txt` statuses C and S). With `v` and `i`, class contents are simple-case-folded as the spec's set operations require. Sets are kept as sorted code-point ranges; with `i` (not `v`) the range set is augmented with canonical images and the input is canonicalized at match time.
- Lookarounds are atomic: backtracking into a successful positive lookaround is impossible; captures set inside a positive lookaround stay, those inside a negative one are discarded.
- A quantified group clears the captures it contains at the start of every iteration; an iteration that matches the empty string after the minimum is met fails (RepeatMatcher step 2.b).
- Lookbehind matches backwards: terms evaluate right to left, backreferences read backwards.
- Repeated group names: ECMA-262 22.2.1.1 forbids two groups of one name unless they sit in different alternatives of one disjunction (MightBothParticipate). The parser registers a named group when it closes, so the error appears at the second group of the pair, before any later error in the pattern.

## Limits

| Limit | Value | Outcome |
|---|---|---|
| Fuel | the `FuelMeter` the caller passes; one unit per instruction step, per backtrack and per character consumed by a repeated single-character atom | `LimitExceeded`/`Fuel` |
| Backtrack stack | `RegExpLimits.MaxBacktrackBytes`, default 64 MiB; entries are 16 bytes | `LimitExceeded`/`Memory` |
| Pattern nesting (groups, lookarounds, nested classes under `v`) | 200 | diagnostic `Regular expression too large` |

The matcher draws fuel from the meter in chunks of 1024 units (`FuelMeter.ChunkSize`), counts down locally and refunds what it did not use; a host that adds wall-clock or cancellation polling derives from `FuelMeter` and overrides `TryConsume`. A meter that refuses means the match call returns `LimitExceeded`; no exception leaves `Exec`. The matcher and the parser use no recursion that follows pattern or input size: the parser checks the nesting counter, the matcher keeps an explicit backtrack stack.

## Compiled program

Programs are arrays of `int`. Each instruction is an opcode followed by operands; `fl` is a flag word (bit 1 backwards, bit 2 ignore-case, bit 4 dot-all). Jump targets are absolute indices.

| Opcode | Operands | Meaning |
|---|---|---|
| `Char` 1 | cp, fl | one character (canonicalized when ignore-case) |
| `Any` 2 | fl | `.` |
| `Set` 3 | class index, fl | character class |
| `Bol` 4 / `Eol` 5 | multiline | `^` / `$` |
| `WordBoundary` 6 | negate, class index | `\b` / `\B` using the word class compiled for the active flags |
| `Split` 7 | target | push a choice to `target`, continue |
| `Jmp` 8 | target | jump |
| `Mark` 9 | register | store the position in a register |
| `EndGroup` 10 | group, register, backwards | set capture (start from the register, end at the position) |
| `Clear` 11 | first group, last group | unset captures |
| `Backref` 12 | backref table index, fl | backreference (several group numbers for repeated names) |
| `LookStart` 13 | negate, resume target | begin a lookaround; pushes a barrier entry |
| `LookEnd` 14 | negate | end it: discard choices (positive), or fail the lookaround (negative) |
| `SetReg` 15 | register, value | |
| `LoopSplit` 16 | counter register, min, max, greedy, exit target | counted loop head |
| `LoopEnd` 17 | counter register, position register or -1, min, loop head | counted loop tail with the empty-iteration check |
| `EmptyCheck` 18 | register | fail if the position did not move |
| `Repeat` 19 | kind (0 char, 1 any, 2 set), argument, fl, min, max, greedy | single-character loop in one backtrack entry |
| `Strings` 20 | trie index, fl | longest-first match over a `v`-mode string set |
| `Match` 21 | — | success |

The backtrack stack holds 4-int entries (kind, pc or register, position, extra): choice points, register-undo records and lookaround barriers.

## Unicode data

`tools/RegExpUnicodeGen` reads the files pinned in `tools/RegExpUnicodeGen/pin.txt` (UCD 17.0.0 with SHA-256 each, plus the emoji sequence files) and writes `Data/regexp-unicode.bin`, embedded as a manifest resource and decoded lazily at first use. The output is deterministic. Layout, all integers as LEB128 varints:

1. magic `OERU`, version byte `1`;
2. property sets: count, then per set a kind byte (General_Category, Script, Script_Extensions, binary), the names (canonical and aliases, UTF-8 with length), and a range list (count, then pairs of gap-from-previous-end and length-minus-one);
3. simple case folds: count, then pairs (key gap, zigzag value delta);
4. non-Unicode uppercase pairs, same encoding;
5. string sets for `\p{Emoji_Keycap_Sequence}`, `RGI_Emoji_Flag_Sequence`, `RGI_Emoji_Modifier_Sequence`, `RGI_Emoji_Tag_Sequence`, `RGI_Emoji_ZWJ_Sequence`, `Basic_Emoji`: name, single code point ranges, then sequences (length, code points) sorted by code points. `RGI_Emoji` is their union, built at run time.

Property names and values are exactly those of the supported-property tables in ECMA-262 22.2.2.9 (so `space` and `Script=Grek` work and `WSpace` does not). The file is about 95 KB. Regenerate with `dotnet run --project tools/RegExpUnicodeGen -c Release -- <ucd-dir> src/RecursiveAI.OneEcma/RegExp/Data/regexp-unicode.bin`.

## Deviations from Node (V8)

The differential in `testdata/regexp/` (835 hand-written cases) and three seeded random sets of 15,000 patterns each, recorded with Node v24.22.1, agree on match results and error messages except for these, which the harness counts as documented:

1. **Match starting inside a surrogate pair.** In a `u`/`v` pattern V8 can return a match whose start index lies between the two halves of a pair (`/\B/u` on a string with astral characters, and lookaheads such as `(?!.)`). The specification only starts on code point boundaries; so do we.
2. **Modifier `i` leaks into `\W`, `\b`, `\B`.** After a group `(?i:…)`, V8 evaluates a later `\W`, `\b` or `\B` of a `u`/`v` pattern without `i` as if `i` were set, so U+017F and U+212A count as word characters. We apply the flags of the enclosing scope, as the spec does.
3. **Repeated group names.** For some arrangements of alternatives V8 accepts names that 22.2.1.1 rejects, for example `(x|y)|(?<n>)(?<n>1|)`, which depends on an earlier capturing group containing `|`. V8's rule is not a function of the spec's, so we implement the spec's MightBothParticipate with V8's registration time. Where V8 accepts `(?<n>a(?<n>b)|c)` (the inner group in a non-last alternative of the outer group's own body) and rejects `(?<n>a|c(?<n>b))`, we agree.

## V8 message table

Reasons the parser produces, each checked against Node v24.22.1 output in the 436 `error` cases of `testdata/regexp/expected.json` (the recorded file is the evidence: pattern, flags and Node's message):

`Unterminated group`, `Unmatched ')'`, `Nothing to repeat`, `Lone quantifier brackets`, `Incomplete quantifier`, `Invalid quantifier`, `numbers out of order in {} quantifier`, `\ at end of pattern`, `Invalid escape`, `Invalid Unicode escape`, `Invalid decimal escape`, `Invalid named reference`, `Invalid named capture referenced`, `Invalid capture group name`, `Duplicate capture group name`, `Invalid group`, `Invalid flag group`, `Repeated flag in flag group`, `Unterminated character class`, `Range out of order in character class`, `Invalid character class`, `Invalid character in character class`, `Invalid set operation in character class`, `Negated character class may contain strings`, `Invalid property name`, `Invalid property name in character class`, `Regular expression too large`.

Ordering rules observed: errors come in source order of the point where the parser first cannot continue; a repeated group name is reported at the closing of the second group. In `v` mode a `-` straight after a completed range (`[a-z-]`) is `Invalid character class`, while a stray `-` elsewhere is `Invalid character in character class`.

## Checks

- test262 data translators: `built-ins/RegExp/property-escapes/generated` (469 files), `unicodeSets/generated` (114), `CharacterClassEscapes` (12) — the generated strings are matched by our engine exactly as `harness/regExpUtils.js` does; all pass.
- Literal validation: every regular-expression literal in `language/literals/regexp`, `built-ins/RegExp/{property-escapes,named-groups,lookBehind,unicodeSets,match-indices}` and `annexB/built-ins/RegExp` through the validator. 13 files in `language/literals/regexp` are lexical (line terminators inside a literal, a `/` inside a body, escaped flags) and belong to the lexer.
- Fuzzing: `fuzz/OneEcma.Fuzz` (`regexp <seconds> [seed] [corpus.json]`) generates and mutates patterns and runs them under small fuel and memory limits, checking that nothing throws, compile and validate agree, matches are in range and each case ends in bounded time.
- Run `tests/OneEcma.Tests` for all of the above; set `ONEECMA_TEST262_ROOT` to a test262 checkout to include the translators.
