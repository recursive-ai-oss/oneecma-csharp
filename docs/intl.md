# Intl formatting core

Normative description of the engine-internal services behind `Intl.NumberFormat`, `PluralRules`, `ListFormat`, `RelativeTimeFormat`, `DisplayNames`, `DateTimeFormat`, `DurationFormat`, time zone identifiers and the locale machinery, as implemented in `RecursiveAI.OneEcma.Intl`. The JavaScript constructors and option reading (property access, `ToString`/`ToBoolean`/`ToNumber` conversions, abrupt completions from user getters) sit on top of these services and are not part of them: every option arrives as a string, integer or boolean, and an invalid value raises `IntlException` (`Kind` is `Range` or `Type`, which map to `RangeError` and `TypeError`).

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
| `DateTimeFormatService` | `Create` (ECMA-402 11.1.2 CreateDateTimeFormat, including the `required`/`defaults` of `toLocaleString` and friends), `Format`, `FormatToParts`, `FormatRange`, `FormatRangeToParts`; resolved options as properties, `Pattern` |
| `DurationFormatService` | `Create`, `Format`, `FormatToParts`; `Unit(i)` gives the resolved style and display of each unit; `DurationRecord.Create` is ToDurationRecord after property reading |
| `TimeZoneData` | `Canonicalize` (primary identifier), `Identify` (identifier as asked, case-normalized), offset zone syntax |
| `ITimeZoneSource` | `TryGetOffset(zoneId, epochMs)`: the host's zone rules |
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
| DateTimeFormat (format, formatToParts, resolvedOptions, option errors, time zones) | 12928 | 12792 | 136 | 0 |
| DateTimeFormat formatRange / formatRangeToParts | 2600 | 2595 | 5 | 0 |
| DurationFormat (parts, resolvedOptions, option and value errors) | 3030 | 3029 | 1 | 0 |

Deviations, with the rule followed:

- **Huge values (`1e21`)**: ICU overflows the plural operands, so PluralRules and the RelativeTimeFormat plural choice differ; the CLDR rules on the exact value are followed.
- **DisplayNames code canonicalization**: ECMA-402 canonicalizes region and script codes and calendar aliases before lookup (`us` is `US`); Node returns its input spelling or no name.
- **Range with a currency code**: Node shifts the source labels of the code prefix; the specification's shared/start/end labels are kept.
- **Locale set**: `supportedLocalesOf`, the resolved locale and `supportedValuesOf` depend on the available-locale set (six locales here, all of CLDR in Node); `supportedValuesOf` lists only what the data set carries (calendar `gregory`, numbering system `latn`).
- **Locale algorithms**: `minimize` of `und-Hant` and the base name of `en-u-va-posix` follow the ECMA-402 algorithms; ICU keeps the language / variant keyword.

Date-time and duration deviations:

- **U+202F.** The reference's `format()` prints U+0020 where its `formatToParts` keeps the narrow no-break space of the CLDR 48 patterns (`3:05 PM`); we keep the CLDR text in both.
- **Time zone identifiers.** The reference resolves a `timeZone` to the CLDR primary identifier; ECMA-402 keeps the identifier as asked (case-normalized), and so do we (test262 `timezone-case-insensitive`, `timezone-not-canonicalized`, `canonicalize-utc-timezone`). `Canonicalize` gives the primary identifier for lookups.
- **Hour with fractional seconds** (`hour` + `fractionalSecondDigits`, mostly with the K cycle): ICU drops, reorders or annotates fields in ways CLDR does not describe (133 rows); we compose the pattern from the CLDR `appendItems`. `es-US` with a long month and a 2-digit day joins with a hyphen in the reference (3 rows); CLDR has `de`.
- **Ranges**: a reversed range with `dateStyle: full` in `en-GB` loses the weekday comma in the reference (4 rows); `de` month-only January to December separates with thin spaces there (1 row).
- **DurationFormat** formats a `NaN` field as the empty string in the reference; ToIntegerIfIntegral makes it a `RangeError`, which we follow.

