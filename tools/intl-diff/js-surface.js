// Differential script: runs unchanged under Node and under `oneecma run`. Prints one line per probe.
const out = typeof print === 'function' ? print : console.log;
const show = (v) => {
  if (typeof v === 'string') return JSON.stringify(v);
  if (typeof v === 'symbol') return String(v);
  if (v === undefined) return 'undefined';
  try { return JSON.stringify(v); } catch (e) { return String(v); }
};
const probe = (label, f) => {
  try { out(label + ' => ' + show(f())); } catch (e) { out(label + ' !! ' + e.name + ': ' + e.message); }
};
const L = ['en-US', 'en-GB', 'es', 'es-US', 'fr', 'de', 'pt-BR'];
const D = Date.UTC(2024, 2, 9, 15, 4, 5, 678);

// Intl object and locale lists
probe('getCanonicalLocales', () => Intl.getCanonicalLocales(['EN-us', 'de-DE-u-co-phonebk', 'iw', 'en-us']));
probe('getCanonicalLocales invalid', () => Intl.getCanonicalLocales('en_US'));
probe('getCanonicalLocales number', () => Intl.getCanonicalLocales([1]));
probe('supportedLocalesOf', () => Intl.NumberFormat.supportedLocalesOf(['en-US', 'es-419', 'fr-CA', 'de', 'zxx']));
probe('supportedValuesOf calendar', () => Intl.supportedValuesOf('calendar').slice(0, 1));
probe('supportedValuesOf bad', () => Intl.supportedValuesOf('foo'));
probe('toStringTag', () => [Intl, Intl.Collator.prototype, new Intl.Segmenter()].map((o) => Object.prototype.toString.call(o)));

// Locale
probe('Locale parts', () => { const l = new Intl.Locale('de-Latn-DE-u-co-phonebk-hc-h23-kn'); return [l.language, l.script, l.region, l.collation, l.hourCycle, l.numeric, l.baseName, String(l)]; });
probe('Locale maximize', () => [new Intl.Locale('en').maximize().toString(), new Intl.Locale('fr-Latn-FR').minimize().toString(), new Intl.Locale('pt').maximize().toString()]);
probe('Locale options', () => String(new Intl.Locale('en', { region: 'GB', hourCycle: 'h12', numeric: true, caseFirst: 'upper' })));
probe('Locale getWeekInfo', () => ['en-US', 'de', 'fr-FR'].map((t) => new Intl.Locale(t).getWeekInfo()));
probe('Locale getTextInfo', () => ['en', 'ar'].map((t) => new Intl.Locale(t).getTextInfo()));
probe('Locale getHourCycles', () => ['en-US', 'de', 'es-ES'].map((t) => new Intl.Locale(t).getHourCycles()));
probe('Locale calendars', () => new Intl.Locale('en-US').getCalendars());
probe('Locale invalid', () => new Intl.Locale('x'));
probe('Locale no new', () => Intl.Locale('en'));

// Collator
probe('Collator sort de', () => ['z', 'a', 'ä', 'Z', 'A'].sort(new Intl.Collator('de').compare));
probe('Collator sort sv-less es', () => ['ñ', 'n', 'o', 'N'].sort(new Intl.Collator('es').compare));
probe('Collator numeric', () => ['a10', 'a2', 'a1'].sort(new Intl.Collator('en', { numeric: true }).compare));
probe('Collator base', () => new Intl.Collator('en', { sensitivity: 'base' }).compare('a', 'Á'));
probe('Collator resolvedOptions', () => new Intl.Collator('de-u-co-phonebk', { caseFirst: 'upper' }).resolvedOptions());
probe('localeCompare', () => ['a'.localeCompare('b'), 'b'.localeCompare('a'), 'a'.localeCompare('a'), 'ä'.localeCompare('z', 'de'), 'ä'.localeCompare('z', 'de-u-co-phonebk')]);
probe('Collator bad usage', () => new Intl.Collator('en', { usage: 'x' }));

