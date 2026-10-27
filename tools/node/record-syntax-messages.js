// usage: node nodemsg.js < cases.json   -> prints [{src, name, message, line, col}]
// Black-box recording of SyntaxError wording: runs each snippet through the engine's own parser entry
// point (new vm.Script / ES module) and records the message and the first reported position.
const vm = require('vm');
const cases = JSON.parse(require('fs').readFileSync(0, 'utf8'));
const out = [];
for (const c of cases) {
  const src = typeof c === 'string' ? c : c.src;
  const mod = typeof c === 'object' && c.module;
  let r;
  try {
    if (mod) new vm.SourceTextModule(src); else new vm.Script(src, { filename: 'case.js' });
    r = { src, ok: true };
  } catch (e) {
    let line = null, col = null;
    const m = /case\.js:(\d+)/.exec(e.stack || '');
    if (m) line = +m[1];
    r = { src, name: e.name, message: e.message, line };
  }
  out.push(r);
}
console.log(JSON.stringify(out));
