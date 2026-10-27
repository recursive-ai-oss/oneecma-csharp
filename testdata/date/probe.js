// Date differential probe: prints one line per observation. Run by Node under TZ=<zone> to record the expected output,
// and by the engine with the host zone set to the same zone. Nothing here depends on the current time.
var out = typeof print === 'function' ? print : console.log;
function show(label, f) {
  var r;
  try { r = f(); } catch (e) { r = 'throws ' + e.name + ': ' + e.message; }
  out(label + ' => ' + r);
}
function pad(n) { return String(n); }
function dump(label, d) {
  show(label + ' tv', function () { return d.getTime(); });
  show(label + ' str', function () { return String(d); });
  show(label + ' date', function () { return d.toDateString() + ' | ' + d.toTimeString(); });
  show(label + ' utc', function () { return d.toUTCString() + ' | ' + d.toISOString() + ' | ' + JSON.stringify(d); });
  show(label + ' fields', function () {
    return [d.getFullYear(), d.getMonth(), d.getDate(), d.getDay(), d.getHours(), d.getMinutes(), d.getSeconds(), d.getMilliseconds(), d.getTimezoneOffset(), d.getYear()].join(',');
  });
  show(label + ' utcfields', function () {
    return [d.getUTCFullYear(), d.getUTCMonth(), d.getUTCDate(), d.getUTCDay(), d.getUTCHours(), d.getUTCMinutes()].join(',');
  });
}

// Instants around transitions and at the ends of the range.
var bases = [
  1710054000000,  // 2024-03-10T07:00Z, US spring forward at 08:00Z
  1730617200000,  // 2024-11-03T07:00Z, US fall back
  1711846800000,  // 2024-03-31T01:00Z, EU spring forward
  1729990800000,  // 2024-10-27T01:00Z, EU fall back
  0, -1, 86399999, 951782400000, 4102444800000, 2147483647000, 2147483648000, 4107542400000,
  -2209075200000, -2717668800000, -3786825600000, -62167219200000, -62135596800000, -62198755200000,
  253402300799999, 253402300800000, 4070908800000, 8640000000000000, -8640000000000000,
  3000000000000, 9000000000000, 12345678901234, -12345678901234, 1e15, -1e15,
  -1262304000000, -932731200000, -776563200000, -765417600000,
];
var deltas = [0, -1, 1, -3600000, 3600000, -7200000, 7200000];
bases.forEach(function (b) {
  deltas.forEach(function (dl) {
    var t = b + dl;
    if (Math.abs(t) > 8.64e15) { return; }
    dump('t' + t, new Date(t));
  });
});

// Local-time constructor and the gap/overlap rules.
var edge = [[2024, 2, 10], [2024, 10, 3], [2024, 2, 31], [2024, 9, 27], [2025, 2, 9], [2025, 9, 26], [1999, 3, 4], [1999, 9, 31]];
edge.forEach(function (e) {
  for (var h = 0; h < 5; h++) {
    [0, 30].forEach(function (mi) {
      var d = new Date(e[0], e[1], e[2], h, mi, 15, 250);
      dump('L' + e.join('-') + 'T' + h + ':' + mi, d);
    });
  }
});
[[1, 0, 1], [0, 0, 1], [99, 11, 31], [100, 0, 1], [1000, 5, 5], [1883, 10, 18, 12], [1883, 10, 18, 12, 10], [2038, 0, 19, 3, 14, 8], [9999, 11, 31, 23, 59, 59, 999], [10000, 0, 1], [275760, 8, 13], [275760, 8, 14], [-271821, 3, 20], [-271821, 3, 19], [-1, 0, 1], [2024, 13, 35], [2024, -1, -1, -1, -1, -1, -1]].forEach(function (a) {
  var d = new Date(...a); dump('C' + a.join('/'), d);
  show('UTC' + a.join('/'), function () { return Date.UTC.apply(null, a); });
});

