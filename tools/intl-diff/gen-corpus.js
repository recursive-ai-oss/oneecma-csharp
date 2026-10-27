// Records Intl output from a reference Node (black box) for a systematic grid of inputs, one JSON row per line.
//
//   node gen-corpus.js <outDir>
//
// The grid is deterministic: the same Node and ICU give the same files. Rows hold the service, the locale,
// the options as passed to the constructor, the input value(s) and the recorded output (format string
// parts, or the error class when the call throws). Value tokens: "n:<text>" Number, "b:<digits>" BigInt,
// "s:<text>" string (exact decimal).
'use strict';
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');

const out = process.argv[2] || '.';
fs.mkdirSync(out, { recursive: true });

const LOCALES = ['en-US', 'en-GB', 'es', 'es-US', 'fr', 'de', 'pt-BR'];
const SIX = ['en-US', 'en-GB', 'es', 'fr', 'de', 'pt-BR'];

// Deterministic sampling.
function rng(seed) {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6D2B79F5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}
function sample(arr, n, seed) {
  if (arr.length <= n) return arr;
  const r = rng(seed);
  const idx = arr.map((_, i) => i);
  for (let i = idx.length - 1; i > 0; i--) {
    const j = Math.floor(r() * (i + 1));
    [idx[i], idx[j]] = [idx[j], idx[i]];
  }
  return idx.slice(0, n).sort((a, b) => a - b).map(i => arr[i]);
}

function val(token) {
  const k = token[0], t = token.slice(2);
  if (k === 'n') return Number(t);
  if (k === 'b') return BigInt(t);
  return t;
}
function cross(spec) {
  let rows = [{}];
  for (const [k, vs] of Object.entries(spec)) {
    const next = [];
    for (const r of rows) for (const v of vs) next.push({ ...r, [k]: v });
    rows = next;
  }
  return rows;
}
function id(row) {
  return crypto.createHash('sha1').update(JSON.stringify([row.s, row.l, row.o, row.v, row.w])).digest('hex').slice(0, 8);
}
function run(fn) {
  try { return { r: fn() }; } catch (e) { return { e: e.constructor.name }; }
}

const files = {};
function emit(name, row) {
  row.id = id(row);
  (files[name] ||= []).push(row);
}

// ---------------------------------------------------------------- NumberFormat
const NUMS = ['n:0', 'n:-0', 'n:1', 'n:-1', 'n:0.5', 'n:1.5', 'n:2.5', 'n:-2.5', 'n:0.001', 'n:1234.5678', 'n:-1234.5678', 'n:12345', 'n:123456',
  'n:1234567.891', 'n:0.000123456', 'n:999999', 'n:999.9999', 'n:1e21', 'n:1.5e-7', 'n:9.995', 'n:NaN', 'n:Infinity', 'n:-Infinity',
  'b:12345678901234567890', 's:1234.5678901234567890123', 'n:0.1', 'n:100', 'n:1000', 'n:1e15'];
const SMALL = ['n:0', 'n:1', 'n:-1', 'n:1234.5', 'n:-0.5', 'n:1234567.891', 'n:NaN', 'n:Infinity'];

function nf(group, locales, optsList, values, cap, seed) {
  let rows = [];
  for (const l of locales) for (const o of optsList) for (const v of values) rows.push({ l, o, v });
  rows = cap ? sample(rows, cap, seed) : rows;
  for (const { l, o, v } of rows) {
    const res = run(() => {
      const f = new Intl.NumberFormat(l, o);
      const x = val(v);
      return { p: f.formatToParts(x).map(p => [p.type, p.value]), f: f.format(x) };
    });
    if (res.r) {
      if (res.r.f !== res.r.p.map(p => p[1]).join('')) throw new Error('format != parts ' + JSON.stringify({ l, o, v }));
      emit('number-format', { s: 'nf', g: group, l, o, v, p: res.r.p });
    } else emit('number-format', { s: 'nf', g: group, l, o, v, e: res.e });
  }
}