Where the specification leaves room, Node's behaviour is followed: Spanish list conjunction `y`/`e` and `o`/`u` before a vowel sound, percent and compact-percent patterns, the range collapse heuristic, the approximately sign from the locale symbols, and the 1% epsilon for `numeric: auto` relative times.

## Date-time and duration formatting

### DateTimeFormat

`DateTimeFormatService.Create(locales, options, defaultLocale, defaultTimeZone, zones, required = "any", defaults = "date")`. The instant is epoch milliseconds (a `Date` time value, ±8.64e15); the zone offset comes from an `ITimeZoneSource` passed in, never from the machine. Gregorian calendar and Latin digits; any other `calendar` or `numberingSystem` falls back to them (ECMA-402 9.2.7).

- **Pattern choice.** The options become a UTS 35 skeleton (`EEE`, `y`, `MMM`, `d`, `h`/`H`/`K`/`k` by hour cycle, `m`, `s`, `S` per fractional digit, `B` for `dayPeriod`, `z`/`zzzz`/`O`/`OOOO`/`v`/`vvvv` for `timeZoneName`), and `DateTimePatternGenerator` picks the CLDR pattern: the candidates are the single-field canonical patterns, the locale's date and time styles and `availableFormats`; the distance counts 0x1000 for a field the candidate lacks, 0x10000 for one it adds and the difference of the field types otherwise; a 12-hour candidate never stands in for a 24-hour request. A request no candidate satisfies is split into a date part and a time part, joined with `dateTimeFormats` (the `atTime` variant for formats; the plain one between a date and a time interval), and fields still missing are added with `appendItems`. Widths are then adjusted to the request. Ties go to the earlier candidate unless the later one lacks a higher-numbered field; candidates are visited in a fixed order, never by hash order.
- **Styles.** `dateStyle`/`timeStyle` use the locale's `dateFormats`/`timeFormats` directly, glued with the `atTime` pattern of the date style. Mixing them with component options is a `TypeError` (`Invalid option : option`).
- **Hour cycle.** `hour12` wins over `hourCycle`, which wins over the `hc` locale keyword, which wins over the locale default; the keyword is dropped from the resolved locale when `hour12` is given. `dayPeriod` is ignored when a 23- or 24-hour clock shows an hour. `fractionalSecondDigits` adds a seconds field when a minute is shown without one.
- **Resolved options** are read back from the chosen pattern text (a quoted `de` therefore does not count as `day`), as the reference does.
- **Time zones.** `Identify` returns the identifier as asked in the spelling of the time zone database (`Asia/Kolkata` stays `Asia/Kolkata`; `Etc/UTC`, `Etc/GMT` and `GMT` are kept; `UTC` is `UTC`; matching ignores ASCII case); an offset such as `+0530` becomes `+05:30`. Offsets and names are looked up under the CLDR primary identifier (`Asia/Calcutta`). Names: specific (`CEST`), generic (`CET`/`Central European Time`), `shortOffset`/`longOffset` (`GMT+2`, `GMT+02:00`, zero as `GMT`), with the locale's `hourFormat` and `gmtFormat`; the metazone in force (1970-01-01 up to 9999-12-31T23:00Z) decides, the daylight flag picks the specific name, a generic name needs the offsets half a year either side (a zone that does not change is generic in both). A zone without a metazone name gets its location name (the country name for a country's primary zone, else `City Time`), and the localized GMT format as the last resort.
- **Parts** are `weekday`, `era`, `year`, `month`, `day`, `dayPeriod`, `hour`, `minute`, `second`, `fractionalSecond`, `timeZoneName` and `literal` (`relatedYear` and `yearName` need another calendar). `format` keeps U+202F where the CLDR pattern has it.
- **Ranges.** `FormatRangeToParts` compares the two instants in their zone from the era down and takes the largest differing field. If the format shows nothing that differs, the date is printed once and every part is `shared`. A range that differs in a field the format lacks is widened first: a day-level range of a weekday with a time gets the day, a month-level one the month, a year-level one the year (a weekday alone, or a month alone, is not widened). The interval pattern for the skeleton and the differing field (`intervalFormats`) is used when the locale has one, its day-period, zone and numeric widths adjusted to the format; the text up to the first repeated field letter belongs to `startRange`, the rest to `endRange`, and text outside the fields spanned by both halves is `shared`. A date with a time that differs only in the time is the date once, joined with the plain date-time pattern, and the time as an interval. Otherwise both dates are printed in full around `intervalFormatFallback` (`{0} – {1}`), with literal text before the first and after the last field marked `shared`. A bare long month (`LLLL`) has no interval pattern and takes this fallback. A reversed range is formatted as given.
- **Limits.** Parsing patterns and building parts are loops over the pattern and skeleton; widening a range builds one more service, once (the widened format has the fields it lacked). No recursion follows input.

