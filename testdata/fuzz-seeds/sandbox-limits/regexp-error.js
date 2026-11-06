const keep = []; for (let i = 0; ; i++) { keep.push(new RegExp('a'.repeat(50) + i)); keep.push(new Error('e' + i)); }