nf('decimal', LOCALES, [{}], NUMS);
nf('grouping', LOCALES, [{ useGrouping: false }, { useGrouping: 'always' }, { useGrouping: 'min2' }, { useGrouping: 'auto' }],
  ['n:1234', 'n:12345', 'n:123456.789', 'n:1234567', 'n:999']);
nf('minInt', SIX, [{ minimumIntegerDigits: 4 }, { minimumIntegerDigits: 2, useGrouping: 'always' }], ['n:5', 'n:1234.5', 'n:0.5']);
nf('percent', LOCALES, [{ style: 'percent' }, { style: 'percent', maximumFractionDigits: 2 }, { style: 'percent', signDisplay: 'always' }],
  ['n:0', 'n:0.256', 'n:-0.256', 'n:1', 'n:12.5', 'n:NaN', 'n:Infinity', 'n:0.00001']);

const CURR = ['USD', 'EUR', 'JPY', 'GBP', 'BRL', 'CAD', 'KWD', 'CHF', 'MXN', 'INR', 'xyz'];
nf('currency', LOCALES,
  cross({ style: ['currency'], currency: CURR, currencyDisplay: ['symbol', 'narrowSymbol', 'code', 'name'], currencySign: ['standard', 'accounting'] }),
  ['n:1', 'n:-1234.5', 'n:0', 'n:1234567.891', 'n:-0.005'], 350, 11);
nf('currency-sign', LOCALES,
  cross({ style: ['currency'], currency: ['USD', 'EUR'], currencySign: ['accounting'], signDisplay: ['auto', 'always', 'exceptZero', 'negative', 'never'] }),
  ['n:-5', 'n:5', 'n:0', 'n:-0', 'n:NaN'], 150, 13);

const UNITS = ['kilometer', 'meter', 'mile', 'celsius', 'fahrenheit', 'kilogram', 'pound', 'liter', 'gallon', 'byte', 'gigabyte', 'second', 'minute',
  'hour', 'day', 'week', 'month', 'year', 'millisecond', 'percent', 'degree', 'fluid-ounce', 'mile-scandinavian', 'acre', 'hectare',
  'kilometer-per-hour', 'mile-per-gallon', 'meter-per-second', 'liter-per-kilometer', 'kilobyte-per-second', 'celsius-per-hour', 'foot-per-minute'];
nf('unit', LOCALES, cross({ style: ['unit'], unit: UNITS, unitDisplay: ['short', 'narrow', 'long'] }),
  ['n:1', 'n:0', 'n:2.5', 'n:-1234.5', 'n:1000000'], 450, 12);

nf('compact', LOCALES, cross({ notation: ['compact'], compactDisplay: ['short', 'long'] }),
  ['n:0', 'n:1', 'n:12', 'n:123', 'n:999', 'n:999.9', 'n:1000', 'n:1234', 'n:12345', 'n:123456', 'n:999999', 'n:1000000', 'n:1234567', 'n:12345678',
    'n:123456789', 'n:1234567890', 'n:1e12', 'n:1.5e12', 'n:1e14', 'n:1e15', 'n:1e18', 'n:-1234567', 'n:0.001234', 'n:2', 'n:1.5', 'n:NaN', 'n:Infinity']);
nf('compact-currency', LOCALES, cross({ notation: ['compact'], style: ['currency'], currency: ['USD', 'EUR'], currencyDisplay: ['symbol', 'code', 'name'], compactDisplay: ['short', 'long'] }),
  ['n:1', 'n:1234', 'n:123456', 'n:1234567', 'n:-98765432'], 200, 14);
nf('compact-opts', SIX, [{ notation: 'compact', maximumFractionDigits: 2 }, { notation: 'compact', minimumFractionDigits: 1 }, { notation: 'compact', maximumSignificantDigits: 3 },
  { notation: 'compact', useGrouping: 'always' }, { notation: 'compact', style: 'percent' }, { notation: 'compact', style: 'unit', unit: 'kilometer', unitDisplay: 'long' },
  { notation: 'compact', signDisplay: 'always' }],
  ['n:1234', 'n:12345', 'n:1234567', 'n:0.5', 'n:999999']);

