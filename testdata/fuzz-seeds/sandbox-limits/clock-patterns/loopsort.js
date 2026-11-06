(function () { const a=new Array(1e6).fill(1).map((x,i)=>i%1000); for(;;) a.slice().sort((x,y)=>x-y) })()
