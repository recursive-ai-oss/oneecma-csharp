// Turns test262 intl402 tests into plain data checks for the C# services.
//
//   node translate.js <test262-root> <out.json> [filter-substring]
//
// Each test runs in a fresh vm context in which Intl.NumberFormat, PluralRules, ListFormat,
// RelativeTimeFormat, DisplayNames, Locale and getCanonicalLocales are shims over the reference Node
// implementation. A shim records every constructor call (locales and plain options) and returns tagged
// results; the harness's assert.sameValue, assert.compareArray and assert.throws are wrapped so that
// when an assertion compares a tagged result with a literal, the pair (call, literal) becomes a check
// instead of being evaluated against Node. Everything else in the test runs unchanged. The C# test
// replays the checks against the engine's own services.
'use strict';
const fs = require('fs');
const path = require('path');
const vm = require('vm');

const root = process.argv[2];
const outFile = process.argv[3];
const filter = process.argv[4] || '';
const T = path.join(root, 'test', 'intl402');
const H = path.join(root, 'harness');

const DIRS = ['NumberFormat', 'PluralRules', 'ListFormat', 'RelativeTimeFormat', 'DisplayNames', 'Locale', 'Intl', 'DateTimeFormat', 'DurationFormat'];
const SKIP_FEATURES = ['Temporal', 'Intl.Era-monthcode', 'intl-normative-optional', 'Intl.Segmenter', 'ShadowRealm', 'Atomics', 'SharedArrayBuffer'];

function walk(dir, out) {
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) walk(p, out);
    else if (e.name.endsWith('.js') && !e.name.includes('_FIXTURE')) out.push(p);
  }
  return out;
}

function meta(src) {
  const m = /\/\*---([\s\S]*?)---\*\//.exec(src);
  const text = m ? m[1] : '';
  const list = (key) => {
    const r = new RegExp(key + ':\\s*\\[([^\\]]*)\\]').exec(text);
    return r ? r[1].split(',').map(s => s.trim()).filter(Boolean) : [];
  };
  return { includes: list('includes'), flags: list('flags'), features: list('features'), locale: list('locale') };
}

const harnessCache = {};
function harness(name) { return harnessCache[name] ??= fs.readFileSync(path.join(H, name), 'utf8'); }

const results = [];
const stats = { files: 0, translated: 0, checks: 0, skippedNoCheck: 0, skippedFeature: 0, nodeFailed: 0 };