### DurationFormat

`DurationFormatService.Create(locales, options, defaultLocale)` follows ECMA-402 section 13: `style` (`long`, `short`, `narrow`, `digital`) and per-unit `style`/`display` options (`GetDurationUnitOptions`, `ValidateDurationUnitStyle`), `fractionalDigits` 0 to 9, `numberingSystem` (only `latn` is carried). Digital style and numeric units join hours, minutes and seconds with the separators of the `durationUnit` patterns (`:` in all six locales); sub-second units shown as `numeric` fold into the seconds as a fraction truncated to `fractionalDigits` (default up to 9). The unit pieces are formatted with `NumberFormatService` (style `unit`, `unitDisplay` from the unit style) and joined with the `unit` list pattern of `ListFormatService` (`digital` uses `short`). `DurationRecord.Create` takes the ten fields (null for undefined) and enforces integers, equal signs, the 2^32 limit on years, months and weeks and the 2^53 s limit on the total (exact arithmetic).

One behavior the specification text does not state but the reference shows, and the corpus confirms: seconds shown as a number after a spelled-out unit (`5 min:07`) are appended to the previous list item with the minute/second separator instead of being a list item of their own. Parts carry `unit` for everything inside a unit's number and label; the list separators have none.

### Time zone source

`ITimeZoneSource.TryGetOffset(zoneId, epochMs, out TimeZoneOffset)` answers the offset in milliseconds and whether daylight saving is in force, for a CLDR primary identifier; an unknown zone gives a `RangeError`. The services never read the operating system: the host supplies the rules (a tzdata reader, a recorded table in tests). `BclTimeZoneSource` wraps `TimeZoneInfo` for tests and for hosts that want it; instants outside the years 1 to 9999 use the offset at the nearest end.

## test262 `intl402` replay

`tools/intl-test262/translate.js` runs the data-style tests of `test/intl402` (NumberFormat, PluralRules, ListFormat, RelativeTimeFormat, DisplayNames, Locale, Intl, DateTimeFormat, DurationFormat; run it with `TZ=UTC`) in a vm context where the Intl constructors record their calls; every assertion that compares a call result with a literal becomes a check (`{service, locales, options, method, args, expected}`). `IntlTest262Tests` replays the checks against the services (set `ONEECMA_INTL_TEST262_CASES` to the translator output). Checks for locales outside the data set (`ja`, `zh`, `en-IN`, `pt-PT`, ...) and shapes that belong to the JS layer (user getters, non-string locale objects) are skipped. Tests that read the parts of a result with `part.type === ...` get primitive types (only the values are tagged), and the date-time `format` results are compared with U+202F read as U+0020 because the expected strings come from the reference's `format()`. At the pinned test262 commit: 1,133 files, 362 translated, 24,869 checks; **14,119 pass, 0 fail, 1,129 documented, 9,621 skipped**. By area, the date-time and duration part: DateTimeFormat 3,710 pass, 15 documented, 138 skipped; DurationFormat 320 pass, 83 documented; `Intl.supportedValuesOf` and `getCanonicalLocales` 761 pass, 248 documented. `testdata/intl/test262-expectations.txt` documents each documented check: the numbering-system and calendar tests need non-Latin digits and non-Gregorian calendars, and one roundingIncrement check needs integer conversion in the option layer.

## Known gaps

