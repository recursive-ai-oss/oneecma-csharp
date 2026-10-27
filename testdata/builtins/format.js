let seed = 12345;
function rnd() { seed ^= seed << 13; seed >>>= 0; seed ^= seed >>> 17; seed ^= seed << 5; seed >>>= 0; return seed; }
function rdouble() {
  const m = (rnd() * 2097152 + (rnd() >>> 11)) / 9007199254740992;
  const k = (rnd() % 71) - 35;
  const kind = rnd() % 5;
  let v;
  if (kind === 0) v = m * Math.pow(10, k);
  else if (kind === 1) v = (rnd() % 100000) / Math.pow(10, rnd() % 6);
  else if (kind === 2) v = rnd() % 1000000;
  else if (kind === 3) v = Math.round(m * 1e6) / 1e3 + 0.005;
  else v = m * Math.pow(2, (rnd() % 200) - 100);
  return (rnd() & 1) ? -v : v;
}
const out = [];
const vals = [0, -0, 1, -1, 0.5, 1.5, 2.5, 0.1, 0.2, 0.3, 1e21, 1e-7, 123456789.123456789, 5e-324, 1.7976931348623157e308, 4.35, 1.005, 8.345, 1.45, 10.235, 0.000001, 1e20, 99.99999, 0.5e-10, NaN, Infinity, -Infinity];
for (let i = 0; i < 200; i++) vals.push(rdouble());
for (const v of vals) {
  const line = [String(v)];
  for (let d = 0; d <= 20; d += 1) line.push(v.toFixed(d));
  for (let d = 0; d <= 20; d += 2) line.push(v.toExponential(d));
  line.push(v.toExponential());
  for (let d = 1; d <= 21; d += 2) line.push(v.toPrecision(d));
  out.push(line.join(' '));
}
for (const v of vals.filter(x => Math.abs(x) < 2 ** 53).slice(0, 120)) {
  const line = [];
  for (const r of [2, 3, 8, 16, 32, 36]) line.push(v.toString(r));
  out.push('radix ' + line.join(' '));
}
// parse*
const strs = ['  12px', '0x1F', '1e3x', '.5', '-.5e-2z', '+Infinityx', 'Infinit', '1_000', '0b101', '  -0', '123456789012345678901234567890', '1.', '1.e2', '1e', '1e+', '0.0000001', '0.00000001e8', '\u00a0\ufeff42', '08', '0o17', '-0x10', '', 'abc'];
for (const s of strs) {
  out.push(['pf', JSON.stringify(s), parseFloat(s), Object.is(parseFloat(s), -0), 'pi', parseInt(s), parseInt(s, 16), parseInt(s, 2), parseInt(s, 36), parseInt(s, 37), parseInt(s, 0), 'N', Number(s)].join(' '));
}
// string methods
const sset = ['', 'a', 'abc', 'abcabc', 'AbC dEf', ' \t\n x \u00a0\ufeff\u2028', 'a\ud83d\ude00b', '\ud800x\udc00', 'ß', 'ǅ', 'İ', 'ΑΣ', 'ΑΣΑ', 'Σ', 'ΑΣ.', 'Α.Σ', 'Α\u0345Σ', '\u1e9e', 'ŉ', 'ǰ', '\ufb03', 'Ω', 'K', 'ǆ', 'ᾈ', 'x\u0301', 'e\u0301', '\u00e9', '\u212b', '\u1e9b\u0323', '\ud801\udc00', '\ud801\udc28'];
for (const s of sset) {
  const l = [JSON.stringify(s), s.toUpperCase(), s.toLowerCase(), s.toLocaleUpperCase(), s.toLocaleLowerCase(), s.normalize('NFD'), s.normalize('NFC'), s.normalize('NFKD'), s.normalize('NFKC'), s.isWellFormed(), JSON.stringify(s.toWellFormed()), s.trim().length, s.trimStart().length, s.trimEnd().length, [...s].length, s.codePointAt(0), s.at(-1), s.padStart(8, 'xy'), s.padEnd(7, '12'), s.repeat(2), s.indexOf('b'), s.lastIndexOf('a'), s.includes('bc'), s.startsWith('ab'), s.endsWith('bc'), s.substr(1, 2), s.substr(-2), s.slice(-3, -1), s.substring(2, 0), s.charAt(1), s.charCodeAt(1), s.concat('1', 2, null), s.localeCompare('b'), 'b'.localeCompare(s), s.anchor('a"b'), s.fontsize(3)];
  out.push(l.join('|'));
}
// full case mapping sweep, reduced to a checksum
function fnv(str, h) { for (let i = 0; i < str.length; i++) { h ^= str.charCodeAt(i); h = Math.imul(h, 16777619) >>> 0; } return h; }
let hu = 2166136261, hl = 2166136261, nu = 0, nl = 0;
for (let cp = 0; cp < 0x110000; cp++) {
  if (cp >= 0xd800 && cp < 0xe000) continue;
  const c = String.fromCodePoint(cp);
  const u = c.toUpperCase(), l = c.toLowerCase();
  if (u !== c) { nu++; hu = fnv(cp + ':' + u, hu); }
  if (l !== c) { nl++; hl = fnv(cp + ':' + l, hl); }
}
out.push('UP ' + nu + ' ' + hu, 'LO ' + nl + ' ' + hl);
// uri
for (const s of ['a b', '\u00e9\u20ac\ud83d\ude00', ';/?:@&=+$,#-_.!~*\'()', '%41%7e', '%E2%82%AC', '%f0%9f%98%80', '%zz', '\ud800', '%C3%A9x', '%25', 'A\u00ff\u0100']) {
  const r = [];
  for (const f of [encodeURI, encodeURIComponent, decodeURI, decodeURIComponent, escape, unescape]) { try { r.push(f(s)); } catch (e) { r.push(e.name + ':' + e.message); } }
  out.push('uri ' + JSON.stringify(s) + ' ' + r.join(' '));
}
// Math
const mv = [0, -0, 1, -1, 0.5, -0.5, 2, 10, 100, 1e-10, 1e10, 3, 0.1, 1.5, 2.5, -2.5, Infinity, -Infinity, NaN, 0.49999999999999994, 4503599627370495.5, 2 ** 31, 2 ** 32 + 5, -(2 ** 31) - 1];
for (const v of mv) {
  out.push(['M', v, Math.abs(v), Math.ceil(v), Math.floor(v), Math.round(v), Math.trunc(v), Math.sign(v), Math.sqrt(v), Math.fround(v), Math.clz32(v), Math.f16round(v), Math.hypot(v, 3), Math.max(v, 1), Math.min(v, -0), Math.imul(v, 3), Math.atan2(v, -0), Math.pow(v, 3), Math.pow(2, v), Math.pow(v, -0.5)].map(x => Object.is(x, -0) ? '-0' : String(x)).join(' '));
}
console.log(out.join('\n'));
