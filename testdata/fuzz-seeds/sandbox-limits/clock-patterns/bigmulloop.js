(function () { let x=BigInt('7'.repeat(1e6)); let y=x; for(let i=0;i<20;i++) y=y*x%(x*x+1n); return y>0n })()
