function chain(n, kinds) {
  const root = {};
  let cur = root;
  for (let i = 0; i < n; i++) { const nx = kinds === 'arr' ? [] : {}; if (Array.isArray(cur)) cur[0] = nx; else cur['k' + i] = nx; cur = nx; }
  if (Array.isArray(cur)) cur[0] = root; else cur.back = root;
  return root;
}
const tests = [
  () => JSON.stringify(chain(1)),
  () => JSON.stringify(chain(2)),
  () => JSON.stringify(chain(3)),
  () => JSON.stringify(chain(4)),
  () => JSON.stringify(chain(5)),
  () => JSON.stringify(chain(6)),
  () => JSON.stringify(chain(10)),
  () => JSON.stringify(chain(3, 'arr')),
  () => { class Foo {} const f = new Foo(); f.self = f; JSON.stringify(f); },
  () => { const o = Object.create(null); o.x = o; JSON.stringify(o); },
  () => { const o = new (class {})(); o.x = o; JSON.stringify(o); },
  () => { const o = {}; o['a b'] = o; JSON.stringify(o); },
  () => { const o = {}; o[1] = {}; o[1].p = o; JSON.stringify(o); },
  () => { const o = {a: {b: {}}}; o.a.b.c = o.a; JSON.stringify(o); },
  () => { const o = {a: [ {b: 1}, ]}; o.a[0].c = o.a; JSON.stringify(o); },
  () => { const a = {}; const b = {a}; a.b = b; JSON.stringify([a]); },
  () => { const o = {toJSON() { return o; }}; JSON.stringify(o); },
  () => { const x = {}; const o = {toJSON() { return {x}; }}; x.o = o; JSON.stringify(x); },
  () => JSON.parse('undefined '),
  () => JSON.parse(' undefined'),
  () => JSON.parse('[undefined]'),
  () => JSON.parse('NaN '),
  () => JSON.parse('[NaN]'),
  () => JSON.parse('undefinedx'),
  () => JSON.parse('Infinity'),
  () => JSON.parse('-Infinity'),
  () => JSON.parse('{"a":1,'),
  () => JSON.parse('{"a":1'),
  () => JSON.parse('{"a":[1,2,{"b":tru'),
  () => JSON.parse('"\\ud800\\u12"'),
  () => JSON.parse('\n\n  [1,\n  x]'),
  () => JSON.parse('{\n  "a": 1,\n  "b": x\n}'),
  () => JSON.parse('{\n  "a": 1\n  "b": 2\n}'),
  () => JSON.parse('\r\n[x]'),
  () => JSON.parse('"abc\ndef"'),
  () => JSON.parse('"\\u00zz"'),
  () => JSON.parse('[1,2,3,\n4,5,6,\n7,8,9,\n10,11,12 13]'),
];
for (const f of tests) {
  try { console.log(String(f()).slice(0, 80)); } catch (e) { console.log(e.name + ': ' + e.message); }
}