nf('scientific', LOCALES, cross({ notation: ['scientific', 'engineering'] }),
  ['n:0', 'n:1', 'n:-1', 'n:12345', 'n:0.000123', 'n:123456789', 'n:1e21', 'n:1.5e-7', 'n:99999', 'n:NaN', 'n:Infinity', 'n:-1234.5678']);
nf('scientific-opts', SIX, [{ notation: 'scientific', maximumFractionDigits: 1 }, { notation: 'engineering', maximumSignificantDigits: 2 },
  { notation: 'scientific', style: 'percent' }, { notation: 'scientific', style: 'currency', currency: 'USD' }, { notation: 'engineering', style: 'unit', unit: 'meter' },
  { notation: 'scientific', minimumIntegerDigits: 2 }],
  ['n:12345', 'n:0.00123', 'n:999.99', 'n:-5']);

nf('sign', LOCALES, cross({ signDisplay: ['auto', 'never', 'always', 'exceptZero', 'negative'] }),
  ['n:0', 'n:-0', 'n:1', 'n:-1', 'n:0.0001', 'n:-0.0001', 'n:NaN', 'n:Infinity', 'n:-Infinity'], 200, 15);
nf('sign-rounded', SIX, cross({ signDisplay: ['auto', 'exceptZero', 'negative'], maximumFractionDigits: [0] }), ['n:-0.4', 'n:0.4', 'n:-0.5', 'n:0.5']);

nf('digits', SIX, [{ minimumFractionDigits: 2 }, { maximumFractionDigits: 0 }, { minimumFractionDigits: 1, maximumFractionDigits: 5 }, { minimumSignificantDigits: 3 },
  { maximumSignificantDigits: 3 }, { minimumSignificantDigits: 2, maximumSignificantDigits: 4 }, { maximumFractionDigits: 20 }, { maximumSignificantDigits: 21 },
  { minimumFractionDigits: 100 }, { maximumFractionDigits: 101 }, { minimumFractionDigits: 3, maximumFractionDigits: 1 }, { minimumIntegerDigits: 22 }],
  ['n:0', 'n:1.005', 'n:1234.5678', 'n:0.000123456', 'n:123456789', 'n:2.5', 'n:0.1', 'n:1e-7', 'n:1e21', 'n:99.99', 'n:-0.5', 'b:12345678901234567890', 's:0.1000000000000000055511151231257827'], 400, 16);
nf('rounding-mode', ['en-US', 'de'], cross({ roundingMode: ['ceil', 'floor', 'expand', 'trunc', 'halfCeil', 'halfFloor', 'halfExpand', 'halfTrunc', 'halfEven', 'bogus'], maximumFractionDigits: [0, 1] }),
  ['n:2.5', 'n:-2.5', 'n:3.5', 'n:-3.5', 'n:0.25', 'n:-0.25', 'n:1.45', 'n:2.51', 'n:-2.51', 'n:0.05', 'n:-0.05', 'n:5.5'], 240, 17);
nf('rounding-sig', ['en-US'], cross({ roundingMode: ['ceil', 'floor', 'halfEven', 'halfTrunc', 'expand'], maximumSignificantDigits: [2, 3] }),
  ['n:1234', 'n:-1235', 'n:0.0125', 'n:-9.995', 'n:99.5', 'n:125', 'n:135']);
nf('rounding-priority', ['en-US', 'fr'], cross({ roundingPriority: ['auto', 'morePrecision', 'lessPrecision', 'bogus'], maximumSignificantDigits: [3], maximumFractionDigits: [1, 2] }),
  ['n:1.23456', 'n:12.3456', 'n:123.456', 'n:0.0123456', 'n:1000.01']);
nf('rounding-increment', ['en-US', 'de'], cross({ roundingIncrement: [1, 2, 5, 10, 20, 25, 50, 100, 200, 250, 500, 1000, 2000, 2500, 5000, 3, 0], maximumFractionDigits: [0, 2], minimumFractionDigits: [0, 2] }),
  ['n:1.27', 'n:-1.27', 'n:12.5', 'n:0.05', 'n:123.456', 'n:2.5', 'n:0.125'], 300, 18);