// Setters.
var seeds = [1710054000000 - 3600000, 1730617200000 - 3600000, 1711846800000 - 3600000, 1729990800000 - 3600000, 1700000000000, 0, -8640000000000000, 8640000000000000];
seeds.forEach(function (s) {
  ['setHours:2', 'setHours:1,30', 'setHours:3,0,0,0', 'setMinutes:90', 'setDate:0', 'setDate:32', 'setMonth:12', 'setMonth:-1,31', 'setFullYear:2000,1,29', 'setFullYear:275760', 'setFullYear:-300000',
    'setSeconds:3600', 'setMilliseconds:-1', 'setUTCHours:25', 'setUTCDate:31', 'setUTCMonth:1,30', 'setUTCFullYear:1,0,1', 'setYear:99', 'setYear:2024', 'setYear:NaN', 'setTime:1e3'].forEach(function (spec) {
    var parts = spec.split(':'), args = parts[1].split(',').map(Number);
    var d = new Date(s);
    var r = d[parts[0]].apply(d, args);
    out('S' + s + ' ' + spec + ' => ' + r + ' ' + (isNaN(d) ? 'Invalid' : d.toString() + ' ' + d.toISOString()));
  });
});
var nan = new Date(NaN);
['setHours:1', 'setDate:1', 'setFullYear:2020', 'setUTCFullYear:2020,5', 'setMonth:1', 'setYear:2020', 'setMilliseconds:1'].forEach(function (spec) {
  var parts = spec.split(':'), args = parts[1].split(',').map(Number);
  var d = new Date(NaN); var r = d[parts[0]].apply(d, args);
  out('N ' + spec + ' => ' + r + ' ' + (isNaN(d) ? 'Invalid' : d.toString()));
});
out('invalid => ' + [String(nan), nan.toDateString(), nan.toTimeString(), nan.toUTCString(), JSON.stringify(nan), nan.getTime(), nan.getTimezoneOffset(), nan.getYear()].join(' | '));
show('invalid iso', function () { return nan.toISOString(); });

