// Compares the output of js-surface.js under the reference Node (js-surface.node24.txt) with the output under the engine.
// Usage: node compare-js-surface.js <engine-output.txt>
// Rows named in js-surface.deviations.txt ("<label> | <reason>") are expected to differ. U+202F and U+0020 are treated alike:
// the reference prints the narrow no-break space of CLDR 48 patterns only in formatToParts.
const fs = require('fs');
const path = require('path');
const dir = __dirname;
const norm = (s) => s.replace(/\u202f/g, ' ');
const rows = (text) => new Map(norm(text).split('\n').filter(Boolean).map((l) => {
  const m = /^(.*?) (=>|!!) /.exec(l);
  return [m ? m[1] : l, l];
}));
const ref = rows(fs.readFileSync(path.join(dir, 'js-surface.node24.txt'), 'utf8'));
const ours = rows(fs.readFileSync(process.argv[2], 'utf8'));
const deviations = new Map(fs.readFileSync(path.join(dir, 'js-surface.deviations.txt'), 'utf8').split('\n')
  .filter((l) => l && !l.startsWith('#')).map((l) => { const i = l.indexOf(' | '); return [l.slice(0, i), l.slice(i + 3)]; }));
let same = 0;
let documented = 0;
const unexplained = [];
for (const [label, line] of ref) {
  if (ours.get(label) === line) { same++; if (deviations.has(label)) console.log('stale deviation: ' + label); }
  else if (deviations.has(label)) documented++;
  else unexplained.push(label + '\n  node:   ' + line + '\n  engine: ' + ours.get(label));
}
for (const label of ours.keys()) if (!ref.has(label)) unexplained.push('extra row: ' + label);
console.log(`rows ${ref.size}: identical ${same}, documented deviations ${documented}, unexplained ${unexplained.length}`);
for (const u of unexplained) console.log(u);
process.exit(unexplained.length ? 1 : 0);
