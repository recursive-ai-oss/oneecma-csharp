var s = '';
for (var i = 0; i < 300; i++) { s = s + 'ab' + i; if (i % 7 === 0) s = 'x' + s; }
var parts = [s, s + s, s.slice(3) + s.slice(0, 3), 'k' + s];
var out = {};
for (var j = 0; j < parts.length; j++) { out[parts[j].slice(0, 12)] = parts[j].length; }
var t = '';
for (var k = 0; k < 50; k++) { t = k + t + `${k}-${t.length}`; }
var m = new Map(); m.set(t, s); m.get(t) === s;
JSON.stringify([t.length, s.charCodeAt(5), s.indexOf('ab9'), s.split('ab').length, s.toUpperCase().length, Object.keys(out)]);
