const row = { id: 1, unitPrice: 4 };
const n = row.price * 2;
const m = Number(row.qty) + 1;
const xs = [n, m, NaN, 0 / 0];
console.log(xs.map(x => Object.is(x, NaN)), isNaN(n), String(m), new Set(xs).size);
try { new Array(n); } catch (e) { console.log(e.message); }
try { BigInt(m); } catch (e) { console.log(e.message); }
const when = new Date(row.time * 1000);
console.log(when.toISOString());
for (const x of row.items()) { console.log(x); }
const [a] = row.keys();
[...row.values()];
function* g() { yield* row.items(); }
[...g()];