nf('rounding-increment-mode', ['en-US'], cross({ roundingIncrement: [5, 25], maximumFractionDigits: [2], minimumFractionDigits: [2], roundingMode: ['ceil', 'floor', 'halfEven', 'trunc', 'expand', 'halfTrunc'] }),
  ['n:1.02', 'n:-1.02', 'n:1.025', 'n:1.125', 'n:0.12']);
nf('rounding-increment-bad', ['en-US'], [{ roundingIncrement: 5, maximumSignificantDigits: 3 }, { roundingIncrement: 5, minimumFractionDigits: 1, maximumFractionDigits: 2 },
  { roundingIncrement: 5, roundingPriority: 'morePrecision' }, { roundingIncrement: 5, notation: 'compact' }, { roundingIncrement: 5, style: 'currency', currency: 'USD' }],
  ['n:1.27']);
nf('trailing-zero', SIX, cross({ trailingZeroDisplay: ['auto', 'stripIfInteger', 'bogus'], minimumFractionDigits: [2], style: ['decimal', 'currency'], currency: ['USD'] }),
  ['n:1', 'n:1.5', 'n:1.001', 'n:0', 'n:1234']);
nf('currency-digits', SIX, [{ style: 'currency', currency: 'USD', minimumFractionDigits: 0 }, { style: 'currency', currency: 'JPY', minimumFractionDigits: 2 },
  { style: 'currency', currency: 'USD', maximumFractionDigits: 0 }, { style: 'currency', currency: 'USD', maximumFractionDigits: 1 },
  { style: 'currency', currency: 'USD', maximumSignificantDigits: 3 }],
  ['n:1234.5678', 'n:0.5', 'n:1']);

nf('options-errors', ['en-US'], [{ style: 'currency' }, { style: 'unit' }, { style: 'unit', unit: 'bogus' }, { style: 'bogus' }, { currency: 'US' }, { currency: 'USDD' }, { currency: 'usd' },
  { style: 'unit', unit: 'meter-per-meter' }, { style: 'unit', unit: 'meter-per-bogus' }, { style: 'unit', unit: 'kilometer-per-hour-per-second' }, { notation: 'bogus' },
  { signDisplay: 'bogus' }, { useGrouping: 'bogus' }, { useGrouping: 'true' }, { useGrouping: 'false' }, { currencyDisplay: 'bogus', style: 'currency', currency: 'USD' },
  { compactDisplay: 'bogus' }, { minimumIntegerDigits: 0 }, { minimumIntegerDigits: 22 }, { maximumFractionDigits: -1 }, { minimumSignificantDigits: 0 }, { maximumSignificantDigits: 22 },
  { minimumSignificantDigits: 5, maximumSignificantDigits: 3 }, { unit: 'meter' }, { currency: 'USD' }],
  ['n:1']);
nf('string-values', ['en-US', 'de'], [{}, { maximumFractionDigits: 30 }, { style: 'percent' }, { notation: 'compact' }, { notation: 'scientific' }],
  ['s:0', 's:-0', 's:  12  ', 's:', 's:1e3', 's:0x10', 's:0b11', 's:0o17', 's:1_000', 's:abc', 's:Infinity', 's:-Infinity', 's:+5', 's:.5', 's:5.', 's:1e-3',
    's:123456789012345678901234567890.123456789', 's:0.000000000000000000000001', 's:1e30', 's:-1e-30', 's:1E5', 's:5e+2', 's:--1', 's:1.2.3', 's:\u00a05\u2028'], 130, 19);
nf('bigint', ['en-US', 'fr'], [{}, { notation: 'compact' }, { style: 'currency', currency: 'USD' }, { maximumFractionDigits: 2 }, { notation: 'scientific' }, { style: 'percent' }],
  ['b:0', 'b:-1', 'b:9007199254740993', 'b:123456789012345678901234567890', 'b:-1000000000000000000000']);