// NumberFormat
for (const l of L) probe('NF decimal ' + l, () => new Intl.NumberFormat(l).format(1234567.891));
for (const l of L) probe('NF currency ' + l, () => new Intl.NumberFormat(l, { style: 'currency', currency: 'EUR' }).format(-1234.5));
for (const l of ['en-US', 'de', 'fr']) probe('NF percent ' + l, () => new Intl.NumberFormat(l, { style: 'percent', maximumFractionDigits: 1 }).format(0.4567));
for (const l of ['en-US', 'es']) probe('NF compact ' + l, () => [1234, 1234567, 12345678901].map((n) => new Intl.NumberFormat(l, { notation: 'compact' }).format(n)));
probe('NF unit', () => new Intl.NumberFormat('en', { style: 'unit', unit: 'kilometer-per-hour', unitDisplay: 'long' }).format(88));
probe('NF scientific', () => new Intl.NumberFormat('en', { notation: 'scientific' }).format(123456));
probe('NF bigint', () => new Intl.NumberFormat('en').format(12345678901234567890n));
probe('NF string exact', () => new Intl.NumberFormat('en', { maximumFractionDigits: 20 }).format('1.00000000000000000001'));
probe('NF rounding', () => new Intl.NumberFormat('en', { maximumFractionDigits: 0, roundingMode: 'floor' }).format(2.9));
probe('NF signDisplay', () => [-1, 0, 1].map((n) => new Intl.NumberFormat('en', { signDisplay: 'exceptZero' }).format(n)));
probe('NF parts', () => new Intl.NumberFormat('de', { style: 'currency', currency: 'USD' }).formatToParts(1234.5));
probe('NF formatRange', () => new Intl.NumberFormat('en', { style: 'currency', currency: 'USD', maximumFractionDigits: 0 }).formatRange(3, 5));
probe('NF formatRangeToParts', () => new Intl.NumberFormat('en').formatRangeToParts(1, 2).map((p) => p.type + ':' + p.source));
probe('NF resolvedOptions', () => new Intl.NumberFormat('fr', { style: 'currency', currency: 'CAD', minimumFractionDigits: 1 }).resolvedOptions());
probe('NF resolvedOptions sig', () => new Intl.NumberFormat('en', { maximumSignificantDigits: 3 }).resolvedOptions());
probe('NF bound format', () => [1, 2, 3].map(new Intl.NumberFormat('en', { minimumIntegerDigits: 2 }).format));
probe('NF legacy call', () => { const o = Object.create(Intl.NumberFormat.prototype); const r = Intl.NumberFormat.call(o, 'de'); return [r === o, Intl.NumberFormat.prototype.resolvedOptions.call(o).locale]; });
probe('NF bad style', () => new Intl.NumberFormat('en', { style: 'foo' }));
probe('NF bad currency', () => new Intl.NumberFormat('en', { style: 'currency', currency: 'EU' }));
probe('NF no currency', () => new Intl.NumberFormat('en', { style: 'currency' }));
probe('NF bad digits', () => new Intl.NumberFormat('en', { minimumFractionDigits: 101 }));
probe('NF bad unit', () => new Intl.NumberFormat('en', { style: 'unit', unit: 'bogus' }));
probe('NF incompatible', () => Intl.NumberFormat.prototype.formatToParts.call({}, 1));
probe('NF formatRange undefined', () => new Intl.NumberFormat('en').formatRange(undefined, 1));
probe('toLocaleString', () => [(1234.5).toLocaleString('de'), (1234.5).toLocaleString('en', { style: 'currency', currency: 'USD' }), 12345n.toLocaleString('fr'), [1234.5, 6789].toLocaleString('es')]);

// PluralRules
for (const l of ['en', 'es', 'fr', 'de', 'pt-BR']) probe('PR cardinal ' + l, () => [0, 1, 2, 5, 1.5, 1000000].map((n) => new Intl.PluralRules(l).select(n)));
probe('PR ordinal en', () => [1, 2, 3, 4, 11, 21, 22, 23].map((n) => new Intl.PluralRules('en', { type: 'ordinal' }).select(n)));
probe('PR range', () => new Intl.PluralRules('en').selectRange(1, 5));
probe('PR resolvedOptions', () => new Intl.PluralRules('fr').resolvedOptions());
probe('PR no new', () => Intl.PluralRules());
probe('PR range undefined', () => new Intl.PluralRules('en').selectRange(1));

