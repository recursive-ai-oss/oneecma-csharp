const inputs = ['[', '[1,', '{"a":', '{"a"', '{"a":1,"b"', '"\\u12"', '"\\u12zz"', '"\\', '"\\u', '"', '-x', '+1', '.5', '1e+', '1e+x', 'tru', 'truee', 'nulll', 'undefined', 'NaN', 'Infinity', '[1 2]', '[,]', '{"a":1 "b":2}', '{1:2}', '\ttrue ', "'a'", '1 2', ' ', 'x', '\u2028', '00', '-01', '1.e3', '1.5.5', '1ee', '[1,2,]', '{"a":}', '{"a":1,}', '{,}', '[}', '{]', '"a\tb"', '"\ud800"', '{"__proto__":1}', 'tr ue', 'fals', 'true false', '0x10', '1_0', '{"a":1}x', '[1]]', '"abc" "def"', '-', '- 1', '1.', '1.e', '\u00e9', '\ud83d\ude00', '"\\ud800"',
  'x'.repeat(19), 'x'.repeat(20), 'x'.repeat(21), 'x'.repeat(22), 'x'.repeat(40), '0123456789'.repeat(3) + 'x', 'x' + '0123456789'.repeat(3), '[' + '1,'.repeat(15) + 'x', '[' + '1,'.repeat(4) + 'x', '[1,2,3,4,5,6,7,8,9,x]', '[1,2,3,4,5,6,7,8,9,10,11,x]', '{"a":1,"b":2,"c":3,"d":4, e}', '"abcdefghijklmnopqrstuvwxyz" x', 'abcdefghijklmnopqrstuvwxyz'];
for (const s of inputs) {
  try { JSON.parse(s); console.log(JSON.stringify(s), '=> OK'); }
  catch (e) { console.log(JSON.stringify(s), '=>', e.name, '|', e.message); }
}
for (const s of ['{"a":1}', 'x', '[1,2,3]']) { try { JSON.parse(s, 5); console.log('reviver non-callable ok'); } catch (e) { console.log(e.message); } }
console.log(JSON.parse('{"__proto__":1}').__proto__, Object.keys(JSON.parse('{"__proto__":1}')).join());
console.log(JSON.stringify(JSON.parse('[1,2,{"a":3}]', function (k, v, c) { return typeof v === 'number' ? c.source + '!' : v; })));
console.log(JSON.stringify(JSON.parse('{"a":1,"a":2}', function (k, v, c) { return k === 'a' ? c.source : v; })));
console.log(JSON.stringify(JSON.parse('[1,2]', function (k, v, c) { if (k === '0') this[1] = 9; return Object.keys(c).join() + ':' + String(v); })));
console.log(JSON.stringify(JSON.parse('-0', function (k, v, c) { return [Object.is(v, -0), c.source]; })));
console.log(JSON.stringify(JSON.parse('1e3', function (k, v, c) { return c.source; })));
console.log(JSON.stringify(JSON.parse(' "a\\u0041" ', function (k, v, c) { return c.source; })));
// stringify
const t = [
  () => JSON.stringify({a: [1, {b: 2}], c: 'x'}, null, 2),
  () => JSON.stringify({a: 1, b: [1, 2]}, null, '--'),
  () => JSON.stringify({a: 1, b: 2, c: 3}, ['c', 'a']),
  () => JSON.stringify({a: 1, b: 2}, (k, v) => typeof v === 'number' ? v * 2 : v),
  () => JSON.stringify('\u2028\u2029\ud800\udc00\ud800 \u007f\u001f"\\'),
  () => JSON.stringify([undefined, function () {}, Symbol()]),
  () => JSON.stringify({a: new Number(1), b: new String('s'), c: new Boolean(false), d: Object(1n) === 1}),
  () => JSON.stringify(NaN) + JSON.stringify(-0) + JSON.stringify(Infinity) + JSON.stringify(1e21) + JSON.stringify(null),
  () => JSON.stringify(undefined) + '|' + JSON.stringify(() => 1) + '|' + JSON.stringify(Symbol()),
  () => JSON.stringify({a: 1}, null, 20),
  () => JSON.stringify({a: 1}, null, 'abcdefghijklmnop'),
  () => JSON.stringify([], null, 2) + JSON.stringify({}, null, 2) + JSON.stringify([[]], null, 2),
  () => JSON.stringify({a: [], b: {}}, null, 1),
  () => JSON.stringify({a: 1}, null, new Number(3)),
  () => JSON.stringify({a: 1}, null, new String('xx')),
  () => JSON.stringify({a: 1, 1: 2, 0: 3}),
  () => JSON.stringify(Object.create({inherited: 1}, {own: {value: 2, enumerable: true}, hidden: {value: 3}})),
  () => JSON.stringify({toJSON(k) { return 'k=' + k; }}),
  () => JSON.stringify({x: {toJSON(k) { return 'k=' + k; }}}),
  () => JSON.stringify([{toJSON(k) { return 'k=' + k; }}]),
  () => JSON.stringify({a: 5n}, (k, v) => typeof v === 'bigint' ? v.toString() : v),
  () => { BigInt.prototype.toJSON = function () { return 'big' + this; }; try { return JSON.stringify({a: 5n}); } finally { delete BigInt.prototype.toJSON; } },
  () => JSON.stringify({a: 1, b: 2}, [new String('a'), 1, {}, 'a']),
  () => JSON.stringify({1: 'x', a: 'y'}, [1]),
  () => JSON.stringify('x', (k, v) => k === '' ? 'root:' + v : v),
  () => JSON.stringify({a: 1}, function (k, v) { return k === '' ? [this[''] === v, typeof this] : v; }),
  () => JSON.stringify({a: undefined}, null, 2),
  () => JSON.stringify(Object(Symbol())),
  () => JSON.stringify('\ud83d\ude00'),
  () => JSON.stringify({'\ud800': 1}),
];
for (const f of t) { try { console.log(JSON.stringify(f())); } catch (e) { console.log('ERR', e.name, e.message); } }
for (const n of [1000, 100000]) { JSON.parse('['.repeat(n) + ']'.repeat(n)); console.log('parse', n, 'ok'); }
