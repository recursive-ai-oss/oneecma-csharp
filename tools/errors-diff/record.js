// Records what Node prints for every snippet in testdata/errors/cases.txt: the error name, message and stack of the
// error a snippet throws (or stores in globalThis.__e). Run: node record.js cases.txt expected.json
// Frames inside Node itself (node:vm and friends) are dropped, since the engine has no counterpart for them.
const fs = require('fs');
const vm = require('vm');

function parse(text) {
  const cases = [];
  let cur = null;
  for (const line of text.split('\n')) {
    const m = /^\/\/\/\/ (\S+)$/.exec(line);
    if (m) { cur = { id: m[1], lines: [] }; cases.push(cur); } else if (cur) { cur.lines.push(line); }
  }
  return cases.map(c => ({ id: c.id, source: c.lines.join('\n').replace(/\n+$/, '\n') }));
}

async function main() {
  const [casesPath, outPath] = process.argv.slice(2);
  process.on('unhandledRejection', () => {});
  const result = { node: process.version, cases: {} };
  for (const c of parse(fs.readFileSync(casesPath, 'utf8'))) {
    const ctx = vm.createContext({});
    let thrown = null; let threw = false;
    try {
      new vm.Script(c.source, { filename: "case.js" }).runInContext(ctx, { displayErrors: false });
    } catch (e) { threw = true; thrown = e; }
    await new Promise(r => setImmediate(r));
    const value = threw ? thrown : ctx.__e;
    const entry = { threw };
    if (value !== undefined && value !== null && typeof value === 'object') {
      // An error thrown out of a script carries a copy of the failing source line in front of its stack; drop that.
      let raw = String(value.stack);
      if (/^case\.js:\d+\n/.test(raw) && raw.includes('\n\n')) raw = raw.slice(raw.indexOf('\n\n') + 2);
      const stack = raw.split('\n').filter(l => !/\(node:|^\s+at node:|record\.js/.test(l)).join('\n');
      Object.assign(entry, { name: String(value.name), message: String(value.message), stack });
    } else {
      entry.primitive = String(value);
    }
    result.cases[c.id] = entry;
  }
  fs.writeFileSync(outPath, JSON.stringify(result, null, 1) + '\n');
}
main();
