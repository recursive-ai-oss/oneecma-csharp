const m = new WeakMap(); const keep = []; for (let i = 0; ; i++) { const k = {}; keep.push(k); m.set(k, i); }