// ListFormat
for (const l of L) probe('LF ' + l, () => new Intl.ListFormat(l).format(['a', 'b', 'c']));
probe('LF disjunction', () => new Intl.ListFormat('en', { type: 'disjunction', style: 'short' }).format(['x', 'y']));
probe('LF unit narrow', () => new Intl.ListFormat('de', { type: 'unit', style: 'narrow' }).format(['1', '2', '3']));
probe('LF parts', () => new Intl.ListFormat('es').formatToParts(['uno', 'dos', 'tres']));
probe('LF non-string', () => new Intl.ListFormat('en').format([1]));
probe('LF resolvedOptions', () => new Intl.ListFormat('fr', { style: 'short' }).resolvedOptions());

// RelativeTimeFormat
for (const l of L) probe('RTF ' + l, () => [new Intl.RelativeTimeFormat(l).format(-3, 'day'), new Intl.RelativeTimeFormat(l, { numeric: 'auto' }).format(1, 'day')]);
probe('RTF parts', () => new Intl.RelativeTimeFormat('en', { style: 'short' }).formatToParts(5, 'minutes'));
probe('RTF bad unit', () => new Intl.RelativeTimeFormat('en').format(1, 'fortnight'));
probe('RTF infinite', () => new Intl.RelativeTimeFormat('en').format(Infinity, 'day'));
probe('RTF resolvedOptions', () => new Intl.RelativeTimeFormat('de', { numeric: 'auto' }).resolvedOptions());

// DisplayNames
for (const l of ['en', 'es', 'fr', 'de']) probe('DN region ' + l, () => new Intl.DisplayNames(l, { type: 'region' }).of('US'));
probe('DN language', () => [new Intl.DisplayNames('en', { type: 'language' }).of('fr-CA'), new Intl.DisplayNames('de', { type: 'language', languageDisplay: 'standard' }).of('en-GB')]);
probe('DN currency', () => new Intl.DisplayNames('fr', { type: 'currency' }).of('USD'));
probe('DN script', () => new Intl.DisplayNames('en', { type: 'script' }).of('Latn'));
probe('DN calendar', () => new Intl.DisplayNames('en', { type: 'calendar' }).of('gregory'));
probe('DN dateTimeField', () => new Intl.DisplayNames('es', { type: 'dateTimeField' }).of('month'));
probe('DN fallback none', () => new Intl.DisplayNames('en', { type: 'region', fallback: 'none' }).of('ZZ'));
probe('DN no type', () => new Intl.DisplayNames('en', {}));
probe('DN no options', () => new Intl.DisplayNames('en'));
probe('DN invalid code', () => new Intl.DisplayNames('en', { type: 'region' }).of('usa'));

