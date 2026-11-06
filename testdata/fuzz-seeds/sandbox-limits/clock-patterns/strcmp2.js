(function () { const a='a'.repeat(1.5e7)+'x'; let b=('a'.repeat(1.5e7)+'y'); let n=0; for(let i=0;i<300;i++) if(a<b) n++; return n })()
