(function () { const a=[]; for(let i=0;i<300;i++) a.push('a'.repeat(5e5)+i); a.sort((x,y)=>x.localeCompare(y)); return a.length })()
