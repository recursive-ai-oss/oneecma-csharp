# Intl formatting core

Normative description of the engine-internal services behind `Intl.NumberFormat`, `PluralRules`, `ListFormat`, `RelativeTimeFormat`, `DisplayNames` and the locale machinery, as implemented in `RecursiveAI.OneEcma.Intl`. The JavaScript constructors and option reading (property access, `ToString`/`ToBoolean`/`ToNumber` conversions, abrupt completions from user getters) sit on top of these services and are not part of them: every option arrives as a string, integer or boolean, and an invalid value raises `IntlException` (`Kind` is `Range` or `Type`, which map to `RangeError` and `TypeError`).

Data comes from the embedded CLDR 48.0 tables (`docs/locale-data.md`). Supported locales: `en-US`, `en-GB`, `es` (and `es-US`), `fr`, `de`, `pt-BR`; Gregorian calendar and Latin digits only. The default locale is a parameter of every `Create`.

## Surface

| Service | Entry points |
|---|---|
| `LocaleId` | `Parse`, `Canonicalize` (UTS 35 language/script/region/variant aliases, keyword type aliases, extension order), `Maximize`, `Minimize` |
| `LocaleOptions` | `Apply(tag, options)`: ECMA-402 14.1.1 language/script/region replacement and the `ca`, `co`, `hc`, `kf`, `kn`, `nu` keyword options |
| `LocaleResolver` | `CanonicalizeLocaleList`, `BestAvailableLocale`, `Match`, `Resolve` (ResolveLocale with extension keys), `SupportedLocales`, `ValidateLocaleMatcher`, `ValidateNumberingSystem` |
| `NumberFormatService` | `Create`, `Format`, `FormatToParts`, `FormatRange`, `FormatRangeToParts`; resolved options as properties |
| `PluralRulesService` | `Create`, `Select`, `SelectRange`, `PluralCategories` |
| `ListFormatService` | `Create`, `Format`, `FormatToParts` |
| `RelativeTimeFormatService` | `Create`, `Format`, `FormatToParts` |
| `DisplayNamesService` | `Create`, `Of` |
| `IntlEnumeration` | `SupportedValuesOf(key)` |
| `DecimalNumber` | exact decimal value from a double, BigInt or numeric string; nine rounding modes, increments, significant/fraction digit priority |

Numbers are exact: a double is converted to its shortest round-trip decimal, a BigInt or numeric string keeps all digits, and rounding works on the digit string, never on binary floating point.

Options: `localeMatcher` accepts `lookup` and `best fit` (both use the lookup matcher) and anything else is a `RangeError`. `numberingSystem` must match the Unicode locale `type` production (groups of 3 to 8 alphanumerics joined by hyphens) or it is a `RangeError`; a well-formed system other than `latn` is unsupported and falls back to `latn`, as ECMA-402 9.2.7 allows. `unitDisplay`, `currencyDisplay` and `currencySign` are validated whatever the style.

## Differential against Node

The corpus in `testdata/intl/*.jsonl` is recorded from Node v24.22.1 (ICU 78.3, CLDR 48.0, Unicode 17.0) by `tools/intl-diff/gen-corpus.js`: a deterministic grid of values, options and locales per service, one row per call, with formatToParts output compared part by part. `deviations.txt` lists the rows where we differ on purpose.

| Service | Rows | Match | Documented | Unexplained |
|---|---:|---:|---:|---:|
| NumberFormat | 4566 | 4566 | 0 | 0 |
| NumberFormat formatRange | 720 | 714 | 6 | 0 |
| PluralRules | 784 | 777 | 7 | 0 |
| ListFormat | 766 | 766 | 0 | 0 |
| RelativeTimeFormat | 1122 | 1118 | 4 | 0 |
| DisplayNames | 1505 | 1483 | 22 | 0 |
| Locale (canonicalize, maximize, minimize, supported*) | 580 | 566 | 14 | 0 |

Deviations, with the rule followed:

- **Huge values (`1e21`)**: ICU overflows the plural operands, so PluralRules and the RelativeTimeFormat plural choice differ; the CLDR rules on the exact value are followed.
- **DisplayNames code canonicalization**: ECMA-402 canonicalizes region and script codes and calendar aliases before lookup (`us` is `US`); Node returns its input spelling or no name.
- **Range with a currency code**: Node shifts the source labels of the code prefix; the specification's shared/start/end labels are kept.
- **Locale set**: `supportedLocalesOf`, the resolved locale and `supportedValuesOf` depend on the available-locale set (six locales here, all of CLDR in Node); `supportedValuesOf` lists only what the data set carries (calendar `gregory`, numbering system `latn`).
- **Locale algorithms**: `minimize` of `und-Hant` and the base name of `en-u-va-posix` follow the ECMA-402 algorithms; ICU keeps the language / variant keyword.

Where the specification leaves room, Node's behaviour is followed: Spanish list conjunction `y`/`e` and `o`/`u` before a vowel sound, percent and compact-percent patterns, the range collapse heuristic, the approximately sign from the locale symbols, and the 1% epsilon for `numeric: auto` relative times.

## test262 `intl402` replay

`tools/intl-test262/translate.js` runs the data-style tests of `test/intl402` (NumberFormat, PluralRules, ListFormat, RelativeTimeFormat, DisplayNames, Locale, Intl) in a vm context where the Intl constructors record their calls; every assertion that compares a call result with a literal becomes a check (`{service, locales, options, method, args, expected}`). `IntlTest262Tests` replays the checks against the services (set `ONEECMA_INTL_TEST262_CASES` to the translator output). Checks for locales outside the data set (`ja`, `zh`, `en-IN`, `pt-PT`, ...) and shapes that belong to the JS layer (user getters, non-string locale objects) are skipped. At the pinned test262 commit: 778 files, 188 translated, 11,720 checks; **9,672 pass, 0 fail, 937 documented, 1,111 skipped**. `testdata/intl/test262-expectations.txt` documents each of the 937: the numbering-system tests need non-Latin digits, and one roundingIncrement check needs integer conversion in the option layer.

## Known gaps

- Numbering systems other than `latn`; calendars other than Gregorian.
- Locales outside the six (the lookup falls back to the default locale).
- `Intl.Locale` accessors (`calendars`, `collations`, `hourCycles`, `textInfo`, `weekInfo`, `getTimeZones`) are not provided yet; `LocaleOptions.Apply` covers the constructor.
- `best fit` is the lookup matcher.
- DateTimeFormat and DurationFormat are a separate unit.
- ICU's quirks above are not reproduced.

## Throughput

Informal (`ONEECMA_INTL_THROUGHPUT=<file>`, Release build, 1000 random doubles with 0 to 8 integer digits and up to 3 decimals, one thread, loaded shared host): `format` of a double 450 to 1,650 ns for decimal, currency and compact patterns (about 1.1 KB allocated each), `format` of a pre-converted decimal 360 to 610 ns, `PluralRules.select` 113 ns and 137 B. No recursion follows input size: rounding, grouping and pattern assembly are loops over the digit string.
