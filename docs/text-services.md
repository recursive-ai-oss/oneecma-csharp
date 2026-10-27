# Text services

Normative description of the three Unicode text services the engine builds on: normalization (UAX #15), collation (UTS #10 with the CLDR root order and UTS #35 tailorings) and segmentation (UAX #29). They are internal C# services in `RecursiveAI.OneEcma.TextServices`; `String.prototype.normalize`, `Intl.Collator`, `Intl.Segmenter` and `localeCompare` wrap them. Data: Unicode 17.0.0 and CLDR 48.0 (see `locale-data.md` for the embedded formats). Nothing here uses the platform's globalization or normalization support.

## API (internal)

| Type | Members |
|---|---|
| `Normalizer` | `Normalize(string, form)` (same instance back when already normalized), `Normalize(ReadOnlySpan<char>, form)`, `TryNormalize(source, destination, form, out written)`, `IsNormalized(span, form)`, `Decompose(span, compatibility, CodePointBuffer)` (NFD or NFKD as code points, for collation). `NormalizationForm` = `Nfc`, `Nfd`, `Nfkc`, `Nfkd`. |
| `TextSegmenter` | `Enumerate(span, granularity)` (a `foreach`-able ref struct yielding `TextSegment(Start, End, IsWordLike)`), `NextBoundary(span, start, granularity, out isWordLike)`, `TryFindSegment(span, index, granularity, ...)` (ECMA-402 `containing`). `SegmentGranularity` = `Grapheme`, `Word`, `Sentence`. |
| `TextCollator` | `Create(locale, CollatorOptions?)`, `Compare(span, span)`, `GetSortKey(span)`, `TryGetSortKey(span, destination, out written)`. Instances are immutable and thread-safe. |
| `CollatorOptions` | `Strength` (`Primary` to `Identical`), `CaseLevel`, `CaseFirst` (`Off`, `Lower`, `Upper`), `Numeric`, `IgnorePunctuation`, `Usage` (`Sort`, `Search`), `Collation` (BCP 47 `-u-co-` type such as `phonebk`, `trad`), `WithSensitivity("base" | "accent" | "case" | "variant")`. |

Text is UTF-16; lone surrogates pass through as ordinary code points of no special class (normalization leaves them alone, collation gives them implicit weights, segmentation treats them as `Other`). Nothing depends on the thread's culture.

## Normalization

NFC, NFD, NFKC and NFKD by the algorithm of UAX #15 section 3: full decomposition (table lookup, Hangul syllables by the arithmetic of Unicode 3.12), canonical ordering of each run of non-starters, and for the composing forms the canonical composition algorithm (D117) with the Hangul and primary-composite rules. `IsNormalized` uses the quick-check properties (derived from the same data: no for composition-excluded canonical mappings and for compatibility mappings under NFKC, maybe for characters that can compose backward and for composites whose decomposition starts with one, per UAX #15 section 9.2) and falls back to normalizing and comparing for "maybe". Pure ASCII input returns immediately.

Time is linear in the input. The one exception is a single run of more than 32 combining marks, which is sorted in n log n; the sort is stable on (class, position).

## Collation

### Algorithm

1. The text is decomposed to NFD (the table is keyed on NFD).
2. Collation elements are looked up with the longest-match rule of UTS #10 section 3.3: the longest contiguous contraction (up to three code points, the root table's longest), then **discontiguous** extension (S2.1.2 and S2.1.3): a following non-starter that is not blocked from the match and forms a table entry with it joins it and is removed from its position. At most 64 following non-starters are examined for one match; real text has a few, and the bound keeps hostile runs of marks linear. Code points without an entry get the implicit weights of UTS #10 section 10.1.3 (Han, Tangut, Nushu, Khitan, unassigned).
3. Elements are converted to a working scale (primary x 4096, secondary and tertiary x 256) so that a tailoring can place a new weight between two neighbours.
4. A sort key is built level by level (UTS #10 S3): primaries (4 bytes each), then 0, secondaries (3 bytes), optionally the case level, tertiaries (2 bytes), the fourth level when punctuation is ignored and strength allows, and for `Identical` the NFD code points. Zero weights are omitted. `Compare` builds both keys and compares the bytes.

### Compare and sort keys

Both are provided. `Compare` is the primary entry point. Sort keys exist because sorting n strings with `Compare` rebuilds 2 n log n keys; sorting by cached keys was about 30 times faster on 627 short strings (65 microseconds against 2.2 ms, see Throughput). Keys are plain byte strings whose byte order equals the collator's order (checked in the tests); they are only comparable with keys of an identically configured collator.

### Options (ECMA-402 section 10)

| Option | Behaviour |
|---|---|
| `sensitivity` `base` / `accent` / `case` / `variant` | strength Primary / Secondary / Primary with case level / Tertiary. |
| `ignorePunctuation` | Variable weighting "shifted" (UTS #10 section 4): elements marked variable (spaces and punctuation; the CLDR root's `maxVariable` is punctuation, so symbols and currency signs still count) drop out of levels 1 to 3, as do ignorables directly after them. |
| `numeric` | A run of decimal digits (general category Nd, any script) compares by value; leading zeros do not count; digits of any length. Implemented as a marker element just below the primary of "0", then the digit count, then base-100 pairs. |
| `caseFirst` `lower` / `upper` | Case is ordered ahead of the rest of the tertiary weight. Case classes are lower, mixed, upper (mixed is a contraction written with both cases, "Ch"). Case follows the tertiary weight: the capital forms 8 to 0xC, squared capitals 0x1D and normal-size kana 0xE, 0x11, 0x12 (small kana count as lowercase), and for tailored elements the letters they are written with. The default `Off` uses the table's raw tertiary order. |
| `usage: "search"` | The CLDR `search` collation of the locale (root search rules plus the locale's own): equates, for example, compatibility Arabic forms and Korean jamo, and does not apply the Thai/Lao prevowel contractions. |
| `-u-co-` type | `phonebk` (German) and `trad` (Spanish) select the tailoring of that name; a type the locale lacks is ignored. |

### Tailorings

`CollationRules` parses UTS #35 collation rule syntax and applies it to a copy of the root table: `&` resets (a string, or `[last primary ignorable]`), the relations `<`, `<<`, `<<<`, `=`, the shorthands `;` and `,`, quoting (`'...'`, `''`) and backslash escapes, `[import tag-u-co-type]`, `[suppressContractions [set]]`, and a number of settings that do not affect Latin text (`normalization`, `strength`, `caseFirst`, ...) which are read and ignored. A new weight is the next unit above its reference at the relation's level, with the lower levels set to the common weights. Syntax the supported locales do not use (`[before n]`, `/` expansions, `|` prefixes, `<*` lists, `<<<<`, `[reorder]`) raises `NotSupportedException`, never a silently wrong order. The rule text of the supported locales (es standard, traditional and search; de phonebook and search; root search) is stored in the supplemental data; en, fr and pt use the root order. Every stored rule set parses (a test creates each).

### Known gaps

- **Han** orders by code point (implicit weights), not by radical and stroke as the CLDR root does. The Node differential and the conformance files do not exercise it.
- Backwards secondary weights (fr-CA), `[reorder]` of scripts, and the draft `eor` and Han-specific collations are not supported. The six locales do not need them.
- A tailoring whose reference adds more than 4095 primaries (or 255 secondaries or tertiaries) between two root weights would collide; the stored rules add at most a few.
- `maxVariable` is fixed at punctuation.

## Segmentation

UAX #29 default boundaries, extended grapheme clusters (rules GB1 to GB999 including GB9c, the Indic conjunct rule, GB11 emoji ZWJ sequences and GB12/13 regional-indicator pairs), word boundaries (WB1 to WB999) and sentence boundaries (SB1 to SB998). Each boundary decision reads forward from the segment start with a few fields of state (the last two classes, the regional-indicator count, the sentence-terminator state); the two unbounded rules are linear in total: WB4 skipping (each run is walked a constant number of times) and the SB8 look-ahead (it stops at the next sentence terminator, so each character is examined once per preceding terminator it follows, and a terminator ends the previous look-ahead).

`IsWordLike` for words follows ECMA-402 and what the reference implementation reports: true when the segment contains a letter, a number, katakana, hiragana or a Han ideograph, or two or more connector punctuation characters in a row ("__" is word-like, "_" is not). Hiragana and Han are recognized by code point ranges because UAX #29 gives them no word class.

### Locale handling and known gaps

- The reference Node applies no locale tailoring to Intl.Segmenter for the six locales: the corpus below segments identically in all seven tags, and sentence breaks after abbreviations ("Mr. Smith") are **not** suppressed. This implementation does the same, so there are no suppression lists. If an application wants CLDR's `ss=standard` behaviour it would be an added option, not the default.
- **Dictionary segmentation** (Han, Thai, Lao, Khmer, Burmese, and Japanese words) is not implemented: the default rules break between ideographs and hiragana and treat Thai letters as no class, so each cluster stands alone. Node uses an ICU dictionary there. Documented differences are listed in `tests/OneEcma.Tests/SegmenterTests.cs` with the reason.
- `TryFindSegment` scans from the start of the text (linear in the index).

## Conformance and differential results

Files are fetched by `tools/locale-data/fetch.sh` (pinned SHA-256 in `tools/locale-data/pin.txt`) and never committed; the tests skip with a message when they are absent.

| Test file | Version | Result |
|---|---|---|
| `NormalizationTest.txt` | 17.0.0 | 20,034 of 20,034 lines for NFC, NFD, NFKC, NFKD, the is-normalized check on every column, and every code point outside Part 1 unchanged |
| `GraphemeBreakTest.txt` | 17.0.0 | 766 of 766 |
| `WordBreakTest.txt` | 17.0.0 | 1,944 of 1,944 |
| `SentenceBreakTest.txt` | 17.0.0 | 512 of 512 |
| `CollationTest_CLDR_NON_IGNORABLE.txt` | CLDR 48 (`common/uca`, UCA 17.0.0 root) | 206,298 of 206,298 lines in order at identical strength |
| `CollationTest_CLDR_SHIFTED.txt` | CLDR 48 | 227,809 of 227,809 lines in order (shifted, identical strength) |

Node differential (`tools/text-diff/`, expectations recorded in `testdata/text/`; Node v22.22.1 with ICU 78.2 and the reference Node v24.22.1 with ICU 78.3 produce byte-identical expectation files, both CLDR 48.0 and Unicode 17.0):

- Collation: 627 strings in six languages, symbols, numbers, kana, combining sequences and other scripts (no Han) over 768 combinations of 9 locale tags (`en-US`, `en-GB`, `es`, `es-US`, `fr`, `de`, `pt-BR`, `de-u-co-phonebk`, `es-u-co-trad`), four sensitivities, ignorePunctuation, numeric, three caseFirst values and both usages: **0 mismatches**.
- Segmentation: 53 strings at three granularities, all seven tags agreeing with each other: matches except three documented dictionary cases (Thai, Han, Japanese words).

To refresh the expectations: `node tools/text-diff/gen-collation.js > testdata/text/collation-expected.json` and `node tools/text-diff/gen-segmentation.js > testdata/text/segmentation-expected.json`, with the corpus from `tools/text-diff/make-collation-corpus.py` and `testdata/text/segmentation-corpus.json`.

## Throughput

Informal, one core of a 32-thread host (load average about 2.6), .NET 10.0.11, Release, `dotnet run -c Release` of the test project with `ONEECMA_TEXT_THROUGHPUT=1`. The mixed text is 4 million characters of the segmentation corpus (mostly Latin, with accents, emoji and several scripts); the ASCII text is 4.5 million characters of English.

| Service | Result |
|---|---|
| NFC / NFD / NFKC / NFKD, mixed text | 121 / 260 / 49 / 245 million chars/s |
| NFC of ASCII (quick check) / NFD of ASCII | 2,100 / 1,070 million chars/s |
| Grapheme / word / sentence segmentation | 36 / 53 / 75 million chars/s |
| Collation `Compare`, default / numeric + shifted | 3.2 / 2.3 million compares/s (short strings) |
| Sort keys | 5.7 million keys/s |
| Sorting 627 strings | 65 microseconds by cached keys, 2.2 ms by `Compare` |

## Safety

No recursion follows the input. Every loop is bounded by the input length or by a small constant (contraction length 3, discontiguous scan 64). Buffers are pooled arrays sized by the input. A seeded random test runs all services over 4,000 and 1,500 strings of interesting code points (marks, Hangul, regional indicators, ZWJ sequences, lone surrogates, noncharacters) and checks idempotence, segment coverage, and that the collator is a consistent order (a sort with an inconsistent comparer throws). Pathological inputs (400,000 combining marks, 200,000 regional indicators, 200,000 "1. ", 300,000 digits in numeric mode) finish in the tests.
