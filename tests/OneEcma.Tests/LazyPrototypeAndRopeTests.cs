namespace RecursiveAI.OneEcma.Tests;

/// <summary>
/// A function's "prototype" object is created on first use and strings built by concatenation are flattened on first use; neither may
/// be visible to a script except through speed.
/// </summary>
public class LazyPrototypeAndRopeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Eval(string source)
    {
        var e = new ScriptEngine();
        return e.ToDisplayString(e.Evaluate(source, "t.js", Ct));
    }

    [Theory]
    [InlineData("Reflect.ownKeys(function () {}).join()", "length,name,prototype")]
    [InlineData("Object.getOwnPropertyNames(function () {}).join()", "length,name,prototype")]
    [InlineData("var f = function () {}; f.x = 1; Reflect.ownKeys(f).join()", "length,name,prototype,x")]
    [InlineData("var f = function () {}; f.x = 1; f.prototype.constructor === f", "true")]
    [InlineData("var f = function () {}; Object.defineProperty(f, 'y', { value: 1 }); Reflect.ownKeys(f).join()", "length,name,prototype,y")]
    [InlineData("var f = function () {}; f.hasOwnProperty('prototype')", "true")]
    [InlineData("var f = function () {}; 'prototype' in f", "true")]
    [InlineData("var f = function () {}; (function (d) { return d.writable + ',' + d.enumerable + ',' + d.configurable; })(Object.getOwnPropertyDescriptor(f, 'prototype'))", "true,false,false")]
    [InlineData("var f = function () {}; Object.getOwnPropertyDescriptor(f, 'prototype').value === f.prototype", "true")]
    [InlineData("var f = function () {}; delete f.prototype", "false")]
    [InlineData("var f = function () {}; f.prototype = 5; f.prototype", "5")]
    [InlineData("var f = function () {}; var o = new f(); Object.getPrototypeOf(o) === f.prototype", "true")]
    [InlineData("var f = function () {}; Object.freeze(f); Object.isFrozen(f) && f.prototype.constructor === f", "true")]
    [InlineData("var f = function () {}; Object.setPrototypeOf(f, null); Reflect.ownKeys(f).join()", "length,name,prototype")]
    [InlineData("var f = function () {}; var p = new Proxy(f, {}); Reflect.ownKeys(p).join()", "length,name,prototype")]
    [InlineData("var f = function () {}; var seen = []; var p = new Proxy(f, { getOwnPropertyDescriptor(t, k) { seen.push(k); return Reflect.getOwnPropertyDescriptor(t, k); } }); p.hasOwnProperty('prototype'); seen.join()", "prototype")]
    [InlineData("var f = function () {}; Object.keys(f).length", "0")]
    [InlineData("var f = function () {}; var n = 0; for (var k in f) n++; n", "0")]
    [InlineData("var f = function () {}; var c = Object.create(f); c.prototype === f.prototype", "true")]
    [InlineData("Function.prototype.prototype = 7; var f = function () {}; var r = typeof f.prototype; delete Function.prototype.prototype; r", "object")]
    [InlineData("var f = function () {}; f.call === Function.prototype.call", "true")]
    [InlineData("function g() {} g.prototype.m = function () { return 1; }; new g().m()", "1")]
    [InlineData("var f = (function () { return function () {}; })(); var a = Object.getOwnPropertyNames(f); Reflect.ownKeys(f).length === a.length", "true")]
    public void TheDeferredPrototypeIsInvisible(string source, string expected) => Assert.Equal(expected, Eval(source));

    // Each case builds a string by concatenation and checks it against the same content built through an array join.
    [Theory]
    [InlineData("var s = ''; var p = []; for (var i = 0; i < 3000; i++) { s = s + 'ab' + i; p.push('ab' + i); } var f = p.join(''); s.length === f.length && s.charCodeAt(5) === f.charCodeAt(5) && s.slice(-6) === f.slice(-6) && s === f")]
    [InlineData("var a = 'x'.repeat(30) + 'y'; var b = a + a; var c = b + b; var f = a.repeat(4); c.length === 124 && c[61] === 'y' && c.indexOf('yx') === 30 && c === f")]
    [InlineData("var l = ''; var r = ''; var lp = []; var rp = []; for (var i = 0; i < 50; i++) { l = l + 'L' + i; r = 'R' + i + r; lp.push('L' + i); rp.unshift('R' + i); } (l + r) === lp.join('') + rp.join('')")]
    [InlineData("var s = 'a'.repeat(40); var t = s + 'b'; var u = s + 'c'; t.slice(-2) + u.slice(-2) + t.length === 'abac41'")]
    [InlineData("var s = 'hello world, this is long'; var k = {}; k[s + '!'] = 1; Object.keys(k)[0] === 'hello world, this is long!'")]
    [InlineData("var s = 'abcdefghijklmnopqrstuvwxyz' + 'abcdefghijklmnopqrstuvwxyz'; s.startsWith('abc') && s.split('z').length === 3 && s.toUpperCase().slice(0, 3) === 'ABC'")]
    [InlineData("var a = 'long string number one'; var b = a + ' and two'; var m = new Map(); m.set(b, 1); m.get('long string number one and two') === 1 && typeof b === 'string' && b == 'long string number one and two' && !(b < a)")]
    public void ConcatenatedStringsBehaveAsFlatOnes(string source) => Assert.Equal("true", Eval(source));

    [Fact]
    public void AStringMethodFoundOnTheStringPrototypeFollowsLaterChangesToIt()
    {
        Assert.Equal("1,2,3", Eval("var out = []; function t(s) { return s.charAt(0).length; } out.push(t('abc')); String.prototype.charAt = function () { return 'zz'; }; out.push(t('abc')); String.prototype.charAt = function () { return 'zzz'; }; out.push(t('abc')); out.join()"));
    }

    [Fact]
    public void ManyConcatenationsOfRopesDoNotExhaustTheHostStack()
    {
        // Left-leaning and right-leaning shapes, each far deeper than the host stack could recurse.
        Assert.Equal("200000:200000:200000", Eval("var a = ''; for (var i = 0; i < 200000; i++) a = a + 'x'; var b = ''; for (var i = 0; i < 200000; i++) b = 'x' + b; var d = ''; for (var i = 0; i < 100000; i++) d = d + 'x' + 'y'; a.length + ':' + b.length + ':' + d.length"));
    }

    [Fact]
    public void ExplainingAMemoryLimitDoesNotFlattenARopeIntoAnotherLimitReport()
    {
        // The report lists locals, and previewing a rope local flattens it, which is charged to the same budget that just ran out.
        var limits = new EngineLimits { Fuel = 50_000_000, MaxAllocatedBytes = 1 << 20 };
        var engine = new ScriptEngine(new ScriptEngineOptions { Limits = limits });
        var e = Assert.Throws<JsLimitException>(() => engine.Evaluate("function f() { var s = 'x'.repeat(100000); var r = s + 'a'; var t = 'y'.repeat(2000000); return r.length + t.length; }\nf();", "m.js", Ct));
        Assert.Equal(LimitKind.Memory, e.Kind);
    }

    // The loop-condition, increment and add-constant sequences run in one step for numbers; every other operand type takes the ordinary path.
    [Theory]
    [InlineData("function f() { var i = '5'; i++; return typeof i + i; } f()", "number6")]
    [InlineData("function f() { var i = '5'; i += 7; return typeof i + i; } f()", "string57")]
    [InlineData("function f() { var i = 5n; i++; return typeof i + i; } f()", "bigint6")]
    [InlineData("function f() { var log = []; var i = { valueOf() { log.push('v'); return 3; } }; var n = 0; for (; i < 5; ) { n++; if (n > 2) break; } return log.length + ':' + n; } f()", "3:3")]
    [InlineData("function f() { var i = NaN; var n = 0; for (; i < 5; ) { n++; break; } for (; !(i >= 5); ) { n += 10; break; } return n; } f()", "10")]
    [InlineData("function f() { var i = 0, n = 0; for (i = 0; i <= 3; i++) n += i; for (i = 5; i > 2; i--) n += 100; for (i = 5; i >= 5; i++) { n += 1000; break; } return n + ':' + i; } f()", "1306:5")]
    [InlineData("function f() { var i = 9007199254740992; i++; var j = -0; j += 0; return i + ':' + Object.is(j, 0); } f()", "9007199254740992:true")]
    [InlineData("function f() { let n = 0; for (let i = 0; i < 4; i++) { n += i; } return n; } f()", "6")]
    [InlineData("function f() { var fs = []; for (let i = 0; i < 3; i++) { fs.push(function () { return i; }); } return fs.map(function (g) { return g(); }).join(); } f()", "0,1,2")]
    [InlineData("function f() { try { for (let i = 0; i < 3; i++) { x; let x = 1; } } catch (e) { return e.name; } } f()", "ReferenceError")]
    [InlineData("function f() { var i = 0; var n = 0; while (i < 100000) { i += 3; n++; } return n + ':' + i; } f()", "33334:100002")]
    public void FusedLoopSequencesKeepTheirMeaning(string source, string expected) => Assert.Equal(expected, Eval(source));
}
