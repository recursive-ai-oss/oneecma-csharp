const cfg = { retries: 3 };
const timeout = cfg.timeout;
var a=function(t){return t.ms*2},b=function(n){var r=0;for(var i=0;i<n;i++){r+=i}return r},c=function(t){return a(t)+b(10)},total=c(timeout),done=true;
