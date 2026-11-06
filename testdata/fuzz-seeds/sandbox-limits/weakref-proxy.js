const keep = []; for (;;) { keep.push(new WeakRef({})); keep.push(new Proxy({}, {})); }
