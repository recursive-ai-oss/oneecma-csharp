# Locale data

Normative description of the data the Intl implementation reads: where it comes from, the embedded format, and how to add a locale. A port must be able to reproduce the format from this text.

## Sources and pins

| Input | Version | Pinned by |
|---|---|---|
| CLDR JSON (`cldr-48.0.0-json-full.zip`) | 48.0 | SHA-256 in `tools/locale-data/pin.txt` |
| CLDR common (`cldr-common-48.zip`: `uca/allkeys_CLDR.txt`, `collation/*.xml`) | 48 | SHA-256 and the official SHA-512 in `pin.txt` |
| tzdata (`tzdata2025b.tar.gz`, only `zone.tab`) | 2025b | SHA-256 in `pin.txt` |
| UCD files (grapheme, word, sentence break properties, `emoji-data.txt`, `DerivedCoreProperties.txt`, `PropList.txt`, `Blocks.txt`, `DerivedGeneralCategory.txt`) | 17.0.0 | SHA-256 in `pin.txt` |

CLDR 48.0 and Unicode 17.0 are what the reference Node releases (v22.22.1 and v24.22.1) report as `process.versions.cldr` / `process.versions.unicode`, so Intl output compared against them carries no data-version noise. Raw inputs are fetched by `tools/locale-data/fetch.sh` (checksum-verified, never committed). The licence for all of it is the Unicode License v3 (see `THIRD-PARTY-NOTICES.md`).

## Regenerating

```
tools/locale-data/fetch.sh                      # -> $ONEECMA_LOCALE_DATA or ~/oneecma/locale-data
dotnet run --project tools/LocaleDataGen -- \
  --cldr <root>/cldr/48.0 --ucd <root>/ucd/17.0.0 --tzdata <dir holding zone.tab> \
  --out src/RecursiveAI.OneEcma/Intl/Data/Generated
```

The output is a pure function of the pinned inputs: two runs give identical bytes (checked with `diff -r`). The `.bin` files are committed and embedded in the engine assembly as manifest resources named `oneecma-data/<file>`.

## Files

| File | Content | Bytes |
|---|---|---:|
| `supplemental.bin` | tree (collation tailoring rule text also covers the root `search` collation): likely subtags, aliases, parent locales, default content, currency data (digits, regions), week data, time data, calendar preferences, numbering systems, metazone mapping, plural ranges, day period rules, territory containment, right-to-left scripts, BCP 47 keyword data (calendar, collation, currency, measure, number, timezone including IANA aliases), collation tailoring rule text, list of stored locales | 351,918 |
| `locale-<id>.bin` | tree per stored locale, delta against its parent | und 31,520; en 148,874; en-001 15,736; en-GB 4,192; es 125,352; es-419 17,439; es-US 13,955; fr 133,642; de 130,050; pt 125,568 |
| `plurals.bin` | cardinal and ordinal plural rule ASTs for every CLDR language | 6,968 |
| `collation.bin` | CLDR root collation table, contractions, implicit-weight ranges, decimal digit zeros | 282,838 |
| `breaks.bin` | UAX #29 property ranges | 17,607 |
| `normalization.bin` | UAX #15 combining classes, one-step decomposition mappings, composition exclusions | 35,464 |

Total 1,445,121 bytes. The largest parts are likely subtags (222 KB of the supplemental tree), the root collation table (283 KB), and per language the units (about 35 KB) and currency names (about 31 KB). `en-US` is CLDR default content of `en` and `pt-BR` of `pt`, so they have no blob of their own; `en-GB`, `es-US` store only their differences.

## Container

All integers are little endian. A varint is unsigned LEB128, at most 5 bytes. Every blob starts with 8 bytes: `OEDT`, format version (1), kind (1 tree, 2 plurals, 3 collation, 4 breaks, 5 normalization), two zero bytes. A reader rejects any other version.

### Tree blobs (kind 1)

After the 8 byte header, five u32: `stringCount`, `tableOffset`, `dataOffset`, `nodesOffset`, `rootOffset` (the last relative to `nodesOffset`; the others absolute). Then:

