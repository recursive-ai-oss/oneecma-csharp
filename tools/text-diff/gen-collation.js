// Records the reference Node's Intl.Collator ordering of testdata/text/collation-corpus.json over a grid of
// locales and options. Usage: node tools/text-diff/gen-collation.js > testdata/text/collation-expected.json
// Run with a pinned reference Node (tools/node/install.sh).
//
// For each option combination the strings are sorted with Intl.Collator.compare and written as an ordered
// list of groups; strings in one group compare equal. Equal results are stored once ("orders") and each
// combination refers to one by index, which keeps the file small. The key of a combination is
//   locale|sensitivity|ignorePunctuation|numeric|caseFirst|usage
'use strict';
const fs = require('fs');
const path = require('path');
const corpus = JSON.parse(fs.readFileSync(path.join(__dirname, '../../testdata/text/collation-corpus.json'), 'utf8'));

const locales = ['en-US', 'en-GB', 'es', 'es-US', 'fr', 'de', 'pt-BR', 'de-u-co-phonebk', 'es-u-co-trad'];
const sensitivities = ['base', 'accent', 'case', 'variant'];
const out = { node: process.version, icu: process.versions.icu, cldr: process.versions.cldr, strings: corpus, orders: [], combos: {} };
const seen = new Map();
for (const loc of locales) {
  for (const sensitivity of sensitivities) {
    for (const ignorePunctuation of [false, true]) {
      for (const numeric of [false, true]) {
        for (const caseFirst of ['false', 'lower', 'upper']) {
          for (const usage of ['sort', 'search']) {
            // Extensions apply to sorting only; ECMA-402 ignores -u-co- for search.
            if (usage === 'search' && loc.includes('-u-co-')) continue;
            const c = new Intl.Collator(loc, { sensitivity, ignorePunctuation, numeric, caseFirst, usage });
            const idx = corpus.map((_, i) => i);
            idx.sort((a, b) => c.compare(corpus[a], corpus[b]) || a - b);
            const groups = [];
            for (const i of idx) {
              const last = groups[groups.length - 1];
              if (last && c.compare(corpus[last[0]], corpus[i]) === 0) last.push(i);
              else groups.push([i]);
            }
            const body = JSON.stringify(groups);
            if (!seen.has(body)) { seen.set(body, out.orders.length); out.orders.push(groups); }
            out.combos[[loc, sensitivity, ignorePunctuation ? 1 : 0, numeric ? 1 : 0, caseFirst, usage].join('|')] = seen.get(body);
          }
        }
      }
    }
  }
}
process.stdout.write('{"node":' + JSON.stringify(out.node) + ',"icu":' + JSON.stringify(out.icu) + ',"cldr":' + JSON.stringify(out.cldr)
  + ',\n"strings":' + JSON.stringify(out.strings) + ',\n"orders":[\n' + out.orders.map(o => JSON.stringify(o)).join(',\n')
  + '\n],\n"combos":' + JSON.stringify(out.combos).replace(/,"/g, ',\n"') + '}\n');