nf('doubles', ['en-US'], [{ maximumFractionDigits: 20 }, { maximumSignificantDigits: 21 }, {}],
  ['n:0.1', 'n:0.2', 'n:0.30000000000000004', 'n:5e-324', 'n:1.7976931348623157e308', 'n:4.35', 'n:1.005', 'n:2.675', 'n:123456789.12345678', 'n:9007199254740993',
    'n:1e-7', 'n:1.2345e-10', 'n:123456789012345680000', 'n:0.000001', 'n:1e21', 'n:2e-7', 'n:4.9e-324', 'n:8.41e21', 'n:0.1e-5']);

// ---------------------------------------------------------------- NumberFormat ranges
function nfr(group, locales, optsList, pairs, cap, seed) {
  let rows = [];
  for (const l of locales) for (const o of optsList) for (const v of pairs) rows.push({ l, o, v });
  rows = cap ? sample(rows, cap, seed) : rows;
  for (const { l, o, v } of rows) {
    const res = run(() => {
      const f = new Intl.NumberFormat(l, o);
      return f.formatRangeToParts(val(v[0]), val(v[1])).map(p => [p.type, p.value, p.source]);
    });
    if (res.r) emit('number-format-range', { s: 'nfr', g: group, l, o, v: v.join(' '), p: res.r });
    else emit('number-format-range', { s: 'nfr', g: group, l, o, v: v.join(' '), e: res.e });
  }
}
const PAIRS = [['n:3', 'n:5'], ['n:3', 'n:3'], ['n:1', 'n:1.0001'], ['n:-5', 'n:5'], ['n:0', 'n:1000'], ['n:1234.5', 'n:5678.9'], ['n:5', 'n:3'], ['n:1e6', 'n:2e6'],
  ['n:NaN', 'n:1'], ['n:1', 'n:NaN'], ['n:-Infinity', 'n:Infinity'], ['n:0.001', 'n:0.002'], ['n:12', 'n:12.4'], ['b:100', 'b:200'], ['s:1.5', 's:2.5']];
nfr('range-decimal', LOCALES, [{}, { maximumFractionDigits: 0 }, { signDisplay: 'always' }, { notation: 'compact' }], PAIRS);
nfr('range-style', SIX, cross({ style: ['percent', 'currency', 'unit'], currency: ['USD'], unit: ['kilometer'], currencyDisplay: ['symbol', 'code', 'name'], unitDisplay: ['short', 'long', 'narrow'] })
  .filter(o => (o.style === 'currency' || (o.currencyDisplay === 'symbol')) && (o.style === 'unit' || o.unitDisplay === 'short'))
  .map(o => { const r = { ...o }; if (o.style !== 'currency') { delete r.currency; delete r.currencyDisplay; } if (o.style !== 'unit') { delete r.unit; delete r.unitDisplay; } return r; }),
  [['n:3', 'n:5'], ['n:3', 'n:3'], ['n:1234.5', 'n:5678.9'], ['n:-5', 'n:5'], ['n:0.1', 'n:0.5']]);
nfr('range-other', ['en-US', 'de', 'fr'], [{ style: 'currency', currency: 'USD', currencySign: 'accounting' }, { notation: 'scientific' }, { notation: 'compact', compactDisplay: 'long' },
  { style: 'currency', currency: 'EUR', notation: 'compact' }, { minimumFractionDigits: 2 }, { style: 'unit', unit: 'kilometer-per-hour' }],
  [['n:3', 'n:5'], ['n:3', 'n:3'], ['n:-5', 'n:5'], ['n:1500', 'n:2500000'], ['n:0', 'n:0']]);