// DateTimeFormat
for (const l of L) probe('DTF medium ' + l, () => new Intl.DateTimeFormat(l, { dateStyle: 'medium', timeStyle: 'short', timeZone: 'UTC' }).format(D));
for (const l of ['en-US', 'de', 'fr']) probe('DTF full ' + l, () => new Intl.DateTimeFormat(l, { dateStyle: 'full', timeStyle: 'long', timeZone: 'Europe/Paris' }).format(D));
probe('DTF components', () => new Intl.DateTimeFormat('en-US', { weekday: 'short', month: 'long', day: 'numeric', hour: 'numeric', minute: '2-digit', timeZone: 'America/New_York' }).format(D));
probe('DTF hour12', () => [true, false].map((h) => new Intl.DateTimeFormat('en-GB', { hour: 'numeric', minute: 'numeric', hour12: h, timeZone: 'UTC' }).format(D)));
probe('DTF fractional', () => new Intl.DateTimeFormat('en', { minute: 'numeric', second: 'numeric', fractionalSecondDigits: 3, timeZone: 'UTC' }).format(D));
probe('DTF timeZoneName', () => ['short', 'long', 'shortOffset', 'longOffset'].map((z) => new Intl.DateTimeFormat('en-US', { timeZone: 'Asia/Kolkata', timeZoneName: z, hour: 'numeric' }).format(D)));
probe('DTF offset zone', () => new Intl.DateTimeFormat('en', { timeZone: '+05:30', timeStyle: 'full' }).format(D));
probe('DTF parts', () => new Intl.DateTimeFormat('es', { dateStyle: 'long', timeZone: 'UTC' }).formatToParts(D).map((p) => p.type + '=' + p.value).join('|'));
probe('DTF range', () => new Intl.DateTimeFormat('en', { dateStyle: 'medium', timeZone: 'UTC' }).formatRange(D, D + 5 * 86400000));
probe('DTF rangeToParts', () => new Intl.DateTimeFormat('en', { month: 'short', day: 'numeric', timeZone: 'UTC' }).formatRangeToParts(D, D + 86400000).map((p) => p.type + ':' + p.source).join(','));
probe('DTF resolvedOptions', () => new Intl.DateTimeFormat('de', { timeZone: 'Europe/Berlin', year: 'numeric', month: '2-digit', day: '2-digit' }).resolvedOptions());
probe('DTF resolvedOptions hc', () => new Intl.DateTimeFormat('en-US', { timeStyle: 'short', timeZone: 'UTC' }).resolvedOptions());
probe('DTF invalid date', () => new Intl.DateTimeFormat('en').format(NaN));
probe('DTF bad zone', () => new Intl.DateTimeFormat('en', { timeZone: 'Mars/Base' }));
probe('DTF style mix', () => new Intl.DateTimeFormat('en', { dateStyle: 'short', year: 'numeric' }));
probe('DTF legacy call', () => { const o = Object.create(Intl.DateTimeFormat.prototype); Intl.DateTimeFormat.call(o, 'fr'); return Intl.DateTimeFormat.prototype.resolvedOptions.call(o).locale; });
probe('DTF no args', () => typeof new Intl.DateTimeFormat().format());
probe('Date toLocale*', () => { const d = new Date(D); return [d.toLocaleDateString('de', { timeZone: 'UTC' }), d.toLocaleTimeString('fr', { timeZone: 'UTC' }), d.toLocaleString('en-GB', { timeZone: 'UTC' }), new Date(NaN).toLocaleString('en')]; });
probe('Date toLocaleDateString style conflict', () => new Date(D).toLocaleDateString('en', { timeStyle: 'short' }));

// DurationFormat
probe('DF long', () => new Intl.DurationFormat('en', { style: 'long' }).format({ hours: 1, minutes: 46, seconds: 40 }));
probe('DF digital', () => new Intl.DurationFormat('en', { style: 'digital' }).format({ hours: 1, minutes: 5, seconds: 9, milliseconds: 250 }));
for (const l of ['es', 'fr', 'de', 'pt-BR']) probe('DF short ' + l, () => new Intl.DurationFormat(l).format({ days: 2, hours: 3, minutes: 4 }));
probe('DF parts', () => new Intl.DurationFormat('en', { style: 'narrow' }).formatToParts({ minutes: 5, seconds: 2 }).map((p) => p.type + ':' + p.value + ':' + p.unit).join('|'));
probe('DF resolvedOptions', () => new Intl.DurationFormat('en', { style: 'digital', fractionalDigits: 2 }).resolvedOptions());
probe('DF empty', () => new Intl.DurationFormat('en').format({}));
probe('DF string', () => new Intl.DurationFormat('en').format('P1D'));
probe('DF fraction', () => new Intl.DurationFormat('en').format({ hours: 1.5 }));
probe('DF mixed signs', () => new Intl.DurationFormat('en').format({ hours: 1, minutes: -1 }));

