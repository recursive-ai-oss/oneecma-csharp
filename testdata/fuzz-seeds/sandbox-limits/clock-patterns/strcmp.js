(function () { const a='a'.repeat(1.5e7); const b='a'.repeat(1.5e7); let n=0; for(let i=0;i<1e5;i++) if(a===b) n++; return n })()
