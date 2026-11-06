(function () { let x=BigInt('7'.repeat(3e6)); let r=0n; for(let i=0;i<100;i++){ r+= x % (x/3n+BigInt(i)) } return r>0n })()
