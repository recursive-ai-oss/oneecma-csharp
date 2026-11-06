function* g() { yield 1; } const keep = []; for (;;) { const it = g(); it.next(); keep.push(it); }
