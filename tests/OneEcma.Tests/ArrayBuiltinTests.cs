namespace RecursiveAI.OneEcma.Tests;

/// <summary>Array and iterator built-ins: stack safety, huge lengths, sort behaviour and the Iterator helpers.</summary>
public class ArrayBuiltinTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ScriptEngine Engine(Action<EngineLimits>? configure = null)
    {
        var limits = new EngineLimits { Fuel = 20_000_000, MaxAllocatedBytes = 256L << 20 };
        configure?.Invoke(limits);
        return new ScriptEngine(new ScriptEngineOptions { Limits = limits });
    }

    private static string Eval(string source, Action<EngineLimits>? configure = null)
    {
        ScriptEngine e = Engine(configure);
        return e.ToDisplayString(e.Evaluate(source, "t.js", Ct));
    }

    [Fact]
    public void FlatWithInfiniteDepthOnACycleEndsInARangeError()
    {
        Assert.Equal("RangeError:Maximum call stack size exceeded", Eval("var a = []; a[0] = a; try { a.flat(Infinity); 'no' } catch (e) { e.name + ':' + e.message }"));
    }

    [Fact]
    public void FlatHandlesNestingFarBeyondTheHostStack()
    {
        // 100000 levels would overflow a recursive implementation; the explicit stack hits the call-depth limit instead.
        string source = "var a = [1]; for (var i = 0; i < 100000; i++) a = [a];\n" +
                        "try { a.flat(Infinity); 'no' } catch (e) { e instanceof RangeError }";
        Assert.Equal("true", Eval(source));
        Assert.Equal("1", Eval("var a = [1]; for (var i = 0; i < 500; i++) a = [a]; String(a.flat(Infinity).length)"));
    }

    [Fact]
    public void JoinAndToStringSurviveCyclesAndDepth()
    {
        Assert.Equal("1,,2", Eval("var a = [1]; a.push(a, 2); String(a)"));
        Assert.Equal("RangeError", Eval("var a = []; for (var i = 0; i < 200000; i++) a = [a]; try { a.join(); 'no' } catch (e) { e.name }"));
        Assert.Equal("RangeError", Eval("var a = []; for (var i = 0; i < 200000; i++) a = [a]; try { String(a); 'no' } catch (e) { e.name }"));
    }

    [Fact]
    public void SortIsStableAndSurvivesAnInconsistentComparator()
    {
        Assert.Equal("a1,a2,a3,b1,b2", Eval("var xs = ['a1', 'b1', 'a2', 'b2', 'a3']; xs.sort(function (x, y) { return x[0] < y[0] ? -1 : x[0] > y[0] ? 1 : 0; }); xs.join()"));

        // Comparators that contradict themselves must still produce a permutation of the input.
        string source = "var seed = 7; function rnd() { seed = (seed * 1103515245 + 12345) & 0x7fffffff; return seed; }\n" +
                        "var xs = []; for (var i = 0; i < 5000; i++) xs.push(i);\n" +
                        "xs.sort(function () { return rnd() % 3 - 1; });\n" +
                        "var seen = {}; var ok = xs.length === 5000; for (var j = 0; j < xs.length; j++) { seen[xs[j]] = true; }\n" +
                        "ok && Object.keys(seen).length === 5000";
        Assert.Equal("true", Eval(source));
        Assert.Equal("3,2,1", Eval("[1, 2, 3].sort(function () { return -1; }).join()"));
        Assert.Equal("1,2,3", Eval("[1, 2, 3].sort(function () { return 1; }).join()"));
        Assert.Equal("1,2,3,,x", Eval("var a = [3, , 1, undefined, 2]; a.sort(); a.join() + (3 in a ? 'x' : '') + (4 in a ? 'y' : '')"));
    }

    [Fact]
    public void ThrowingComparatorLeavesTheArrayUntouched()
    {
        Assert.Equal("3,1,2", Eval("var a = [3, 1, 2]; try { a.sort(function () { throw 1; }); } catch (e) {} a.join()"));
    }

    [Fact]
    public void HugeLengthsDoNotAllocatePerIndex()
    {
        // Each of these would take billions of steps (or gigabytes) if it walked or materialised every index.
        Assert.Equal("true", Eval("var a = []; a[4294967294] = 'x'; a.sort(); a[0] === 'x' && a.length === 4294967295"));
        Assert.Equal("4294967294", Eval("var a = []; a[4294967294] = 'x'; String(a.indexOf('x'))"));
        Assert.Equal("4294967294", Eval("var a = []; a[4294967294] = 'x'; String(a.lastIndexOf('x'))"));
        Assert.Equal("true", Eval("var a = []; a[4294967294] = 'x'; a.includes('x')"));
        Assert.Equal("RangeError", Eval("try { Array.prototype.toSorted.call({ length: 4294967296 }); 'no' } catch (e) { e.name }"));
        Assert.Equal("RangeError", Eval("try { Array.prototype.with.call({ length: 4294967296 }, 0, 1); 'no' } catch (e) { e.name }"));
        Assert.Equal("TypeError", Eval("try { Array.prototype.push.call({ length: 9007199254740991 }, 1); 'no' } catch (e) { e.name }"));
        Assert.Equal("9007199254740991", Eval("var o = { length: 9007199254740991 }; Array.prototype.fill.call(o, 1, 9007199254740989); String(o.length)"));
        Assert.Equal("1", Eval("var a = new Array(4294967295); a.length === 4294967295 ? '1' : '0'"));
        Assert.Equal("RangeError", Eval("try { new Array(4294967296); 'no' } catch (e) { e.name }"));
    }

    [Fact]
    public void SparseWritesDoNotReserveTheGap()
    {
        string source = "var x = []; var k = 1; for (var i = 0; i < 32; i++) { k = k * 2; x[k - 2] = k; }\n" +
                        "var ok = true; k = 1; for (i = 0; i < 32; i++) { k = k * 2; ok = ok && x[k - 2] === k; } ok";
        Assert.Equal("true", Eval(source));
    }

    [Fact]
    public void LoopsOverAHugeArrayLikeEndInAFuelReport()
    {
        var e = Assert.Throws<JsLimitException>(() => Engine(l => l.Fuel = 2_000_000).Evaluate("Array.prototype.forEach.call({ length: 9007199254740991, 0: 1 }, function () {})", cancellationToken: Ct));
        Assert.Equal(LimitKind.Fuel, e.Kind);
        e = Assert.Throws<JsLimitException>(() => Engine(l => l.Fuel = 2_000_000).Evaluate("Array.prototype.reverse.call({ length: 9007199254740991 })", cancellationToken: Ct));
        Assert.Equal(LimitKind.Fuel, e.Kind);
    }

    [Fact]
    public void HugeMaterialisedResultsEndInAMemoryReport()
    {
        var e = Assert.Throws<JsLimitException>(() => Engine(l => l.MaxAllocatedBytes = 16L << 20).Evaluate("Array.prototype.toReversed.call({ length: 4294967295 })", cancellationToken: Ct));
        Assert.Equal(LimitKind.Memory, e.Kind);
    }

    [Fact]
    public void FastPathsRespectInheritedIndexSetters()
    {
        // push, unshift and splice take a dense fast path only while nothing on the prototype chain can see the new index.
        string source = "var hits = 0; Object.defineProperty(Array.prototype, '1', { set: function (v) { hits++; }, configurable: true });\n" +
                        "var a = [0]; a.push(5); var r = hits + ':' + a.length + ':' + a.hasOwnProperty(1);\n" +
                        "delete Array.prototype[1]; r";
        Assert.Equal("1:2:false", Eval(source));
    }

    [Fact]
    public void FillingAHoleKeepsTheRestOfADenseArray()
    {
        Assert.Equal(",8,8,,0", Eval("var a = [, , , , 0]; a.fill(8, 1, 3); var s = ''; for (var i = 0; i < 5; i++) s += (i in a ? a[i] : '') + (i < 4 ? ',' : ''); s"));
    }

    [Fact]
    public void IteratorHelpersCloseTheirSourceOnceAndKeepTheOrderOfEffects()
    {
        string source = "var log = [];\n" +
                        "var src = { __proto__: Iterator.prototype, i: 0, next() { log.push('next'); return { done: false, value: this.i++ }; }, return() { log.push('return'); return {}; } };\n" +
                        "var out = src.map(x => x * 2).filter(x => x > 0).take(2).toArray();\n" +
                        "out.join() + '|' + log.join()";
        Assert.Equal("2,4|next,next,next,return", Eval(source));
        Assert.Equal("1,2,3", Eval("Iterator.concat([1], [2].values(), [3]).toArray().join()"));
    }

    [Fact]
    public void IteratorHelperRunningFlagBlocksReentry()
    {
        Assert.Equal("TypeError", Eval("var g = Iterator.prototype.map.call({ next() { g.next(); return { done: false, value: 1 }; } }, x => x); try { g.next(); 'no' } catch (e) { e.name }"));
    }

    [Fact]
    public void ArrayFromAsyncAwaitsEachElementAndRejectsOnAMapperFailure()
    {
        ScriptEngine e = Engine();
        e.Evaluate("var out = []; Array.fromAsync([Promise.resolve(1), 2], async x => x * 2).then(v => out.push(v.join()));\n" +
                   "Array.fromAsync([1], async () => { throw new Error('boom'); }).catch(err => out.push(err.message));", "t.js", Ct);
        e.RunJobs(Ct);
        Assert.Equal("2,4,boom", e.ToDisplayString(e.Evaluate("out.sort().join()", cancellationToken: Ct)));
    }
}