- Numbering systems other than `latn`; calendars other than Gregorian.
- Locales outside the six (the lookup falls back to the default locale).
- `Intl.Locale` `getCalendars`, `getHourCycles` and `getNumberingSystems` list one entry, the keyword if given and otherwise the region's preference: CLDR carries one for each of the six locales and Node gives the same lists for them. `getCollations` lists only the collation types the Collator can use (the tailorings of the language: `trad` for `es`, `phonebk` for `de`); Node also lists the root types `emoji` and `eor`, which the Collator does not implement (see the text services notes). Outside the six locales the lists follow the default-locale fallback, so Node's longer lists for `ja`, `ar`, `th` and others differ by design.
- `best fit` is the lookup matcher.
- DateTimeFormat has no non-Gregorian calendars, no `era` beyond BC/AD, and `Temporal` objects are not accepted (the JS layer converts `Date`s to time values).
- The JS-facing constructors and `Date` arithmetic are not part of these services.
- ICU's quirks above are not reproduced.

## Service cache

`String.prototype.localeCompare`, `Number.prototype.toLocaleString`, `BigInt.prototype.toLocaleString` and the three `Date.prototype.toLocale*String` methods read their options exactly as the constructors do (same order, same getters), then look the resulting service up in a per-realm cache keyed by the resolved locale list, the host's default locale and zone, and the option record. A miss builds the service; the cache holds at most 64 entries and is emptied when full. Constructors (`new Intl.NumberFormat(...)`) never share services. A service only reads its options, so reuse cannot be observed.

## Throughput

Date-time and duration (same method, 1000 random instants between 2020 and 2033, fixed-offset zone source): `format` of a numeric date and time 600 ns and 1 KB; full date with long time and a zone name in `de` 5.7 µs (the zone name probes the offsets half a year either side); `formatRange` of a date 2.8 µs; `DurationFormat.format` of a five-unit duration in `en` 3.3 µs and 14 KB (a unit-style NumberFormat per unit is cached, the list and parts are built per call). Release build on a shared 16-core Ryzen 9 7950X3D host, load about 5.

Number formatting, as measured for the first services:

Informal (`ONEECMA_INTL_THROUGHPUT=<file>`, Release build, 1000 random doubles with 0 to 8 integer digits and up to 3 decimals, one thread, loaded shared host): `format` of a double 450 to 1,650 ns for decimal, currency and compact patterns (about 1.1 KB allocated each), `format` of a pre-converted decimal 360 to 610 ns, `PluralRules.select` 113 ns and 137 B. No recursion follows input size: rounding, grouping and pattern assembly are loops over the digit string.

## JavaScript surface

`Builtins/Intl*.cs` exposes the services above as the ECMA-402 objects: the `Intl` namespace (`getCanonicalLocales`, `supportedValuesOf`), every constructor (`Locale`, `Collator`, `NumberFormat`, `PluralRules`, `ListFormat`, `RelativeTimeFormat`, `DisplayNames`, `DateTimeFormat`, `DurationFormat`, `Segmenter`) with `supportedLocalesOf`, `resolvedOptions`, the bound `format`/`compare` getters and the legacy constructor chaining for `NumberFormat` and `DateTimeFormat`, and the locale-sensitive overrides of `String.prototype.localeCompare`/`toLocaleUpperCase`/`toLocaleLowerCase` and `Number`, `BigInt`, `Array` and `Date` `toLocale*String`.

- Options are read in the order ECMA-402 specifies (GetOption, GetNumberOption), so getters observe the same sequence as in the specification.
- The default locale is `IJsHost.DefaultLocale`; the default time zone is `IJsHost.TimeZoneId`, with offsets from `IJsHost.TryGetTimeZoneOffset`.
- `Date.prototype.toLocale*String` and `DateTimeFormat.prototype.format` keep U+202F from the CLDR patterns, as the intl402 tests expect; the reference engine prints U+0020 there.
- test262 `intl402` on the engine: 1104 pass, 0 fail, 17 documented (non-Gregorian calendars, non-Latin numbering systems, one collation and one locale not in the data), 78 skipped for locales outside the supported set.
- A JavaScript-level differential (`tools/intl-diff/js-surface.js`, 228 rows recorded from the reference engine) has 7 documented differences, listed in `tools/intl-diff/js-surface.deviations.txt`.