// Segmenter
probe('Seg grapheme', () => [...new Intl.Segmenter('en').segment('a\u0301e\u{1F468}\u200D\u{1F469}')].map((s) => s.segment.length));
probe('Seg word', () => [...new Intl.Segmenter('en', { granularity: 'word' }).segment('Hello, world! 42')].map((s) => s.segment + (s.isWordLike ? '+' : '-')).join('|'));
probe('Seg sentence', () => [...new Intl.Segmenter('en', { granularity: 'sentence' }).segment('One. Two? Three!')].map((s) => s.segment));
probe('Seg containing', () => new Intl.Segmenter('en', { granularity: 'word' }).segment('hello world').containing(7));
probe('Seg containing out', () => new Intl.Segmenter('en').segment('abc').containing(9));
probe('Seg resolvedOptions', () => new Intl.Segmenter('fr', { granularity: 'word' }).resolvedOptions());
probe('Seg iterator tag', () => Object.prototype.toString.call(new Intl.Segmenter().segment('a')[Symbol.iterator]()));

// String case mapping
probe('toLocaleUpperCase tr', () => ['i'.toLocaleUpperCase('tr'), 'I'.toLocaleLowerCase('tr'), '\u0130'.toLocaleLowerCase('tr'), 'ß'.toLocaleUpperCase('de'), 'i'.toLocaleUpperCase('en')]);
probe('toLocaleLowerCase lt', () => 'I\u0300'.toLocaleLowerCase('lt'));
probe('toLocaleUpperCase bad', () => 'a'.toLocaleUpperCase('x'));
probe('toLocaleLowerCase null this', () => String.prototype.toLocaleLowerCase.call(null));