- **String table** at `tableOffset`: `stringCount + 1` u32 offsets into the string data (the last is the data length). At `dataOffset`: the UTF-8 strings, sorted by byte order, so a reader binary-searches a key without decoding. Keys and values share the table.
- **Nodes** from `nodesOffset`. A node starts with a varint `(count << 2) | tag`. Tag 2 is a string and `count` is its string id. Tag 0 is an object: `count` key ids (varints, ascending) then `count` child offsets (varints, relative to `nodesOffset`). Tag 1 is an array: `count` child offsets. Children precede their parent and identical subtrees are stored once, so offsets may be shared.
- JSON numbers and booleans are stored as their text; JSON objects keep their keys, and properties that only carry prose (`_description`, `_since`, `_reason`, `_iso4217`, `_comment`, `_references`) are dropped.

**Inheritance.** A locale blob holds only members whose value differs from its parent's resolved data (CLDR JSON `full` packages are already resolved against the locale's own parents, so the generator diffs resolved against resolved). Each locale blob has a member `@parent` naming its parent locale (empty for `und`). Parents follow CLDR `parentLocales`, else drop the last subtag: en-GB → en-001 → en → und; es-US → es-419 → es → und. A lookup walks the chain from the most specific blob until some blob has the path. Arrays and strings are replaced whole, never merged. The value `∅∅∅` is CLDR's explicit "nothing" marker and is stored as is.

**Paths.** Locale trees have the members `numbers` (symbols, decimal/percent/scientific/currency/rational formats, compact patterns; Latin digits only), `currencies`, `gregorian` (months, days, quarters, day periods, eras, date/time/date-time formats, availableFormats, appendItems, intervalFormats), `dateFields` (fields and relative-time patterns), `timeZoneNames` (zone, metazone and region formats and names), `languages`, `scripts`, `territories`, `variants`, `displayNames` (keys, types such as calendars, code patterns, locale display pattern), `listPatterns`, `layout`, `units` (all CLDR units in long/short/narrow plus duration unit patterns, which also serve DurationFormat). The members below each keep their CLDR JSON names and structure.

### Plurals (kind 2)

