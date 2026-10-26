// Shared by record.js and gen-random.js: runs [category, pattern, flags, ...inputs] cases under the current Node and
// returns the text of an expected.json document.

// Strings are written ASCII-only: anything else becomes \uXXXX text, so lone surrogates survive the round trip.
const enc = (s) => Array.from({ length: s.length }, (_, i) => {
  const c = s.charCodeAt(i);
  return c >= 0x20 && c < 0x7f && c !== 0x5c ? s[i] : '\\u' + c.toString(16).padStart(4, '0');
}).join('');

function record(cases) {
  const lines = [];
  for (const [cat, p, f, ...inputs] of cases) {
    const entry = { cat, p: enc(p), f };
    let compiled;
    try {
      compiled = new RegExp(p, f);
    } catch (e) {
      entry.error = enc(e.message);
      lines.push(JSON.stringify(entry));
      continue;
    }
    entry.runs = [];
    for (const input of inputs) {
      const s = typeof input === 'string' ? input : input.s;
      const start = typeof input === 'string' ? 0 : input.start;
      const r = new RegExp(p, (f.includes('d') ? f : f + 'd') + (f.includes('g') || f.includes('y') ? '' : 'g'));
      r.lastIndex = start;
      const m = r.exec(s);
      const run = { s: enc(s), start };
      if (m === null) {
        run.m = null;
      } else {
        run.m = m.indices.map((x) => (x === undefined ? null : [x[0], x[1]]));
        if (m.indices.groups) {
          run.names = {};
          for (const [k, v] of Object.entries(m.indices.groups)) run.names[enc(k)] = v === undefined ? null : [v[0], v[1]];
        }
      }
      entry.runs.push(run);
    }
    lines.push(JSON.stringify(entry));
  }
  const header = { node: process.version, v8: process.versions.v8, cldr: process.versions.cldr, unicode: process.versions.unicode };
  return JSON.stringify(header).slice(0, -1) + ',"cases":[\n' + lines.join(',\n') + '\n]}';
}

module.exports = { record, enc };
