function make(n) { return function f(x) { return x + n; }; }
var fs = [];
for (var i = 0; i < 200; i++) { fs.push(make(i)); }
var total = 0;
for (var j = 0; j < fs.length; j++) {
  var g = fs[j];
  if (j % 3 === 0) g.tag = j;
  if (j % 5 === 0) Object.defineProperty(g, 'prototype', { value: { v: j } });
  if (j % 7 === 0) Object.freeze(g);
  if (j % 11 === 0) Object.setPrototypeOf(g, null);
  total += Reflect.ownKeys(g).length;
  try { var o = new g(1); total += o ? 1 : 0; } catch (e) { total += 100; }
}
var p = new Proxy(fs[1], { ownKeys(t) { return Reflect.ownKeys(t); } });
total + Object.getOwnPropertyNames(p).join();