// ---------------------------------------------------------------- PluralRules
function pr(locales, optsList, values) {
  for (const l of locales) for (const o of optsList) {
    const res = run(() => { const p = new Intl.PluralRules(l, o); return { cats: [...p.resolvedOptions().pluralCategories].sort() }; });
    if (res.r) {
      emit('plural-rules', { s: 'prc', l, o, v: '', p: res.r.cats });
      for (const v of sample(values, 12, l.length + JSON.stringify(o).length)) {
        const sel = run(() => new Intl.PluralRules(l, o).select(val(v)));
        emit('plural-rules', { s: 'pr', l, o, v, p: sel.r ?? sel.e });
      }
    } else emit('plural-rules', { s: 'prc', l, o, v: '', e: res.e });
  }
}
const PV = ['n:0', 'n:1', 'n:2', 'n:3', 'n:4', 'n:5', 'n:6', 'n:7', 'n:8', 'n:10', 'n:11', 'n:12', 'n:13', 'n:14', 'n:21', 'n:22', 'n:23', 'n:100', 'n:101', 'n:102', 'n:111', 'n:112', 'n:1000',
  'n:1000000', 'n:2000000', 'n:1000001', 'n:0.5', 'n:1.0', 'n:1.5', 'n:2.5', 'n:1e21', 'n:-1', 'n:-2', 'n:NaN', 'n:Infinity', 'b:1000000', 'b:5', 's:1.00', 's:1.10', 's:0.0', 's:2', 'n:0.07', 'n:21.5', 'n:80', 'n:81', 'n:11000000'];
pr(LOCALES, [{}, { type: 'ordinal' }, { minimumFractionDigits: 1 }, { minimumFractionDigits: 2, type: 'ordinal' }, { maximumFractionDigits: 0 }, { maximumSignificantDigits: 1 }, { roundingMode: 'floor', maximumFractionDigits: 0 }, { type: 'bogus' }], PV);
for (const l of LOCALES) for (const o of [{}, { type: 'ordinal' }]) {
  for (const [a, b] of [['n:0', 'n:1'], ['n:1', 'n:2'], ['n:1', 'n:1'], ['n:0', 'n:0'], ['n:2', 'n:5'], ['n:1', 'n:21'], ['n:11', 'n:12'], ['n:0.5', 'n:1'], ['n:1000000', 'n:2000000'], ['n:NaN', 'n:1']]) {
    const sel = run(() => new Intl.PluralRules(l, o).selectRange(val(a), val(b)));
    emit('plural-rules', { s: 'prr', l, o, v: a + ' ' + b, p: sel.r ?? sel.e });
  }
}

// ---------------------------------------------------------------- ListFormat
const LISTS = [[], ['A'], ['A', 'B'], ['A', 'B', 'C'], ['A', 'B', 'C', 'D'], ['A', 'B', 'C', 'D', 'E'], ['uno', 'otro'], ['1 hora', '30 minutos', '2 segundos'], ['', 'x']];
for (const l of LOCALES) for (const o of cross({ type: ['conjunction', 'disjunction', 'unit'], style: ['long', 'short', 'narrow'] })) for (const list of LISTS) {
  const res = run(() => { const f = new Intl.ListFormat(l, o); return f.formatToParts(list).map(p => [p.type, p.value]); });
  emit('list-format', { s: 'lf', l, o, v: JSON.stringify(list), p: res.r, e: res.e });
}
for (const l of ['en-US', 'es', 'fr']) for (const o of [{ type: 'bogus' }, { style: 'bogus' }, {}]) {
  const res = run(() => new Intl.ListFormat(l, o).resolvedOptions().type);
  emit('list-format', { s: 'lfo', l, o, v: '', p: res.r, e: res.e });
}
// Spanish and others change the conjunction before certain words.
for (const l of ['es', 'en-US', 'fr', 'de', 'pt-BR']) for (const list of [['uno', 'hijo'], ['uno', 'iglesia'], ['siete', 'ocho'], ['uno', 'once'], ['x', 'y', 'hielo'], ['a', 'o'], ['a', 'ocho'], ['a', 'hijo'], ['a', 'ñu'], ['a', 'Hiena'], ['a', 'ho'], ['a', 'y'], ['a', '8'], ['a', '11'], ['a', '1000'], ['a', '800'], ['a', 'ok'], ['a', 'iniciativa'], ['a', 'x']]) {
  for (const type of ['conjunction', 'disjunction']) {
    const res = run(() => new Intl.ListFormat(l, { type }).format(list));
    emit('list-format', { s: 'lf', l, o: { type }, v: JSON.stringify(list), p: res.r ? [['literal', res.r]] : undefined, e: res.e, x: 1 });
  }
}

