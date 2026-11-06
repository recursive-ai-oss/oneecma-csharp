(function () { const a=new Array(1e5).fill({a:1,b:'x'}); for(;;) JSON.parse(JSON.stringify(a)) })()
