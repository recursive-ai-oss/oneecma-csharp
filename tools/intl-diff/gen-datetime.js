// Records Intl.DateTimeFormat and Intl.DurationFormat output from the reference Node (black box).
// Loaded by gen-corpus.js, which supplies the shared helpers. Rows carry, besides the recorded parts, the offsets
// of the time zones involved at the instants used ("z"), read from the same Node, so a replay needs no tzdata of its own.
'use strict';

module.exports = function (h) {
  const { emit, sample, cross, run, LOCALES, SIX } = h;
  // Row counts are scaled so the committed corpus stays a few megabytes.
  const SCALE = 0.5;

  // ------------------------------------------------------------------ zone offsets as recorded by this Node
  const offsetFormatters = new Map();
  function offsetFormatter(zone) {
    let f = offsetFormatters.get(zone);
    if (!f) {
      f = new Intl.DateTimeFormat('en-US', { timeZone: zone, calendar: 'gregory', numberingSystem: 'latn', hourCycle: 'h23', era: 'short', year: 'numeric', month: 'numeric', day: 'numeric', hour: 'numeric', minute: 'numeric', second: 'numeric' });
      offsetFormatters.set(zone, f);
    }
    return f;
  }
  // Days since 1970-01-01 of a proleptic Gregorian date (valid for the whole Date range, unlike Date.UTC).
  function daysFromCivil(y, m, d) {
    y -= m <= 2 ? 1 : 0;
    const era = Math.floor(y / 400);
    const yoe = y - era * 400;
    const doy = Math.floor((153 * (m + (m > 2 ? -3 : 9)) + 2) / 5) + d - 1;
    const doe = yoe * 365 + Math.floor(yoe / 4) - Math.floor(yoe / 100) + doy;
    return era * 146097 + doe - 719468;
  }
  function rawOffset(zone, t) {
    if (/^[+-]/.test(zone)) {
      const m = /^([+-])(\d\d)(?::?(\d\d))?$/.exec(zone);
      return (m[1] === '-' ? -1 : 1) * ((+m[2] * 60) + (+(m[3] || 0))) * 60000;
    }
    const parts = offsetFormatter(zone).formatToParts(t);
    const g = {};
    for (const p of parts) g[p.type] = p.value;
    let year = +g.year;
    if (g.era.startsWith('B')) year = 1 - year;
    const hour = +g.hour === 24 ? 0 : +g.hour;
    const local = (daysFromCivil(year, +g.month, +g.day) * 86400 + hour * 3600 + (+g.minute) * 60 + (+g.second)) * 1000;
    return local - Math.floor(t / 1000) * 1000;
  }
  // Daylight time is in effect when the offset exceeds the smaller of the offsets at the start and middle of that UTC year.
  function zoneInfo(zone, t) {
    const off = rawOffset(zone, t);
    if (/^[+-]/.test(zone) || zone === 'UTC') return [off, 0];
    const y = new Date(t).getUTCFullYear();
    const a = new Date(0), b = new Date(0);
    a.setUTCFullYear(y, 0, 1); b.setUTCFullYear(y, 6, 1);
    if (Number.isNaN(a.getTime()) || Number.isNaN(b.getTime())) return [off, 0];
    const std = Math.min(rawOffset(zone, a.getTime()), rawOffset(zone, b.getTime()));
    return [off, off > std ? 1 : 0];
  }
  // Generic zone names look 184 days either side of the instant to see whether the zone observes daylight time.
  const CHECK = 184 * 86400000;
  function zinfo(zone, ts, generic) {
    const z = {};
    if (generic) ts = ts.flatMap(t => [t, t - CHECK, t + CHECK].filter(x => Math.abs(x) <= 8.64e15));
    for (const t of ts) { try { z[t] = zoneInfo(zone, t); } catch (e) { z[t] = null; } }
    return z;
  }

  const I = (s) => Date.parse(s);
  const INSTANTS = [
    0, 1, -1, 1000, -1000, 86399999,
    I('2024-07-04T15:05:09.123Z'), I('2024-01-15T00:00:00Z'), I('2024-01-15T12:00:00Z'), I('2024-06-15T12:00:00Z'), I('2024-06-15T00:00:00Z'), I('2024-12-31T23:59:59.999Z'),
    I('2024-03-10T06:59:59.999Z'), I('2024-03-10T07:00:00Z'), I('2024-11-03T05:59:59.999Z'), I('2024-11-03T06:00:00Z'),
    I('2024-03-31T00:59:59.999Z'), I('2024-03-31T01:00:00Z'), I('2024-10-27T00:59:59.999Z'), I('2024-10-27T01:00:00Z'),
    I('2024-04-06T15:59:59.999Z'), I('2024-04-06T16:00:00Z'), I('2024-09-28T15:59:59.999Z'), I('2024-09-28T16:00:00Z'),
    I('2000-02-29T23:59:59.999Z'), I('1999-12-31T23:59:59Z'), I('1970-06-15T08:30:00Z'), I('1969-12-31T23:59:59.999Z'), I('1900-01-01T00:00:00Z'), I('1883-11-18T16:59:59Z'),
    I('1582-10-14T12:00:00Z'), I('1582-10-15T12:00:00Z'), I('1600-03-01T00:00:00Z'),
    -62135596800000, -62167219200000, -62198755200000, -62167219200001, -1e14, -3e14, -8.64e15,
    1e12, 1.7e12, 4102444800000, 2145916800000, I('2038-01-19T03:14:07Z'), I('2100-12-31T23:59:59Z'), 253402300799999, 253402300800000, 1e15, 3e14, 8.64e15,
  ];
  const ZONES = ['UTC', 'America/New_York', 'Europe/London', 'Europe/Berlin', 'Asia/Kolkata', 'Australia/Sydney', 'America/Sao_Paulo', 'Asia/Tokyo', 'Pacific/Auckland',
    'Asia/Kathmandu', 'America/St_Johns', '+05:30', '-08:00', 'Europe/Dublin', 'America/Los_Angeles', 'Etc/GMT+5', 'Pacific/Kiritimati', 'Europe/Madrid', 'Europe/Paris',
    'America/Mexico_City', 'America/Argentina/Buenos_Aires', 'Africa/Casablanca', 'Asia/Calcutta', 'Pacific/Apia', 'America/Vancouver', 'Asia/Shanghai'];
  const KEY = [I('2024-07-04T15:05:09.123Z'), I('2024-01-15T00:00:00Z'), I('2024-06-15T12:00:00Z'), 0, I('2024-03-10T07:00:00Z'), I('2024-11-03T05:59:59.999Z'), -62198755200000, 8.64e15, I('1883-11-18T16:59:59Z'), 1.7e12];

  const RES_KEYS = ['locale', 'calendar', 'numberingSystem', 'timeZone', 'hourCycle', 'hour12', 'weekday', 'era', 'year', 'month', 'day', 'dayPeriod', 'hour', 'minute', 'second', 'fractionalSecondDigits', 'timeZoneName', 'dateStyle', 'timeStyle'];
  function resolved(f) {
    const r = f.resolvedOptions();
    const o = {};
    for (const k of RES_KEYS) if (k in r && !['locale', 'calendar', 'numberingSystem', 'timeZone'].includes(k)) o[k] = r[k];
    return o;
  }

  function dtf(group, locales, optsList, instants, zones, cap, seed) {
    let rows = [];
    for (const l of locales) for (const o of optsList) for (const t of instants) for (const z of zones) rows.push({ l, o, t, z });
    rows = cap ? sample(rows, Math.ceil(cap * SCALE), seed) : rows;
    for (const { l, o, t, z } of rows) {
      const opts = z === undefined ? o : { ...o, timeZone: z };
      const res = run(() => {
        const f = new Intl.DateTimeFormat(l, opts);
        const p = f.formatToParts(t).map(x => [x.type, x.value]);
        const joined = p.map(x => x[1]).join('');
        return { p, r: resolved(f), tz: f.resolvedOptions().timeZone, f: f.format(t) !== joined.replace(/\u202f/g, ' ') ? f.format(t) : undefined };
      });
      if (res.r) emit('date-time-format', { s: 'dtf', g: group, l, o: opts, v: t, z: zinfo(res.r.tz, [t], /Generic/.test(opts.timeZoneName)), p: res.r.p, f: res.r.f, r: res.r.r });
      else emit('date-time-format', { s: 'dtf', g: group, l, o: opts, v: t, e: res.e });
    }
  }

  const noZone = [undefined];
  dtf('default', LOCALES, [{}], INSTANTS, ['UTC', 'America/New_York'], 1100, 101);
  const styles = ['full', 'long', 'medium', 'short'];
  const styleOpts = [];
  for (const d of styles) styleOpts.push({ dateStyle: d });
  for (const t of styles) styleOpts.push({ timeStyle: t });
  for (const d of styles) for (const t of styles) styleOpts.push({ dateStyle: d, timeStyle: t });
  dtf('style', LOCALES, styleOpts, KEY, ['UTC', 'America/New_York', 'Asia/Kolkata', 'Europe/Berlin'], 1500, 102);
  dtf('style-hc', SIX, cross({ dateStyle: [undefined, 'long'], timeStyle: ['short', 'medium', 'long', 'full'], hourCycle: ['h11', 'h12', 'h23', 'h24'] }).map(strip), KEY, ['America/New_York'], 500, 103);
  dtf('style-h12', SIX, cross({ timeStyle: ['short', 'long'], hour12: [true, false] }), KEY, ['Europe/Paris', 'America/New_York'], 200, 104);

  const dateComp = cross({ weekday: [undefined, 'short', 'long', 'narrow'], year: [undefined, 'numeric', '2-digit'], month: [undefined, 'numeric', '2-digit', 'short', 'long', 'narrow'], day: [undefined, 'numeric', '2-digit'] }).map(strip);
  dtf('date', LOCALES, dateComp, KEY.slice(0, 5).concat([-62198755200000, 8.64e15]), ['UTC', 'Asia/Tokyo'], 3000, 105);
  const timeComp = cross({ hour: [undefined, 'numeric', '2-digit'], minute: [undefined, 'numeric', '2-digit'], second: [undefined, 'numeric', '2-digit'], fractionalSecondDigits: [undefined, 1, 2, 3], hourCycle: [undefined, 'h11', 'h12', 'h23', 'h24'] }).map(strip);
  dtf('time', LOCALES, timeComp, KEY.slice(0, 6), ['UTC', 'America/New_York'], 3500, 106);
  dtf('hour12', SIX, cross({ hour: ['numeric', '2-digit'], minute: [undefined, 'numeric', '2-digit'], hour12: [true, false] }), KEY.slice(0, 6), ['UTC'], 700, 107);
  dtf('hour12-hc', SIX, cross({ hour: ['numeric'], hour12: [true, false], hourCycle: ['h11', 'h23'] }), KEY.slice(0, 4), ['UTC'], 100, 108);
  const tzn = ['short', 'long', 'shortOffset', 'longOffset', 'shortGeneric', 'longGeneric'];
  dtf('zone-time', LOCALES, cross({ hour: ['numeric'], minute: ['numeric'], timeZoneName: tzn }), KEY, ZONES, 3200, 109);
  dtf('zone-date', LOCALES, cross({ year: ['numeric'], month: ['numeric'], day: ['numeric'], timeZoneName: tzn }), KEY.slice(0, 6), ZONES.slice(0, 8), 700, 110);
  dtf('zone-only', LOCALES, cross({ timeZoneName: tzn }), KEY, ZONES, 1500, 111);
  dtf('zone-full', SIX, cross({ dateStyle: ['full'], timeStyle: ['full', 'long'] }), KEY.slice(0, 4), ZONES, 700, 112);
  dtf('era', LOCALES, cross({ era: ['short', 'long', 'narrow'], year: [undefined, 'numeric', '2-digit'], month: [undefined, 'numeric', 'long', 'short'], day: [undefined, 'numeric'], weekday: [undefined, 'short'] }).map(strip), KEY.concat([-62167219200000, -62135596800000, -1e14, 253402300800000]), ['UTC'], 1800, 113);
  const dp = cross({ dayPeriod: ['narrow', 'short', 'long'], hour: [undefined, 'numeric', '2-digit'], minute: [undefined, 'numeric'], hourCycle: [undefined, 'h12', 'h23', 'h11'] }).map(strip);
  const DP_INSTANTS = [0, 3600e3 * 3, 3600e3 * 6, 3600e3 * 9, 3600e3 * 12, 3600e3 * 12 + 1800e3, 3600e3 * 13, 3600e3 * 17, 3600e3 * 18, 3600e3 * 21, 3600e3 * 23, 3600e3 * 4.5, 3600e3 * 11.99, 3600e3 * 12 + 1];
  dtf('dayperiod', SIX, dp, DP_INSTANTS, ['UTC'], 1500, 114);
  dtf('mixed', LOCALES, cross({ weekday: [undefined, 'long'], month: ['long', 'short', 'numeric'], day: ['numeric'], year: [undefined, 'numeric'], hour: ['numeric', '2-digit'], minute: ['2-digit'], second: [undefined, '2-digit'], timeZoneName: [undefined, 'short', 'long'] }).map(strip), KEY.slice(0, 6), ['America/New_York', 'Europe/London'], 2500, 115);
  dtf('year-month', LOCALES, cross({ year: ['numeric', '2-digit'], month: ['long', 'short', 'narrow', 'numeric', '2-digit'] }), KEY.slice(0, 5), ['UTC'], 600, 116);
  dtf('month-day', LOCALES, cross({ month: ['long', 'short', 'numeric', '2-digit', 'narrow'], day: ['numeric', '2-digit'], weekday: [undefined, 'short', 'long', 'narrow'] }).map(strip), KEY.slice(0, 5), ['UTC'], 600, 117);
  dtf('single', LOCALES, [{ weekday: 'long' }, { weekday: 'short' }, { weekday: 'narrow' }, { year: 'numeric' }, { year: '2-digit' }, { month: 'long' }, { month: 'short' }, { month: 'narrow' }, { month: 'numeric' }, { month: '2-digit' }, { day: 'numeric' }, { day: '2-digit' },
    { hour: 'numeric' }, { hour: '2-digit' }, { minute: 'numeric' }, { minute: '2-digit' }, { second: 'numeric' }, { second: '2-digit' }, { fractionalSecondDigits: 3 }, { era: 'short' }, { dayPeriod: 'long' }], KEY.slice(0, 6), ['UTC'], 1400, 118);
  dtf('zones-tz', ['en-US'], [{ hour: 'numeric', minute: 'numeric', timeZoneName: 'longOffset' }], KEY, ZONES, 400, 119);

  // Option errors and canonicalization.
  const bad = [{ weekday: 'bogus' }, { era: 'x' }, { year: 'long' }, { month: 'x' }, { day: 'long' }, { dayPeriod: 'x' }, { hour: 'long' }, { minute: 'short' }, { second: 'short' }, { fractionalSecondDigits: 0 }, { fractionalSecondDigits: 4 }, { timeZoneName: 'x' },
    { hourCycle: 'h25' }, { dateStyle: 'x' }, { timeStyle: 'x' }, { dateStyle: 'long', weekday: 'long' }, { timeStyle: 'long', hour: 'numeric' }, { dateStyle: 'long', timeZoneName: 'short' }, { timeStyle: 'short', fractionalSecondDigits: 1 },
    { timeZone: 'Mars/Base' }, { timeZone: '' }, { timeZone: 'Z' }, { timeZone: '+25:00' }, { timeZone: '+01:60' }, { formatMatcher: 'x' }, { localeMatcher: 'x' }, { calendar: 'a' }, { calendar: 'bogusbogusbogus' }, { numberingSystem: 'ab' }, { timeZone: 'UTC', timeZoneName: 'short' }];
  for (const o of bad) for (const l of ['en-US']) {
    const res = run(() => { const f = new Intl.DateTimeFormat(l, o); return resolved(f); });
    emit('date-time-format', { s: 'dtfo', l, o, v: 0, p: undefined, r: res.r, e: res.e, m: (() => { try { new Intl.DateTimeFormat(l, o); } catch (e) { return e.message; } })() });
  }
  const tzIds = ['Asia/Calcutta', 'Asia/Kolkata', 'UTC', 'Etc/UTC', 'GMT', 'Etc/GMT', 'Etc/GMT+5', 'etc/gmt-14', 'america/new_york', 'US/Pacific', '+01:00', '-0530', '+01', '+0100', 'Europe/Kiev', 'Europe/Kyiv', 'Asia/Katmandu', 'America/Buenos_Aires', 'Atlantic/Faeroe', 'CET', 'EST', 'EST5EDT', 'Asia/Rangoon', 'Australia/ACT', 'Asia/Saigon', 'Africa/Asmera', 'America/Indianapolis', 'Etc/Unknown', 'Factory', 'ASIA/TOKYO', 'europe/london', 'UCT', 'Zulu', 'GB', 'Japan', 'PRC', 'Asia/Ho_Chi_Minh', '+23:59', '-00:00', '+00', 'Etc/GMT0', 'America/Coral_Harbour', 'Pacific/Truk', 'Antarctica/South_Pole'];
  for (const id of tzIds) {
    const res = run(() => new Intl.DateTimeFormat('en-US', { timeZone: id }).resolvedOptions().timeZone);
    emit('date-time-format', { s: 'tzc', l: 'en-US', o: { timeZone: id }, v: 0, p: res.r, e: res.e });
  }
  for (const v of ['NaN', 'Infinity', '-Infinity', '8.64e15+1', '-8.64e15-1']) {
    const x = v === '8.64e15+1' ? 8.64e15 + 2 : v === '-8.64e15-1' ? -8.64e15 - 2 : Number(v);
    const res = run(() => new Intl.DateTimeFormat('en-US').format(x));
    emit('date-time-format', { s: 'dtfv', l: 'en-US', o: {}, v: v, e: res.e, p: res.r });
  }
  const sv = run(() => Intl.supportedValuesOf('timeZone'));
  emit('date-time-format', { s: 'tzsv', l: '', o: {}, v: 'timeZone', p: sv.r });
  // Locale-level defaults per locale: the resolved options of a bare constructor.
  for (const l of LOCALES.concat(['en-u-hc-h23', 'en-GB-u-hc-h12', 'de-u-hc-h12', 'fr-u-ca-gregory-nu-latn', 'en-u-ca-buddhist', 'es-u-hc-h11', 'pt-BR-u-hc-h23'])) {
    for (const o of [{}, { hour: 'numeric' }, { timeStyle: 'short' }, { hour: 'numeric', hour12: true }, { hour: 'numeric', hour12: false }, { hour: 'numeric', hourCycle: 'h24' }, { hour: 'numeric', hourCycle: 'h11', hour12: undefined }]) {
      const res = run(() => resolved(new Intl.DateTimeFormat(l, { ...o, timeZone: 'UTC' })));
      emit('date-time-format', { s: 'dtfo', g: 'locale', l, o: { ...o, timeZone: 'UTC' }, v: 0, r: res.r, e: res.e });
    }
  }

  // ------------------------------------------------------------------ ranges
  const RANGE_PAIRS = [];
  const T = (s) => Date.parse(s);
  const base = T('2024-07-04T15:05:09.123Z');
  for (const [a, b] of [
    [base, base], [base, base + 1], [base, base + 1000], [base, base + 60e3], [base, base + 3600e3], [base, base + 5 * 3600e3], [base, base + 9 * 3600e3], [base, base + 86400e3], [base, base + 3 * 86400e3],
    [base, base + 40 * 86400e3], [base, base + 200 * 86400e3], [base, base + 800 * 86400e3], [base, base - 3600e3], [base, base - 86400e3 * 400], [T('2024-12-31T22:00:00Z'), T('2025-01-01T02:00:00Z')],
    [T('2024-01-01T00:00:00Z'), T('2024-01-01T11:59:59Z')], [T('2024-01-01T08:00:00Z'), T('2024-01-01T17:00:00Z')], [T('2024-02-28T10:00:00Z'), T('2024-03-02T10:00:00Z')], [T('2024-01-01T00:00:00Z'), T('2024-12-31T00:00:00Z')],
    [T('2024-03-10T06:00:00Z'), T('2024-03-10T09:00:00Z')], [-62198755200000, -62198755200000 + 86400e3 * 400], [T('0099-12-31T00:00:00Z'), T('0100-01-02T00:00:00Z')], [T('2024-07-04T00:00:00Z'), T('2024-07-04T23:59:59Z')], [0, 8.64e15],
  ]) RANGE_PAIRS.push([a, b]);
  const rangeOpts = [{}, { dateStyle: 'short' }, { dateStyle: 'medium' }, { dateStyle: 'long' }, { dateStyle: 'full' }, { timeStyle: 'short' }, { timeStyle: 'medium' }, { dateStyle: 'medium', timeStyle: 'short' }, { dateStyle: 'long', timeStyle: 'long' },
    { year: 'numeric', month: 'short', day: 'numeric' }, { year: 'numeric', month: 'long' }, { year: 'numeric', month: 'long', day: 'numeric' }, { year: 'numeric', month: 'numeric', day: 'numeric' }, { month: 'short', day: 'numeric' }, { month: 'long', day: 'numeric', weekday: 'long' },
    { year: 'numeric' }, { month: 'long' }, { day: 'numeric' }, { weekday: 'long' }, { hour: 'numeric' }, { hour: 'numeric', minute: 'numeric' }, { hour: 'numeric', minute: 'numeric', second: 'numeric' }, { hour: 'numeric', minute: 'numeric', hour12: false },
    { hour: 'numeric', minute: 'numeric', timeZoneName: 'short' }, { hour: 'numeric', dayPeriod: 'long' }, { year: 'numeric', month: 'short', day: 'numeric', hour: 'numeric', minute: 'numeric' }, { month: 'short', day: 'numeric', hour: 'numeric', minute: 'numeric', hour12: false },
    { era: 'short', year: 'numeric', month: 'short', day: 'numeric' }, { minute: 'numeric', second: 'numeric' }, { fractionalSecondDigits: 3, second: 'numeric' }, { year: '2-digit', month: '2-digit', day: '2-digit' }, { weekday: 'short', hour: 'numeric' }];
  let rrows = [];
  for (const l of LOCALES) for (const o of rangeOpts) for (const [a, b] of RANGE_PAIRS) rrows.push({ l, o, a, b });
  rrows = sample(rrows, Math.ceil(5200 * SCALE), 120);
  for (const { l, o, a, b } of rrows) {
    const zone = ['UTC', 'America/New_York', 'Asia/Kolkata', 'Europe/Berlin'][(Math.abs(Math.floor(a / 1000) + Math.floor(b / 1000)) + l.length) % 4];
    const opts = { ...o, timeZone: zone };
    const res = run(() => {
      const f = new Intl.DateTimeFormat(l, opts);
      const p = f.formatRangeToParts(a, b).map(x => [x.type, x.value, x.source]);
      const s = f.formatRange(a, b);
      if (s !== p.map(x => x[1]).join('')) throw new Error('range != parts');
      return { p, tz: f.resolvedOptions().timeZone };
    });
    if (res.r) emit('date-time-range', { s: 'dtr', l, o: opts, v: [a, b], z: zinfo(res.r.tz, [a, b], /Generic/.test(opts.timeZoneName)), p: res.r.p });
    else emit('date-time-range', { s: 'dtr', l, o: opts, v: [a, b], e: res.e });
  }

  // ------------------------------------------------------------------ DurationFormat
  if (typeof Intl.DurationFormat === 'function') {
    const DURS = [
      { hours: 1, minutes: 2, seconds: 3 }, { years: 1, months: 2, weeks: 3, days: 4, hours: 5, minutes: 6, seconds: 7, milliseconds: 8, microseconds: 9, nanoseconds: 10 }, {}, { seconds: 0 }, { hours: 0, minutes: 0, seconds: 0 },
      { hours: -1, minutes: -2, seconds: -3 }, { days: 1 }, { days: 2, hours: 1 }, { years: 5 }, { months: 1 }, { weeks: 2 }, { minutes: 1 }, { seconds: 1, milliseconds: 500 }, { milliseconds: 1 }, { microseconds: 1 }, { nanoseconds: 1 },
      { seconds: 5, milliseconds: 123, microseconds: 456, nanoseconds: 789 }, { hours: 25, minutes: 61, seconds: 61 }, { hours: 1, minutes: 5 }, { minutes: 5, seconds: 7 }, { hours: 12, minutes: 0, seconds: 5 }, { days: 1, hours: 2, minutes: 3, seconds: 4, milliseconds: 5 },
      { hours: 0, minutes: 30 }, { seconds: 1.5 }, { years: 1000000 }, { seconds: 9007199254740991 }, { milliseconds: 1500 }, { hours: 1, seconds: 1 }, { years: -1, months: -1 }, { days: 3, minutes: 7 },
    ];
    const UNITS = ['years', 'months', 'weeks', 'days', 'hours', 'minutes', 'seconds', 'milliseconds', 'microseconds', 'nanoseconds'];
    function df(group, locales, optsList, durs, cap, seed) {
      let rows = [];
      for (const l of locales) for (const o of optsList) for (const d of durs) rows.push({ l, o, d });
      rows = cap ? sample(rows, Math.ceil(cap * SCALE), seed) : rows;
      for (const { l, o, d } of rows) {
        const res = run(() => {
          const f = new Intl.DurationFormat(l, o);
          const p = f.formatToParts(d).map(x => [x.type, x.value, x.unit ?? null]);
          if (f.format(d) !== p.map(x => x[1]).join('')) throw new Error('format != parts');
          return { p, r: f.resolvedOptions() };
        });
        if (res.r) emit('duration-format', { s: 'df', g: group, l, o, v: d, p: res.r.p, r: group === 'default' ? res.r.r : undefined });
        else emit('duration-format', { s: 'df', g: group, l, o, v: d, e: res.e });
      }
    }
    df('default', LOCALES, [{}], DURS);
    df('style', LOCALES, cross({ style: ['long', 'short', 'narrow', 'digital'] }), DURS, 400, 200);
    df('fractional', SIX, cross({ style: ['digital', 'short', undefined], fractionalDigits: [0, 1, 3, 6, 9, undefined] }).map(strip), DURS, 900, 201);
    df('display', SIX, cross({ style: ['long', 'short'], yearsDisplay: ['auto', 'always'], daysDisplay: ['auto', 'always'], hoursDisplay: ['auto', 'always'], minutesDisplay: ['auto', 'always'], secondsDisplay: ['auto', 'always'] }), DURS, 1300, 202);
    const unitStyles = [];
    for (const u of UNITS) {
      const styles = u === 'hours' || u === 'minutes' || u === 'seconds' ? ['long', 'short', 'narrow', 'numeric', '2-digit'] : u === 'milliseconds' || u === 'microseconds' || u === 'nanoseconds' ? ['long', 'short', 'narrow', 'numeric'] : ['long', 'short', 'narrow'];
      for (const s of styles) unitStyles.push({ [u]: s });
    }
    df('unit-style', SIX, unitStyles.concat(unitStyles.map(o => ({ style: 'narrow', ...o }))).concat(unitStyles.map(o => ({ style: 'digital', ...o }))), DURS, 1500, 203);
    df('combo', SIX, cross({ style: ['long', 'short', 'narrow', 'digital'], hours: [undefined, 'numeric', '2-digit', 'long'], minutes: [undefined, 'numeric', '2-digit'], seconds: [undefined, 'numeric', '2-digit'], hoursDisplay: [undefined, 'always'] }).map(strip), DURS, 1500, 204);
    const berr = [{ style: 'x' }, { years: 'numeric' }, { hours: 'long', hoursDisplay: 'x' }, { fractionalDigits: 10 }, { fractionalDigits: -1 }, { minutes: 'long', hours: 'numeric' }, { seconds: 'numeric', milliseconds: 'long' }, { milliseconds: '2-digit' }, { localeMatcher: 'x' }, { numberingSystem: 'abc' }, { hours: '2-digit', minutes: 'long' }];
    for (const o of berr) {
      const res = run(() => new Intl.DurationFormat('en-US', o).resolvedOptions());
      emit('duration-format', { s: 'dfo', l: 'en-US', o, v: {}, r: res.r, e: res.e });
    }
    for (const d of [{ hours: 1.5 }, { hours: NaN }, { hours: Infinity }, { hours: 1, minutes: -1 }, { unknown: 1 }, 'PT1H', { years: 4294967296 }, { hours: 1e21 }, { seconds: -0 }]) {
      const res = run(() => new Intl.DurationFormat('en-US').format(d));
      emit('duration-format', { s: 'dfv', l: 'en-US', o: {}, v: typeof d === 'string' ? d : Object.fromEntries(Object.entries(d).map(([k, x]) => [k, Number.isFinite(x) ? x : String(x)])), p: res.r, e: res.e });
    }
  }

  function strip(o) {
    const r = {};
    for (const [k, v] of Object.entries(o)) if (v !== undefined) r[k] = v;
    return r;
  }
};