// ---------------------------------------------------------------- RelativeTimeFormat
const RT_UNITS = ['second', 'minute', 'hour', 'day', 'week', 'month', 'quarter', 'year', 'seconds', 'minutes', 'hours', 'days', 'weeks', 'months', 'quarters', 'years', 'bogus'];
const RT_VALS = ['n:0', 'n:-0', 'n:1', 'n:-1', 'n:2', 'n:-2', 'n:3', 'n:1.5', 'n:-1.5', 'n:1000', 'n:1234567.5', 'n:0.001', 'n:100', 'n:NaN', 'n:Infinity', 'n:-Infinity', 'n:1e21'];
{
  let rows = [];
  for (const l of LOCALES) for (const o of cross({ numeric: ['always', 'auto'], style: ['long', 'short', 'narrow'] })) for (const u of RT_UNITS) for (const v of RT_VALS) rows.push({ l, o, u, v });
  rows = sample(rows, 700, 21);
  for (const { l, o, u, v } of rows) {
    const res = run(() => new Intl.RelativeTimeFormat(l, o).formatToParts(val(v), u).map(p => [p.type, p.value, p.unit]));
    emit('relative-time-format', { s: 'rtf', l, o, u, v, p: res.r, e: res.e });
  }
  for (const [l, u, v] of sample(cross({ l: LOCALES, u: ['second', 'minute', 'hour', 'day', 'week', 'month', 'quarter', 'year'], v: ['n:-1', 'n:0', 'n:1', 'n:2', 'n:-0', 'n:-2'] }).map(r => [r.l, r.u, r.v]), 140, 22)) {
    for (const style of ['long', 'short', 'narrow']) {
      const res = run(() => new Intl.RelativeTimeFormat(l, { numeric: 'auto', style }).formatToParts(val(v), u).map(p => [p.type, p.value, p.unit]));
      emit('relative-time-format', { s: 'rtf', l, o: { numeric: 'auto', style }, u, v, p: res.r, e: res.e });
    }
  }
  for (const o of [{ numeric: 'bogus' }, { style: 'bogus' }]) {
    const res = run(() => new Intl.RelativeTimeFormat('en', o));
    emit('relative-time-format', { s: 'rtfo', l: 'en', o, u: '', v: '', e: res.e });
  }
}

// ---------------------------------------------------------------- DisplayNames
const DN = {
  language: ['en', 'en-US', 'en-GB', 'es', 'es-419', 'es-US', 'fr', 'fr-CA', 'fr-CH', 'de', 'de-AT', 'de-CH', 'pt', 'pt-BR', 'pt-PT', 'zh', 'zh-Hans', 'zh-Hant', 'zh-Hant-TW', 'ja', 'ar',
    'sr-Latn', 'und', 'root', 'haw', 'nds', 'gsw', 'en-Latn-US', 'es-Cyrl', 'tlh', 'ko', 'ru', 'it', 'hi', 'sw', 'en-US-posix', 'de-1996', 'ca-valencia', 'yue', 'zh-Hans-CN', 'fil', 'en-x-priv',
    'xx', 'qaa', 'sh', 'iw', 'bogus-tag!', 'EN-us', 'en-u-ca-gregory'],
  region: ['US', 'GB', 'ES', 'FR', 'DE', 'BR', 'PT', 'MX', 'CA', 'CH', 'AT', 'CN', 'JP', 'IN', 'AR', '001', '419', '150', '002', 'UN', 'EU', 'ZZ', 'XK', 'AQ', 'HK', 'TW', 'us', 'QO', 'AC', 'BOGUS', '84', 'EZ'],
  script: ['Latn', 'Cyrl', 'Arab', 'Hans', 'Hant', 'Grek', 'Hebr', 'Jpan', 'Kore', 'Deva', 'Zzzz', 'Zyyy', 'latn', 'Qaaa', 'Xxxx', 'Thai', 'Brai', 'Hrkt'],
  currency: ['USD', 'EUR', 'GBP', 'JPY', 'BRL', 'MXN', 'CHF', 'CAD', 'AUD', 'CNY', 'INR', 'RUB', 'KRW', 'XAU', 'XXX', 'XTS', 'usd', 'ZZZ', 'ESP', 'DEM', 'FRF', 'BTC', 'VES', 'CUP', 'HRK', 'SLL'],
  calendar: ['gregory', 'buddhist', 'chinese', 'coptic', 'dangi', 'ethioaa', 'ethiopic', 'hebrew', 'indian', 'islamic', 'islamic-civil', 'islamic-rgsa', 'islamic-tbla', 'islamic-umalqura', 'iso8601', 'japanese', 'persian', 'roc', 'bogus', 'islamicc', 'gregorian'],
  dateTimeField: ['era', 'year', 'quarter', 'month', 'weekOfYear', 'weekday', 'day', 'dayPeriod', 'hour', 'minute', 'second', 'timeZoneName', 'week', 'bogus'],
};
const dnRows = [];
for (const l of LOCALES) for (const type of Object.keys(DN)) for (const style of ['long', 'short', 'narrow']) for (const fallback of ['code', 'none']) {
  const ld = type === 'language' ? ['dialect', 'standard'] : [undefined];
  for (const languageDisplay of ld) {
    const o = { type, style, fallback }; if (languageDisplay) o.languageDisplay = languageDisplay;
    for (const code of DN[type]) {
      dnRows.push({ l, o, code });
    }
  }
}
for (const { l, o, code } of sample(dnRows, 1500, 23)) {
  const res = run(() => { const r = new Intl.DisplayNames(l, o).of(code); return r === undefined ? null : r; });
  emit('display-names', { s: 'dn', l, o, v: code, p: res.r === undefined && !res.e ? undefined : res.r, e: res.e });
}
for (const l of ['en-US']) for (const o of [{}, { type: 'bogus' }, { type: 'language', fallback: 'bogus' }, { type: 'language', languageDisplay: 'bogus' }, { type: 'region', style: 'bogus' }]) {
  const res = run(() => new Intl.DisplayNames(l, o));
  emit('display-names', { s: 'dno', l, o, v: '', e: res.e });
}

