var s = 'The quick brown fox ' + 'jumps over the lazy dog'.repeat(3);
String.prototype.extra = function () { return this.length; };
var r = [s.indexOf('fox'), s.slice(-5), s.replace('quick', 'slow'), s.split(' ').length, s.startsWith('The'), s.charCodeAt(4), s.extra(), s.length];
String.prototype.charAt = function () { return 'Z'; };
r.push(s.charAt(0));
delete String.prototype.charAt;
r.push(typeof s.charAt);
r.join('|');