// Date.parse: the specified format, then the legacy forms.
var strings = [
  '2024-07-04', '2024-07-04T10:00', '2024-07-04T10:00:00Z', '2024-07-04T10:00:00+0500', '2024-07-04 10:00', '2024-07-04 10:00:00 GMT+0200', '2024-02-30', '2024-02-30T10:00', '2024-13-01', '2024-7-4',
  '2024/07/04 10:00', '2024/7/4', '7/4/2024', '07/04/24', '7/4/49', '7/4/50', 'Jul 4 2024', 'Jul 4, 2024', '4 Jul 2024', 'July 4, 2024 10:00 PM', 'July 4, 2024 10:00:00 pm EST', 'Thu Jul 04 2024',
  'Thu, 04 Jul 2024 10:00:00 GMT', 'Thu Jul 04 2024 10:00:00 GMT-0500 (Central Daylight Time)', 'foo 2024', 'Jul 2024', '2024', '2024-07', '24', '12', '1', 'Jul', 'Jul 4', '2024 Jul 4', '4 2024', '10:00',
  'Jul 32 2024', 'Jul 4 2024 25:00', 'Jul 4 2024 24:00', '2024-07-04T24:00:00', '2024-07-04T24:00:01', '+002024-07-04', '-000000-01-01', '-000001-01-01T00:00:00Z', '2024-07-04T10:00:00.123456Z',
  '2024-07-04T10:00:00.1Z', '2024-07-04T10:00:00,5Z', '2024-07-04t10:00z', '  2024-07-04  ', '2024-07-04T10', '2024-07-04T10:00:00 +05:00', 'Jul 4 2024 10:00 +0530', 'Jul 4 2024 10:00 UTC+5',
  'Jul 4 2024 10:00 UT', '12/31/1999 23:59:59', '1999-12-31 23:59:59.5', 'Sun, 01 Jan 2000 00:00:00 +0100', 'Sat Jan 01 2000 00:00:00 GMT+0100', 'Mon Jan 1 2000 am', 'Janua 4 2024', 'Jan. 4 2024',
  'Sept 4 2024', 'sep 4 2024', 'Tuesday, July 4, 2024', '4-Jul-2024', '2024-Jul-04', 'Jul-04-2024', '1/2', '1/2/3', '0/1/2000', '13/1/2000', '2/30/2000', 'Jul 4 2024 1:00:00.5', 'Jul 4 2024 1:2',
  'Jul 4 2024 1:', '12:00 Jul 4 2024', 'Jul 4 2024 12 PM', 'Jul 4 2024 13:00 PM', 'Jul 4 2024 0:00 AM', 'Jul 4 2024 12:00 AM', '+275760-09-13T00:00:00Z', '+275760-09-13T00:00:00.001Z',
  '-271821-04-20T00:00:00Z', '0000-01-01', '0001-01-01', '99-01-01', '1/1/100', '1/1/99', '1/1/0', 'Jul 4 -5', 'Jul 4 2024 GMT', 'Jul 4 2024 Z', '2024-07-04Z', '2024-07-04T10:00:00+05',
  '2024-07-04T10:00:00+05:3', 'Jul 4 2024 (comment) 10:00', 'Jul 4 2024 10:00 (EST)', 'toString', 'Invalid Date', '',
  '2024-04-31', '2024-04-32', '2023-02-29', 'Jul 32', 'Jul 99', 'Jul 100', '50', '32', '31', '99', '100', '999', '2024/7', '2024/13', '7/2024', '1/2/2024 3:04', '2024-07-04T10:00:00-05:00',
  '2024-07-04T10:00Z', '2024-07-04T10:00:60Z', '2024-07-04T10:60Z', '2024-07-04T25:00Z', '2024-07-04T10:00:00.Z', '2024-7-4T10:00', '2024-07-4', '2024-07-04T1:00', '20240704', '12-25-2024',
  '25-12-2024', '1-2-3', '2024.07.04', '04.07.2024', 'Jul 4 2024 10:00 EDT', 'Jul 4 2024 10:00 PDT', 'Jul 4 2024 10:00 CST', 'Jul 4 2024 10:00 MST', 'Jul 4 2024 10:00 XYZ', 'Jul 4 2024 10:00 GMT+2',
  'Jul 4 2024 10:00 GMT+02:00', 'Jul 4 2024 10:00 GMT-0530', 'Jul 4 2024 10:00 +5', 'Jul 4 2024 10:00 +05', 'Jul 4 2024 10:00 +123', 'Jul 4 2024 10:00 +12345', 'Jul 4 2024 10:00 -0800 (PST)',
  'Jul 4th 2024', 'Jul 4 2024 at 10:00', 'Monday', 'Mon 4 Jul 2024', 'Mon Jul 4', 'Jul 4 2024 10:00:00:00', 'Jul 4 2024 10:00:00.', 'Jul 4 2024 1.5', '4/Jul/2024', '4 July', 'JUL 4 2024', 'jUlY 4 2024',
  'Ju 4 2024', 'Julyx 4 2024', 'Julx 4 2024', 'Mar 4 2024', 'March 4 2024', 'Thursday Jul 4 2024', 'Thurs Jul 4 2024', 'Thx Jul 4 2024', 'xThu Jul 4 2024', 'Jul 4 2024 AM', 'Jul 4 2024 10:00AM',
  'Jul 4 2024 10:00 pm', 'Jul 4 2024 10:00pm', 'Jul 4 2024 00:00 PM', '2024-07-04 10:00 PM', '2024-07-04 10:00 Z', '2024-07-04 10:00Z', '2024-07-04 Z', '2024-07-04 10:00:00.123',
  '2024-07-04 10:00:00.123456789', '2024-07-04  10:00', '2024-07-04\t10:00', '2024-07-04T 10:00', '1e3', '0x10', 'Infinity', 'NaN', 'null', 'undefined', '1970-01-01T00:00:00.000+00:00',
  '1970-01-01T00:00:00.000-00:00', '1970-01-01T00:00:00+23:59', '1970-01-01T00:00:00+24:00', '1970-01-01T00:00:00+00:60', 'Jul 4 2024 10:00 +24:00', '0', '00', '000', '0000', '0 0', '1 1', '1 1 1',
  '1 1 1 1', '1/1/1/1', 'Jan 1 1', 'Jan 1 100', 'Jan 1 99', 'Jan 1 49', 'Jan 1 50', 'Jan 99 1', '275760', '-1', '-1 1', '1-1', '1-1-1', '111-1-1', '1111-1-1', '1/1/1111', '1,1,1', 'Jan,1,2000',
  'Jan/1/2000', 'Jan-1-2000', '(Jan 1 2000)', 'Jan (x) 1 2000', 'Jan ((x)) 1 2000', 'Jan (x 1 2000', 'Jan 1 2000 (x', 'Jan 1 2000)', 'Jan 1 2000 ~', 'Jan 1 2000 ,', 'Jan 1 2000.', 'Jan. 1. 2000.',
  'Jan 1, 2000, 10:00',
];
strings.forEach(function (s) { out('P ' + JSON.stringify(s) + ' => ' + Date.parse(s)); });

