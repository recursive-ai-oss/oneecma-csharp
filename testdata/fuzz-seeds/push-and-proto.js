Array.prototype[3] = 'p';
var a = [];
for (var i = 0; i < 20; i++) { a.push(i); }
delete Array.prototype[3];
Object.prototype[1] = 'o';
var b = []; b.push(1, 2, 3);
delete Object.prototype[1];
class C extends Array {}
var c = new C(); c.push('x'); c.length + a.length + b.length;
