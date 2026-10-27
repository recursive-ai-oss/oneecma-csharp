// Generates random patterns and inputs (seeded, so a run is repeatable) and records Node's answers in the
// expected.json format. Usage: node gen-random.js <seed> <count> > random-<seed>.json
// The engine side replays the file with: oneecma-regexp-test262 diff random-<seed>.json
const { record } = require('./recorder.js');
const seed = Number(process.argv[2] ?? 1);
const count = Number(process.argv[3] ?? 2000);
let state = seed >>> 0;
const rnd = () => { state = (state + 0x6d2b79f5) >>> 0; let t = state; t = Math.imul(t ^ (t >>> 15), t | 1); t ^= t + Math.imul(t ^ (t >>> 7), t | 61); return ((t ^ (t >>> 14)) >>> 0) / 4294967296; };
const pick = (a) => a[Math.floor(rnd() * a.length)];
const chance = (p) => rnd() < p;

const lits = ['a', 'b', 'c', 'A', 'B', '1', '_', ' ', '-', '\n', 'k', 's', 'K', '\u017f', '\u212a', '\u01c6', '\u01c4', '\u03c3', '\u03c2', '\u00df', '\u{1F600}', '\u{10400}', '\u{10428}', '\ud83d', '\ude00', '.', '/', '{', '}', ']'];
const esc = ['\\d', '\\D', '\\w', '\\W', '\\s', '\\S', '\\b', '\\B', '\\n', '\\t', '\\x61', '\\u0062', '\\u{1F600}', '\\0', '\\cA', '\\.', '\\-', '\\/', '\\\\', '\\1', '\\2', '\\3', '\\k<n>', '\\p{L}', '\\p{Lu}', '\\P{Lu}', '\\p{Script=Greek}', '\\8', '\\q', '\\u', '\\c'];
const classAtoms = ['a', 'b', 'z', 'A', 'Z', '0', '9', '-', '\\d', '\\w', '\\s', '\\W', '\\b', '\\-', '\\]', '\u00e9', '\u{1F600}', '\u017f', '\u212a', '\\p{L}', '\\P{L}', '\\p{Lu}', '\\u{61}', '\\x41', '\\n', '_', '^', '\\^', '[', '(', '&', '\\&'];

function cls(v) {
  let s = '[' + (chance(0.25) ? '^' : '');
  const n = Math.floor(rnd() * 4);
  for (let i = 0; i < n; i++) {
    const k = rnd();
    if (k < 0.2) s += pick(classAtoms) + '-' + pick(classAtoms);
    else if (v && k < 0.3) s += pick(['--', '&&', '[a-c]', '\\q{ab|c}', '\\q{}', '[^a]']);
    else s += pick(classAtoms);
  }
  return s + ']';
}

function atom(d, v) {
  const k = rnd();
  if (k < 0.34) return pick(lits);
  if (k < 0.42) return '.';
  if (k < 0.55) return pick(esc);
  if (k < 0.70) return cls(v);
  if (d <= 0) return pick(lits);
  const inner = disj(d - 1, v);
  if (k < 0.78) return '(' + inner + ')';
  if (k < 0.82) return '(?:' + inner + ')';
  if (k < 0.86) return '(?<n>' + inner + ')';
  if (k < 0.89) return '(?=' + inner + ')';
  if (k < 0.92) return '(?!' + inner + ')';
  if (k < 0.95) return '(?<=' + inner + ')';
  if (k < 0.97) return '(?<!' + inner + ')';
  return '(?' + pick(['i', 'm', 's', 'i-m', '-s', 'im-s']) + ':' + inner + ')';
}

function term(d, v) {
  let a = atom(d, v);
  if (chance(0.35)) a += pick(['*', '+', '?', '*?', '+?', '??', '{2}', '{1,2}', '{0,}', '{2,3}?', '{1,}']);
  return a;
}

function alt(d, v) {
  let s = '';
  const n = Math.floor(rnd() * 4);
  for (let i = 0; i < n; i++) s += term(d, v);
  return s;
}

function disj(d, v) {
  let s = alt(d, v);
  while (chance(0.25)) s += '|' + alt(d, v);
  return s;
}

const alphabet = ['a', 'b', 'c', 'A', 'B', 'k', 'K', 's', '1', '2', '_', ' ', '-', '\n', '\u017f', '\u212a', '\u01c4', '\u01c5', '\u01c6', '\u03c3', '\u03c2', '\u03a3', '\u00df', '\u00e9', '\u{1F600}', '\u{1F601}', '\u{10400}', '\u{10428}', '\ud83d', '\ude00', '.', ']', '{', '}', '\\'];
function input() {
  let s = '';
  const n = Math.floor(rnd() * 9);
  for (let i = 0; i < n; i++) s += pick(alphabet);
  return s;
}

const cases = [];
for (let i = 0; i < count; i++) {
  const flags = [];
  if (chance(0.3)) flags.push('i');
  if (chance(0.15)) flags.push('m');
  if (chance(0.15)) flags.push('s');
  if (chance(0.1)) flags.push('y');
  const k = rnd();
  const mode = k < 0.35 ? 'u' : k < 0.5 ? 'v' : '';
  if (mode) flags.push(mode);
  const p = disj(3, mode === 'v');
  const inputs = [];
  const m = 2 + Math.floor(rnd() * 4);
  for (let j = 0; j < m; j++) inputs.push(chance(0.2) ? { s: input(), start: Math.floor(rnd() * 4) } : input());
  cases.push(['random', p, flags.join(''), ...inputs.map((x) => (typeof x === 'string' ? x : { s: x.s, start: Math.min(x.start, x.s.length) }))]);
}
console.log(record(cases));