Two sections, cardinal then ordinal. Each: varint language count, then per language (sorted): varint-length UTF-8 id, a byte rule count, and per rule a byte category (0 zero, 1 one, 2 two, 3 few, 4 many, 5 other; `other` is implied and never stored), a varint AST byte length and the AST. The AST of a rule (UTS #35 Part 3): varint `or` count; per `or` term a varint `and` count; per relation a byte operand (0 n, 1 i, 2 v, 3 w, 4 f, 5 t, 6 c, 7 e), a varint modulus (0 none), a byte negated flag (`!=`), a varint range count and per range varint low and high (equal for a single value). Rules are tried in category order. Only `=` and `!=` relations exist in CLDR 48; the generator fails on anything else. Samples are dropped.

### Collation (kind 3)

The CLDR root collation (`allkeys_CLDR.txt`, UCA 17.0.0, default alternate handling non-ignorable). Layout: varint-length version string; implicit classes (varint count; each u16 base, byte shift, byte mode, varint origin); implicit runs (varint count; each varint gap since previous end, varint length-1, byte class; class 0 covers every code point not in a run); single-code-point entries (varint count; each varint code point delta, byte CE count, CEs); contractions (varint count; each byte key length, varint code points, byte CE count, CEs). A CE is varint primary, varint secondary, byte tertiary with bit 7 set for variable (`*`) elements. After the contractions come the decimal digits for numeric collation: varint count, then varint code point deltas of each digit zero (every general category Nd run is ten digits long, so only its zero is stored). A reader of an older blob that ends after the contractions has no digits.

Implicit weights (UTS #10 §10.1.3, Table 16) for code points without an entry: `AAAA = base + (cp >> shift)` (shift 0 means no addition); `BBBB = ((mode 0 ? cp - origin : cp & 0x7FFF) | 0x8000)`; the pair is `[AAAA.0020.0002][BBBB.0000.0000]`. Classes: Tangut and Tangut Supplement (FB00), Tangut Components and Supplement (FB01), Nushu (FB02), Khitan Small Script (FB03) (assigned code points only), core Han = Unified_Ideograph in the CJK Unified Ideographs or CJK Compatibility Ideographs blocks (FB40 + cp>>15), other Unified_Ideograph (FB80 + cp>>15), everything else (FBC0 + cp>>15).

The reader's `MatchLongest` expects NFD text and matches contiguous contractions only; discontiguous contraction matching, variable weighting options and the Collator options (`caseFirst`, `numeric`, `sensitivity`, ...) belong to the Collator (docs/text-services.md).

**Tailoring.** The root table is the default collation of en, fr, de and pt. Spanish (`standard`) adds `&N<ñ<<<Ñ`. The rule text of every non-alternate, non-draft `<collation>` of en, es, fr, de and pt, and the `search` collation of the root (for example `es` `traditional`, `de` `phonebook`) is stored in `supplemental.bin` under `collationTailoring/<language>/<type>` exactly as CLDR writes it (decomposed, comments removed). The Collator applies it with the UTS #35 rule syntax; the data layer does not.

**Deviation.** CLDR orders Han in the root by radical-stroke (`[radical ...]` data in FractionalUCA). The shipped table has the UCA code point order for Han (the implicit weights above). The Node differential must exclude Han ordering until that is added.

### Normalization (kind 5)

After the header: varint range count and per range varint gap since the previous end, varint length-1, byte canonical combining class (only non-zero classes; code points not listed have class 0); varint mapping count and per mapping: varint code point delta, byte flags (bit 0: compatibility mapping, bit 1: Full_Composition_Exclusion), byte length, varint code points. Mappings are the single-step values of `UnicodeData.txt` field 5 with the `<tag>` removed; Hangul syllables are left to the algorithm of Unicode section 3.12. The reader expands every mapping recursively, once, when it loads, and derives the primary composites: canonical mappings of two code points that are not composition-excluded.

### Breaks (kind 4)

Varint property count; per property: UTF-8 name (`Grapheme_Cluster_Break`, `Word_Break`, `Sentence_Break`, `Extended_Pictographic`, `Indic_Conjunct_Break`), varint value count and the value names (index 0 is the default for unlisted code points), varint range count and per range: varint gap since the previous end, varint length-1, byte value index. Adjacent ranges with the same value are merged.

## Time zone data

Date-time formatting needs the display names of time zones and the country of each zone. `supplemental.bin` carries, besides the CLDR metazone mapping:

- `zoneRegions`: CLDR canonical zone id to the ISO 3166 region of the `zone.tab` line that names it or one of its aliases (tzdata 2025b, public domain; `fetch.sh` extracts `zone.tab` from the pinned archive).
- `primaryZones`: region to the zone CLDR names as that region's primary zone (a zone whose location name is the bare country name).
- `metaZoneInfo`: per zone, a list of periods `usesMetazone` with `_mzone` and optional `_from` and `_to` (`yyyy-MM-dd HH:mm`, UTC; a missing `_from` is 1970-01-01).
- `metaZoneMap`: the reference ("golden") zone of each metazone per territory, `001` for the world.

The data holds no offsets: the services ask an `ITimeZoneSource` for the UTC offset and daylight flag at an instant (see `docs/intl.md`).

**Empty markers.** A child locale that blocks a name its parent has (CLDR spells this with the inheritance marker, e.g. en-GB short names that are absent from en-001 although present in en) is stored as an explicit empty string inside `timeZoneNames`, so that the delta against the parent does not make the parent's name reappear. A reader treats an empty string as "no name" and falls back to the localized GMT format.

## Adding a locale

1. Add its id (and any intermediate parent the CLDR chain needs) to `Locales` in `tools/LocaleDataGen/Program.cs`. The id must exist in the CLDR JSON `full` packages; region locales whose data is the language's default content need no entry.
2. For a Latin-script locale with a different collation, add its language to `CollationLanguages`; for plural data nothing is needed (all languages ship).
3. Add the tag to `CldrData.SupportedLocales`, run the generator, and add probes to `tests/OneEcma.Tests/LocaleDataTests.cs` with expected values from the reference Node.

Another numbering system or calendar is a generator change: `DropNonLatinNumberSystems` and the `ca-*` sources.

## What is not in the data

- Territory-to-time-zone lists (`Intl.Locale.prototype.getTimeZones`) need the tz database, which CLDR does not carry; the IANA zone names and aliases come from the BCP 47 timezone data (`_iana`, `_alias`), not from a tz release.
- Language matching data (`languageMatching`), unit conversion and preference data, person names, transforms, annotations and non-Gregorian calendars.

## Reader

`RecursiveAI.OneEcma.Intl.Data` (internal): `CldrData` (entry point; `GetLocale`, `TryResolveLocale`, `Supplemental`, `Plurals`, `Collation`, `Breaks`), `DataTree` (path lookups across the inheritance chain), `TreeBlob`, `PluralData` / `PluralOperands`, `CollationData`, `BreakData`, `NormalizationData`. Loading is lazy and cached; every object is immutable and thread-safe. No reflection other than reading the manifest resource.
