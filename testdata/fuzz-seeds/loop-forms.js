function f(start, step, limit) {
  var i = start, n = 0;
  for (; i < limit; i += step) { n++; if (n > 50) break; }
  for (i = limit; i > 0; i--) { n += 2; if (n > 120) break; }
  while (i <= 10) { i++; n++; if (n > 200) break; }
  return n + ':' + typeof i + ':' + String(i);
}
var out = [f(0, 1, 5), f('2', 3, 9), f(0n, 1n, 4n), f(NaN, 1, 5), f({ valueOf() { return 1; } }, 1, 3), f(1.5, 0.5, 4), f(-0, 1, 2), f(undefined, 1, 3), f(null, 2, 7)];
var g = function () { var k = 0; for (var q = 0; q < 1e3; q++) { k += 7; } return k; };
out.push(g());
out.join('|');