// ---------------------------------------------------------------- Locale (canonicalization, likely subtags)
const TAGS = fs.readFileSync(path.join(__dirname, 'locale-tags.txt'), 'utf8').split('\n').filter(l => l && !l.startsWith('#'));
for (const t of TAGS) {
  const c = run(() => Intl.getCanonicalLocales(t));
  emit('locale', { s: 'canon', l: '', o: {}, v: t, p: c.r, e: c.e });
  const m = run(() => { const loc = new Intl.Locale(t); return [loc.maximize().toString(), loc.minimize().toString(), loc.language, loc.script ?? null, loc.region ?? null, loc.baseName]; });
  emit('locale', { s: 'loc', l: '', o: {}, v: t, p: m.r, e: m.e });
}
for (const [req, o] of [[['en-US'], {}], [['fr-FR'], {}], [['en-u-nu-latn'], {}], [['en-u-nu-arab'], {}], [['de-u-co-phonebk'], {}], [['xx'], {}], [['es-MX', 'en'], {}], [['pt-PT'], {}],
  [['zh-Hans'], {}], [['en-AU'], {}], [['es-419'], {}], [['en-US-u-nu-latn-x-foo'], {}], [['ja', 'fr'], {}], [['und'], {}]]) {
  const r = run(() => Intl.NumberFormat.supportedLocalesOf(req));
  const n = run(() => new Intl.NumberFormat(req).resolvedOptions().locale);
  emit('locale', { s: 'sup', l: '', o: {}, v: req.join(','), p: r.r, e: r.e, q: n.r });
}
for (const k of ['calendar', 'collation', 'currency', 'numberingSystem', 'timeZone', 'unit']) {
  const r = run(() => Intl.supportedValuesOf(k));
  emit('locale', { s: 'sv', l: '', o: {}, v: k, p: r.r, e: r.e });
}

for (const [name, rows] of Object.entries(files)) {
  fs.writeFileSync(path.join(out, name + '.jsonl'), rows.map(r => JSON.stringify(r)).join('\n') + '\n');
  console.log(name, rows.length);
}
console.log('node', process.version, 'icu', process.versions.icu, 'cldr', process.versions.cldr, 'unicode', process.versions.unicode);