for (const dirName of DIRS) {
  const base = path.join(T, dirName);
  if (!fs.existsSync(base)) continue;
  for (const file of walk(base, []).sort()) {
    const rel = path.relative(T, file);
    if (filter && !rel.includes(filter)) continue;
    const src = fs.readFileSync(file, 'utf8');
    const md = meta(src);
    stats.files++;
    if (md.flags.includes('async') || md.flags.includes('module') || md.features.some(f => SKIP_FEATURES.includes(f)) || /Segmenter|Collator|Temporal/.test(rel)) {
      stats.skippedFeature++;
      continue;
    }
    const checks = [];
    // A test that branches on part.type === '...' needs primitive types, so only the values of its parts are tagged.
    // The harness helper that rebuilds the expected duration text compares option values with ===.
    const plainResolved = /formatDurationFormatPattern/.test(src);
    const compareParts = /\.(type|source|unit)\s*[!=]==/.test(src);
    const tags = new WeakMap();
    let calls = [];

    const ctx = vm.createContext({});
    const RealIntl = vm.runInContext('Intl', ctx);

    const isDate = (x) => Object.prototype.toString.call(x) === '[object Date]';
    const encObj = (x) => {
      if (x === null || typeof x !== 'object' || Array.isArray(x) || isDate(x)) return undefined;
      const r = {};
      for (const k of Object.keys(x)) {
        const v = x[k];
        if (typeof v !== 'number' && typeof v !== 'string') return undefined;
        r[k] = typeof v === 'number' ? 'n:' + String(v) : 's:' + v;
      }
      return { o: r };
    };
    const encNum = (x) => isDate(x) ? 'd:' + x.getTime() : (Array.isArray(x) && x.every(e => typeof e === 'string') ? { a: x } : typeof x === 'number' ? 'n:' + (Object.is(x, -0) ? '-0' : String(x)) : typeof x === 'bigint' ? 'b:' + x : typeof x === 'string' ? 's:' + x : undefined);
    const encLocales = (l) => (l === undefined ? null : typeof l === 'string' ? l : Array.isArray(l) && l.every(x => typeof x === 'string') ? l : { bad: true });
    const encOpts = (o) => {
      if (o === undefined) return {};
      if (o === null || typeof o !== 'object') return { bad: true };
      const r = {};
      for (const k of Object.keys(o)) {
        const d = Object.getOwnPropertyDescriptor(o, k);
        if (!('value' in d)) return { bad: true };
        const v = d.value;
        if (v === undefined) continue;
        if (!['string', 'number', 'boolean'].includes(typeof v)) return { bad: true };
        r[k] = v;
      }
      if (Object.getPrototypeOf(o) !== Object.prototype && Object.getPrototypeOf(o) !== null) { /* inherited options are rare; treat as plain */ }
      return r;
    };

    function tag(value, desc) {
      let boxed;
      if (typeof value === 'string') boxed = new String(value);
      else if (typeof value === 'number') boxed = new Number(value);
      else return value;
      tags.set(boxed, desc);
      return boxed;
    }
    const encExpect = (v) => {
      if (v !== null && typeof v === 'object' && v instanceof String) v = String(v);
      if (typeof v === 'string') return { t: 's', v };
      if (typeof v === 'number') return { t: 'n', v: Object.is(v, -0) ? '-0' : String(v) };
      if (typeof v === 'boolean') return { t: 'b', v };
      if (v === undefined) return { t: 'u' };
      if (Array.isArray(v) && v.every(x => typeof x === 'string')) return { t: 'arr', v };
      return null;
    };
    const record = (desc, expect) => {
      if (!desc || desc.bad || !expect) return;
      checks.push({ ...desc, expect });
    };

    // Tagged results are boxed strings and numbers; the reference implementation wants primitives when they come back as options.
    const unboxValue = (v) => (v !== null && typeof v === 'object' && tags.has(v) && (v instanceof String || v instanceof Number) ? v.valueOf() : v);
    const unboxLocales = (l) => Array.isArray(l) ? l.map(unboxValue) : unboxValue(l);
    const unboxOptions = (o) => {
      if (o === null || typeof o !== 'object') return o;
      const keys = Object.keys(o);
      const descs = keys.map(k => Object.getOwnPropertyDescriptor(o, k));
      if (descs.some(d => !d || !('value' in d)) || !descs.some(d => unboxValue(d.value) !== d.value)) return o;
      const c = {};
      keys.forEach((k, i) => { c[k] = unboxValue(descs[i].value); });
      return c;
    };

    function service(name, Real, methodNames, getterNames = []) {
      const proto = {};
      function Shim(locales0, options0) {
        const locales = unboxLocales(locales0), options = unboxOptions(options0);
        if (!new.target) return new Shim(locales, options);
        const desc = { svc: name, loc: encLocales(locales), opts: encOpts(options) };
        if (desc.loc && desc.loc.bad) desc.bad = true;
        if (desc.opts && desc.opts.bad) desc.bad = true;
        const call = { desc: { ...desc, method: 'ctor' } };
        calls.push(call);
        try {
          this.__real = new Real(locales, options);
        } catch (e) {
          call.err = e.constructor.name;
          throw e;
        }
        this.__desc = desc;
      }
      function invoke(self, m, args) {
        const encArgs = args.map(a => (name === 'df' ? encObj(a) : undefined) ?? encNum(a));
        const desc = { ...self.__desc, method: m, args: encArgs, bad: self.__desc.bad || encArgs.some(a => a === undefined) };
        const call = { desc };
        calls.push(call);
        let r;
        try { r = self.__real[m](...args); } catch (e) { call.err = e.constructor.name; throw e; }
        if (typeof r === 'string') return tag(r, desc);
        if (m === 'resolvedOptions' && !plainResolved) {
          return new Proxy(r, { get(t, p) { const v = t[p]; return typeof p === 'string' && (typeof v === 'string' || typeof v === 'number') ? tag(v, { ...desc, prop: p }) : v; } });
        }
        if (Array.isArray(r) && /Parts$/.test(m)) {
          return r.map((part, i) => {
            const o = {};
            for (const k of Object.keys(part)) o[k] = typeof part[k] === 'string' && (k === 'value' || !compareParts) ? tag(part[k], { ...desc, part: i, prop: k }) : part[k];
            return o;
          });
        }
        return r;
      }
      for (const m of methodNames) proto[m] = function (...args) { return invoke(this, m, args); };
      for (const m of getterNames) {
        Object.defineProperty(proto, m, { get() { const self = this; return (self.__bound ??= {})[m] ??= (...args) => invoke(self, m, args); }, configurable: true });
      }
      Object.defineProperty(Shim, 'prototype', { value: proto, writable: false });
      Shim.supportedLocalesOf = (...a) => Real.supportedLocalesOf(...a);
      Object.defineProperty(Shim, 'name', { value: Real.name });
      return Shim;
    }

    const shimIntl = {
      NumberFormat: service('nf', RealIntl.NumberFormat, ['format', 'formatRange', 'resolvedOptions', 'formatToParts', 'formatRangeToParts']),
      PluralRules: service('pr', RealIntl.PluralRules, ['select', 'selectRange', 'resolvedOptions']),
      ListFormat: service('lf', RealIntl.ListFormat, ['format', 'resolvedOptions', 'formatToParts']),
      RelativeTimeFormat: service('rtf', RealIntl.RelativeTimeFormat, ['format', 'resolvedOptions', 'formatToParts']),
      DisplayNames: service('dn', RealIntl.DisplayNames, ['of', 'resolvedOptions']),
      getCanonicalLocales(locales) {
        const desc = { svc: 'canon', loc: encLocales(locales), opts: {}, method: 'call', args: [] };
        const call = { desc };
        calls.push(call);
        let r;
        try { r = RealIntl.getCanonicalLocales(locales); } catch (e) { call.err = e.constructor.name; throw e; }
        const arr = r.map((x, i) => tag(x, { ...desc, index: i }));
        tags.set(arr, { ...desc, whole: true });
        return arr;
      },
      supportedValuesOf: (k) => RealIntl.supportedValuesOf(k),
      DateTimeFormat: service('dtf', RealIntl.DateTimeFormat, ['formatToParts', 'formatRange', 'formatRangeToParts', 'resolvedOptions'], ['format']),
      Collator: RealIntl.Collator,
      Segmenter: RealIntl.Segmenter,
      DurationFormat: RealIntl.DurationFormat ? service('df', RealIntl.DurationFormat, ['format', 'formatToParts', 'resolvedOptions']) : undefined,
      Locale: RealIntl.Locale,
    };
    // Instances inherit the shim prototype; make `instanceof` and constructor lookups behave.
    ctx.Intl = shimIntl;
    Object.defineProperty(ctx, 'Intl', { value: shimIntl, writable: true, configurable: true });

    try {
      const prelude = ['assert.js', 'sta.js', ...md.includes.filter(i => !['assert.js', 'sta.js'].includes(i))];
      for (const name of prelude) vm.runInContext(harness(name), ctx, { filename: name });

      // Wrap the assertions that carry data.
      const A = vm.runInContext('assert', ctx);
      const origSame = A.sameValue, origCompare = A.compareArray, origThrows = A.throws;
      const unwrap = (x) => (x !== null && typeof x === 'object' && (x instanceof String || x instanceof Number) && !tags.has(x) ? x.valueOf() : x);
      A.sameValue = function (a, b, m) {
        const ta = tags.get(a), tb = tags.get(b);
        if (ta || tb) { record(ta || tb, encExpect(ta ? b : a)); return; }
        return origSame.call(this, a, b, m);
      };
      A.compareArray = function (a, b, m) {
        if (Array.isArray(a) && tags.has(a) && tags.get(a).whole) { record({ ...tags.get(a), whole: true }, encExpect([...b])); return; }
        return origCompare.call(this, a, b, m);
      };
      A.throws = function (Err, fn, m) {
        const mark = calls.length;
        let threw = null;
        try { origThrows.call(this, Err, fn, m); } catch (e) { threw = e; }
        // The call that threw inside fn must throw the same class in the engine.
        const inner = calls.slice(mark).filter(c => c.err);
        const last = inner.length ? inner[inner.length - 1] : null;
        if (last && last.err === Err.name) record(last.desc, { t: 'err', v: Err.name });
        else if (!last && calls.length > mark) { /* fn completed without an Intl error; nothing to replay */ }
        if (threw && !last) throw threw;
      };

      let code = src;
      if (md.flags.includes('onlyStrict')) code = '"use strict";\n' + code;
      vm.runInContext(code, ctx, { filename: rel, timeout: 20000 });
    } catch (e) {
      // The test failed in the reference itself (or hit something the shim cannot model); keep the checks seen so far.
      stats.nodeFailed++;
      if (process.env.VERBOSE) console.error('node:', rel, String(e).split('\n')[0]);
    }
    if (checks.length === 0) { stats.skippedNoCheck++; continue; }
    stats.translated++;
    stats.checks += checks.length;
    results.push({ file: rel, locales: md.locale, checks });
  }
}
fs.writeFileSync(outFile, JSON.stringify({ node: process.version, test262: root, stats, files: results }));
console.log(JSON.stringify(stats));
