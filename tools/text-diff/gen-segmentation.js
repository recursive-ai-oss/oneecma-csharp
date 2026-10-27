// Records the reference Node's Intl.Segmenter output for testdata/text/segmentation-corpus.json.
// Usage: node tools/text-diff/gen-segmentation.js > testdata/text/segmentation-expected.json
// Run with a pinned reference Node (tools/node/install.sh). The six supported locales must agree with each
// other; the script fails if they do not, so one expectation serves them all.
'use strict';
const fs = require('fs');
const path = require('path');
const corpus = JSON.parse(fs.readFileSync(path.join(__dirname, '../../testdata/text/segmentation-corpus.json'), 'utf8'));
const locales = ['en-US', 'en-GB', 'es', 'es-US', 'fr', 'de', 'pt-BR'];
const out = { node: process.version, icu: process.versions.icu, cldr: process.versions.cldr, unicode: process.versions.unicode, strings: corpus, grapheme: [], word: [], wordLike: [], sentence: [] };
for (const text of corpus) {
  for (const g of ['grapheme', 'word', 'sentence']) {
    let first = null;
    for (const loc of locales) {
      const segs = [...new Intl.Segmenter(loc, { granularity: g }).segment(text)];
      const key = JSON.stringify(segs.map(s => [s.segment, s.isWordLike === true]));
      if (first === null) first = { key, segs };
      else if (first.key !== key) { console.error(`locale ${loc} differs for ${g} of ${JSON.stringify(text)}`); process.exit(1); }
    }
    out[g].push(first.segs.map(s => s.segment));
    if (g === 'word') out.wordLike.push(first.segs.map(s => (s.isWordLike ? '1' : '0')).join(''));
  }
}
process.stdout.write(JSON.stringify(out, null, 0).replace(/\],\[/g, '],\n[') + '\n');