// Round trips of the printed forms across the transition instants.
bases.slice(0, 12).forEach(function (b) {
  var d = new Date(b);
  [String(d), d.toUTCString(), d.toISOString(), d.toDateString() + ' ' + d.toTimeString().slice(0, 8)].forEach(function (s, i) {
    out('R' + b + ' ' + i + ' ' + Date.parse(s) + ' ' + (Date.parse(s) === b || Date.parse(s) === Math.floor(b / 1000) * 1000));
  });
});

// Locale strings.
[1710054000000, 1730617200000, 1729990800000, 0].forEach(function (b) {
  var d = new Date(b);
  show('loc' + b, function () { return d.toLocaleString() + ' | ' + d.toLocaleDateString() + ' | ' + d.toLocaleTimeString(); });
  show('locfr' + b, function () { return d.toLocaleString('fr') + ' | ' + d.toLocaleDateString('de') + ' | ' + d.toLocaleTimeString('en-GB'); });
  show('locopt' + b, function () { return d.toLocaleString('en-US', { timeZone: 'UTC', timeZoneName: 'short' }) + ' | ' + d.toLocaleDateString('en-US', { weekday: 'long', year: 'numeric', month: 'long', day: 'numeric' }); });
});

// Conversions and shape.
show('toPrimitive', function () {
  var d = new Date(86400000);
  return [typeof (d + 1), typeof (d - 1), typeof d[Symbol.toPrimitive]('number'), d[Symbol.toPrimitive]('default') === String(d), +d, `${d}` === String(d)].join(',');
});
show('toPrimitive bad', function () { return new Date(0)[Symbol.toPrimitive]('x'); });
show('toISOString range', function () { return new Date(8.64e15).toISOString() + ' ' + new Date(-8.64e15).toISOString() + ' ' + new Date(-62198755200000).toISOString(); });
show('this check', function () { return Date.prototype.getTime.call({}); });
show('call as function', function () { return typeof Date() + ' ' + typeof Date(0) + ' ' + (Date() === Date(5)); });
show('names', function () { return [Date.length, Date.UTC.length, Date.prototype.setHours.length, Date.prototype.setUTCMinutes.length, Date.prototype.toJSON.length, Date.prototype.toGMTString === Date.prototype.toUTCString, Object.prototype.toString.call(new Date(0)), Object.prototype.toString.call(Date.prototype)].join(','); });
show('new Date(date)', function () { var a = new Date(5); return new Date(a).getTime() + ',' + new Date('5').getTime() + ',' + new Date(true).getTime() + ',' + new Date(null).getTime() + ',' + new Date(undefined).getTime() + ',' + new Date(8.64e15 + 1).getTime() + ',' + new Date(1.9).getTime() + ',' + new Date(-1.9).getTime(); });
show('toJSON', function () { return [new Date(NaN).toJSON(), new Date(0).toJSON(), Date.prototype.toJSON.call({ toISOString: function () { return 'x'; } })].join(','); });