// Error wording (V8's messages are the reference)
const bad = [
  ['NF style', () => new Intl.NumberFormat('en', { style: 'x' })],
  ['NF notation', () => new Intl.NumberFormat('en', { notation: 'x' })],
  ['NF signDisplay', () => new Intl.NumberFormat('en', { signDisplay: 'x' })],
  ['NF useGrouping', () => new Intl.NumberFormat('en', { useGrouping: 'x' })],
  ['NF roundingMode', () => new Intl.NumberFormat('en', { roundingMode: 'x' })],
  ['NF roundingIncrement', () => new Intl.NumberFormat('en', { roundingIncrement: 3 })],
  ['NF minimumIntegerDigits', () => new Intl.NumberFormat('en', { minimumIntegerDigits: 0 })],
  ['NF mxsd', () => new Intl.NumberFormat('en', { maximumSignificantDigits: 22 })],
  ['NF mnfd>mxfd', () => new Intl.NumberFormat('en', { minimumFractionDigits: 5, maximumFractionDigits: 2 })],
  ['NF numberingSystem', () => new Intl.NumberFormat('en', { numberingSystem: 'a' })],
  ['NF localeMatcher', () => new Intl.NumberFormat('en', { localeMatcher: 'x' })],
  ['NF locale type', () => new Intl.NumberFormat(5)],
  ['NF locale null', () => new Intl.NumberFormat(null)],
  ['NF unit missing', () => new Intl.NumberFormat('en', { style: 'unit' })],
  ['NF format bigint sym', () => new Intl.NumberFormat('en').format(Symbol())],
  ['NF range NaN', () => new Intl.NumberFormat('en').formatRange(NaN, 1)],
  ['NF range end undefined', () => new Intl.NumberFormat('en').formatRange(1, undefined)],
  ['DTF weekday', () => new Intl.DateTimeFormat('en', { weekday: 'x' })],
  ['DTF hourCycle', () => new Intl.DateTimeFormat('en', { hourCycle: 'h99' })],
  ['DTF fractionalSecondDigits', () => new Intl.DateTimeFormat('en', { fractionalSecondDigits: 4 })],
  ['DTF timeZoneName', () => new Intl.DateTimeFormat('en', { timeZoneName: 'x' })],
  ['DTF dateStyle', () => new Intl.DateTimeFormat('en', { dateStyle: 'x' })],
  ['DTF calendar', () => new Intl.DateTimeFormat('en', { calendar: 'a' })],
  ['DTF format huge', () => new Intl.DateTimeFormat('en').format(8.64e15 + 1)],
  ['DTF formatRange undefined', () => new Intl.DateTimeFormat('en').formatRange(undefined, 1)],
  ['DTF formatRange NaN', () => new Intl.DateTimeFormat('en').formatRange(NaN, 1)],
  ['DTF incompat', () => Intl.DateTimeFormat.prototype.formatToParts.call(1)],
  ['DTF format getter incompat', () => Object.getOwnPropertyDescriptor(Intl.DateTimeFormat.prototype, 'format').get.call({})],
  ['Collator sensitivity', () => new Intl.Collator('en', { sensitivity: 'x' })],
  ['Collator caseFirst', () => new Intl.Collator('en', { caseFirst: 'x' })],
  ['Collator compare incompat', () => Object.getOwnPropertyDescriptor(Intl.Collator.prototype, 'compare').get.call(3)],
  ['PR type', () => new Intl.PluralRules('en', { type: 'x' })],
  ['PR select incompat', () => Intl.PluralRules.prototype.select.call({}, 1)],
  ['LF type', () => new Intl.ListFormat('en', { type: 'x' })],
  ['LF style', () => new Intl.ListFormat('en', { style: 'x' })],
  ['LF options prim', () => new Intl.ListFormat('en', 5)],
  ['LF iterable', () => new Intl.ListFormat('en').format(5)],
  ['LF no new', () => Intl.ListFormat('en')],
  ['RTF style', () => new Intl.RelativeTimeFormat('en', { style: 'x' })],
  ['RTF numeric', () => new Intl.RelativeTimeFormat('en', { numeric: 'x' })],
  ['RTF parts bad unit', () => new Intl.RelativeTimeFormat('en').formatToParts(1, 'x')],
  ['RTF NaN', () => new Intl.RelativeTimeFormat('en').format(NaN, 'day')],
  ['DN type', () => new Intl.DisplayNames('en', { type: 'x' })],
  ['DN style', () => new Intl.DisplayNames('en', { type: 'region', style: 'x' })],
  ['DN fallback', () => new Intl.DisplayNames('en', { type: 'region', fallback: 'x' })],
  ['DN lang code', () => new Intl.DisplayNames('en', { type: 'language' }).of('e')],
  ['DN currency code', () => new Intl.DisplayNames('en', { type: 'currency' }).of('us')],
  ['DN script code', () => new Intl.DisplayNames('en', { type: 'script' }).of('Lat')],
  ['DN calendar code', () => new Intl.DisplayNames('en', { type: 'calendar' }).of('a')],
  ['DN field code', () => new Intl.DisplayNames('en', { type: 'dateTimeField' }).of('x')],
  ['DF style', () => new Intl.DurationFormat('en', { style: 'x' })],
  ['DF unit style', () => new Intl.DurationFormat('en', { hours: 'x' })],
  ['DF fractionalDigits', () => new Intl.DurationFormat('en', { fractionalDigits: 10 })],
  ['DF no new', () => Intl.DurationFormat('en')],
  ['DF undefined', () => new Intl.DurationFormat('en').format()],
  ['Seg granularity', () => new Intl.Segmenter('en', { granularity: 'x' })],
  ['Seg no new', () => Intl.Segmenter('en')],
  ['Seg segments incompat', () => new Intl.Segmenter().segment('a').containing.call({}, 0)],
  ['Locale tag type', () => new Intl.Locale(5)],
  ['Locale tag undefined', () => new Intl.Locale()],
  ['Locale option language', () => new Intl.Locale('en', { language: 'e' })],
  ['Locale option script', () => new Intl.Locale('en', { script: 'x' })],
  ['Locale option region', () => new Intl.Locale('en', { region: 'x' })],
  ['Locale option hourCycle', () => new Intl.Locale('en', { hourCycle: 'x' })],
  ['Locale option calendar', () => new Intl.Locale('en', { calendar: 'a' })],
  ['Locale incompat', () => Object.getOwnPropertyDescriptor(Intl.Locale.prototype, 'language').get.call({})],
  ['supportedLocalesOf bad', () => Intl.Collator.supportedLocalesOf('x_y')],
  ['toLocaleString incompat', () => Number.prototype.toLocaleString.call('x')],
  ['BigInt toLocaleString incompat', () => BigInt.prototype.toLocaleString.call(1)],
];
for (const [label, f] of bad) probe('ERR ' + label, f);
