# Date

Normative description of `Date` as implemented in `Builtins/Date*.cs`: ECMA-262 17th edition 21.4 and Annex B.2.3.

## Host seams

- **Clock:** `IJsHost.CurrentTimeMs()` only (`Date.now`, `new Date()`, `Date()`).
- **Zone:** `IJsHost.TimeZoneId` names the IANA zone used for local time.
- **Offsets:** `IJsHost.TryGetTimeZoneOffset(zoneId, epochMs, out offsetMs, out isDaylight)`. A default interface member answers from the BCL time zone rules of the machine; `DefaultHost` exposes it as a virtual method; a host that must not depend on the machine overrides it with its own rules. When it returns false, local time is UTC.
- The BCL rules cover years 1 to 9999. For later instants the engine asks about the same moment in an equivalent 400-year cycle (calendar and weekday repeat exactly; rules do not change that far out). Before year 1 the BCL answers with its earliest offset.

## Local time

- `LocalTime(t)` is `t` plus the offset at `t`.
- `UTC(t)` for a local time value `t` (21.4.1.26, GetUTCEpochNanoseconds): look at the offsets a day before and a day after `t`; each offset `o` for which the zone's offset at `t - o` is `o` gives a candidate instant; the earliest candidate wins (clocks set back: the first occurrence). With no candidate (clocks set forward) the offset from before the transition is used, which moves the time forward by the size of the gap.
- All time arithmetic is exact `double` arithmetic over +/-8.64e15; `MakeDay` returns NaN for years beyond +/-400000 without computing.

## Constructor, `parse`, `UTC`

- Every form of 21.4.2.1; `Date()` called as a function returns the string for the current time. A one-argument call with a Date copies its time value without conversion; other objects go through `ToPrimitive`, strings through `Date.parse`.
- `Date.UTC` and the multi-argument constructor apply `MakeFullYear` (0 to 99 mean 1900 to 1999).

### Strings accepted by `Date.parse`

1. **The Date Time String Format (21.4.1.32)** exactly: `YYYY`, `YYYY-MM`, `YYYY-MM-DD`, expanded years `+YYYYYY`/`-YYYYYY` (`-000000` is rejected), optionally `THH:mm`, `THH:mm:ss`, `THH:mm:ss.s…` (any number of fraction digits, truncated to milliseconds) and `Z` or `±HH:mm` (`±HHmm` too). `T` and `Z` may be lower case. Date-only forms are UTC, date-time forms without an offset are local time. Hour 24 is accepted only as `24:00:00.000`. A day from 1 to 31 is accepted in every month and rolls over (`2023-02-30` is 1 March).
2. **Legacy forms** when the string is not in the format above; observed from Node v24 and kept to the cases real code relies on:
   - Words: a month name (the first three letters decide: `Jul`, `July`, `Sept`), a day name (ignored), `AM`/`PM` (after a time, hour at most 12), `Z`, `UT`, `UTC`, `GMT`, and the US zone abbreviations `EST EDT CST CDT MST MDT PST PDT`. Any other word is ignored before the first number and makes the string invalid after it. A word touching a number (`4th`, `10:00pm`) is invalid, except a `Z` right after a time.
   - Numbers: up to three, separated by spaces, `,`, `/`, `.` or `-` (a `-` after a number must touch its neighbours). With a month name: `D Y`, `Y D` (first number above 31, or three or more digits) or one number (a day up to 31, or a year). Without: `M/D/Y`, `Y/M/D` (first number above 31, three or more digits, or 0), `M/D`, `Y/M` or a single month or year. A missing year is 2001, a missing day is 1. Years below 100 written with fewer than four digits mean 2000 to 2049 and 1950 to 1999.
   - Time: `H:M`, `H:M:S`, `H:M:S.fff`; a colon with no digits after it reads as zero, further `:n` groups are ignored; a dot after the seconds must be followed by digits.
   - Zone: `+H`, `+HH`, `+HMM`, `+HHMM`, `+HH:MM`, also after `GMT`/`UTC`; up to four digits. Without a zone the result is local time.
   - Parenthesised text is skipped (an unclosed parenthesis runs to the end); an unmatched `)` or any other character makes the string invalid.
   - Day 1 to 31 and month 1 to 12 are accepted and day overflow rolls into the next month.
   - The output of `toString`, `toUTCString` and `toISOString` parses back to the same time value (to the second for `toString` and `toUTCString`).

Known differences from the reference: `-000000-01-01` (the reference reads a stray sign, 2001-01-01); a number written with a sign following a number or a zone name is only partly modelled; `Jan 1 1 1` (three numbers and a month, the reference ignores one).

## Prototype

All getters and setters of 21.4.4 in UTC and local variants, with the argument conversion order of the specification (every argument is converted before the NaN check). Annex B: `getYear`, `setYear`, `toGMTString` (the same function object as `toUTCString`). `getTimezoneOffset` is exactly `(t - LocalTime(t)) / msPerMinute`, which is fractional where a zone has a seconds offset. `toJSON`, `@@toPrimitive` (hints `default`, `string`, `number`; others throw `TypeError: Invalid hint: x`), `valueOf`.

Methods called on a non-Date throw `TypeError: this is not a Date object.` `toISOString` of an invalid date throws `RangeError: Invalid time value`.

## String forms

- `toString`: `Thu Jul 04 2024 10:00:00 GMT-0500 (Central Daylight Time)`. The offset is `±HHMM` rounded to the minute; the parenthesised text is the zone's long English name from the CLDR data (`en`), looked up for the instant clamped to 1970 to 9999; a zone without a name there falls back to `GMT-05:00`. Years print with at least four digits and a leading `-` when negative (`-0001`).
- `toDateString`: `Thu Jul 04 2024`; `toTimeString`: `10:00:00 GMT-0500 (Central Daylight Time)`; `toUTCString`: `Thu, 04 Jul 2024 10:00:00 GMT`; `toISOString`: `2024-07-04T15:00:00.000Z`, with `±YYYYYY` outside years 0 to 9999. An invalid date prints `Invalid Date` in all but `toISOString`.
- `toLocaleString`, `toLocaleDateString`, `toLocaleTimeString`: `DateTimeFormatService` with the locale argument (a string or list of strings, else `IJsHost.DefaultLocale`), the options object read as plain values, and the host zone. U+202F in the CLDR time patterns is replaced by U+0020, as the reference engines print it.

## Differential

`testdata/date/probe.js` prints about 2,400 observations (getters, setters, constructor gaps and overlaps, `parse`, locale strings) across DST edges, the epoch, 1900, 2038, the ends of the range and far years. `tools/date-diff/record.sh` records Node's output under `TZ=America/Chicago`, `Europe/Berlin` and `UTC` into `expected.*.txt` (Node v24.22.1). `DateNodeDifferentialTests` runs the same script on the engine with the host zone set to the same zone. Lines for instants before 1900 are compared in UTC only: the zone rules read through the BCL round local mean time to whole minutes and the reference prints seconds (`GMT-0550` against `GMT-0551` in Chicago before 1883). A host with its own tzdata reader does not have this limit. On Windows the default host reads the operating system's zone rules (through CLDR's `windowsZones` mapping when the machine has no IANA ids), which differ from tzdata for the past: Berlin in 1945 has +0200 where tzdata has a second summer hour (+0300). The differential therefore compares Windows runs from 1970 on only; a host that needs tzdata history supplies its own `TryGetTimeZoneOffset`. `testdata/date/deviations.txt` lists the other known differences.
