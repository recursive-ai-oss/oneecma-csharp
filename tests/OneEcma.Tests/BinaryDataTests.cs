using System.Text;

namespace RecursiveAI.OneEcma.Tests;

/// <summary>ArrayBuffer, TypedArrays, DataView and Atomics: behaviour and limits that the test262 directories do not pin down.</summary>
public class BinaryDataTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class PrintHost : DefaultHost, IJsHost
    {
        internal readonly StringBuilder Output = new();

        public override void Print(string text) => Output.Append(text).Append('\n');
    }

    private static ScriptEngine Engine(PrintHost host, Action<EngineLimits>? configure = null)
    {
        var limits = new EngineLimits { Fuel = 400_000_000, MaxAllocatedBytes = 128L << 20 };
        configure?.Invoke(limits);
        return new ScriptEngine(new ScriptEngineOptions { Host = host, InstallTest262Host = true, Limits = limits });
    }

    private static string Run(string source, Action<EngineLimits>? configure = null)
    {
        var host = new PrintHost();
        Engine(host, configure).Evaluate(source, "t.js", Ct);
        return host.Output.ToString().TrimEnd('\n');
    }

    [Fact]
    public void ElementTypesWrapAndRound()
    {
        string o = Run("""
            const b = new ArrayBuffer(16);
            const i8 = new Int8Array(b, 0, 2); i8[0] = 200; i8[1] = -129;
            const c = new Uint8ClampedArray(4); c[0] = 300; c[1] = -5; c[2] = 1.5; c[3] = 2.5;
            const f32 = new Float32Array(1); f32[0] = 16777217;
            const f16 = new Float16Array(2); f16[0] = 65520; f16[1] = 1.337;
            const big = new BigInt64Array(1); big[0] = 2n ** 63n;
            const ubig = new BigUint64Array(1); ubig[0] = -1n;
            print([i8[0], i8[1], c.join(), f32[0], f16[0], f16[1], big[0], ubig[0]].join(' '));
            print(Math.f16round(5.5) + ' ' + Math.f16round(1e-9) + ' ' + Math.f16round(65519.99));
            """);
        Assert.Equal("-56 127 255,0,2,2 16777216 Infinity 1.3369140625 -9223372036854775808 18446744073709551615\n5.5 0 65504", o);
    }

    [Fact]
    public void DataViewHonoursEndiannessAndBounds()
    {
        string o = Run("""
            const d = new DataView(new ArrayBuffer(8));
            d.setUint16(0, 0x1234); d.setUint16(2, 0x1234, true);
            d.setFloat16(4, 1.5); d.setInt16(6, -2, true);
            print([d.getUint8(0), d.getUint8(1), d.getUint8(2), d.getUint8(3), d.getFloat16(4), d.getUint16(6, true)].join());
            try { d.getInt32(6); } catch (e) { print(e.name + ': ' + e.message); }
            try { d.getInt8(-1); } catch (e) { print(e.name + ': ' + e.message); }
            """);
        Assert.Equal("18,52,52,18,1.5,65534\nRangeError: Offset is outside the bounds of the DataView\nRangeError: Offset is outside the bounds of the DataView", o);
    }

    [Fact]
    public void LengthTrackingViewsFollowAResizableBuffer()
    {
        string o = Run("""
            const rab = new ArrayBuffer(8, { maxByteLength: 16 });
            const all = new Uint8Array(rab), fixed = new Uint8Array(rab, 2, 4), off = new Uint8Array(rab, 4);
            const row = () => [all.length, fixed.length, fixed.byteOffset, off.length, off.byteLength].join();
            print(row());
            rab.resize(5); print(row());
            rab.resize(2); print(row());
            rab.resize(16); print(row());
            try { fixed.fill(0); rab.resize(3); fixed.fill(0); } catch (e) { print(e.name); }
            print(Object.isExtensible(all) + ' ' + Reflect.preventExtensions(all) + ' ' + Reflect.preventExtensions(new Uint8Array(2)));
            """);
        Assert.Equal("8,4,2,4,4\n5,0,0,1,1\n2,0,0,0,0\n16,4,2,12,12\nTypeError\ntrue false true", o);
    }

    [Fact]
    public void DetachedBuffersReadAsEmptyAndRejectOperations()
    {
        string o = Run("""
            const u = new Uint8Array([1, 2, 3]);
            const b = u.buffer;
            $262.detachArrayBuffer(b);
            print([b.detached, b.byteLength, u.length, u.byteOffset, u[0], 0 in u, Object.keys(u).length].join());
            try { u.fill(0); } catch (e) { print(e.constructor.name + ': ' + e.message); }
            const t = new ArrayBuffer(4).transfer(2);
            print(t.byteLength + ' ' + t.resizable);
            const r = new ArrayBuffer(4, { maxByteLength: 8 });
            print(r.transfer().resizable + ' ' + new ArrayBuffer(4, { maxByteLength: 8 }).transferToFixedLength().resizable);
            """);
        Assert.Equal("true,0,0,0,,false,0\nTypeError: Cannot perform %TypedArray%.prototype.fill on a detached ArrayBuffer\n2 false\ntrue false", o);
    }

    [Fact]
    public void SetSliceSubarraySortAndSpecies()
    {
        string o = Run("""
            const a = new Int16Array([5, 1, 4, 2, 3]);
            a.set(a.subarray(0, 3), 2);                 // overlapping copy through one buffer
            print(a.join());
            print(new Float64Array([3, NaN, -0, 0, -Infinity]).sort().join() + ' ' + Object.is(new Float64Array([0, -0]).sort()[0], -0));
            print(new Uint8Array([3, 1, 2]).toSorted((x, y) => y - x).join() + ' ' + new Uint8Array([1, 2, 3]).toReversed().with(0, 9).join());
            class My extends Uint8Array {}
            const m = new My([1, 2, 3, 4]);
            print((m.slice(1) instanceof My) + ' ' + (m.map(x => x * 2) instanceof My) + ' ' + m.subarray(1, 3).byteOffset);
            print(new Uint8Array([1, 2, 3]).indexOf(2) + ' ' + new Float32Array([NaN]).includes(NaN) + ' ' + new Float32Array([NaN]).indexOf(NaN));
            """);
        Assert.Equal("5,1,5,1,4\n-Infinity,0,0,3,NaN true\n3,2,1 9,2,1\ntrue true 1\n1 true -1", o);
    }

    [Fact]
    public void Base64AndHexRoundTrip()
    {
        string o = Run("""
            print(new Uint8Array([72, 101, 108, 108, 111]).toBase64() + ' ' + new Uint8Array([251, 255]).toBase64({ alphabet: 'base64url', omitPadding: true }));
            print(new Uint8Array([0, 15, 255]).toHex() + ' ' + Uint8Array.fromHex('00ff10').join());
            print(Uint8Array.fromBase64('SGVs bG8=').join());
            const t = new Uint8Array(4);
            const r = t.setFromBase64('SGVsbG8=');
            print(r.read + ',' + r.written + ' ' + t.join());
            for (const bad of ['SGVsbG8', 'SG=Vs', '@@@@']) {
              try { Uint8Array.fromBase64(bad, { lastChunkHandling: 'strict' }); print('ok'); } catch (e) { print(e.name); }
            }
            print(Uint8Array.fromBase64('SGVsbA', { lastChunkHandling: 'stop-before-partial' }).length);
            """);
        Assert.Equal("SGVsbG8= -_8\n000fff 0,255,16\n72,101,108,108,111\n4,3 72,101,108,0\nSyntaxError\nSyntaxError\nSyntaxError\n3", o);
    }

    [Fact]
    public void AtomicsOperateAndReturnOldValues()
    {
        string o = Run("""
            const i = new Int32Array(new SharedArrayBuffer(16));
            print([Atomics.add(i, 0, 5), Atomics.sub(i, 0, 2), Atomics.or(i, 0, 8), Atomics.and(i, 0, 10), Atomics.xor(i, 0, 3), Atomics.exchange(i, 0, 7), Atomics.compareExchange(i, 0, 7, 9), Atomics.load(i, 0)].join());
            print(Atomics.store(i, 1, 3.9) + ' ' + Atomics.store(i, 1, -0) + ' ' + Atomics.isLockFree(4));
            const u = new Uint8Array(1); print(Atomics.add(u, 0, 300) + ',' + u[0]);
            const b = new BigInt64Array(1); Atomics.store(b, 0, -1n); print(Atomics.add(b, 0, 2n) + ',' + b[0]);
            try { Atomics.add(new Float64Array(1), 0, 1); } catch (e) { print(e.name); }
            try { Atomics.add(u, 2, 1); } catch (e) { print(e.name + ': ' + e.message); }
            print(Atomics.notify(i, 0) + ' ' + Atomics.notify(new Int32Array(4), 0));
            """);
        Assert.Equal("0,5,3,11,10,9,7,9\n3 0 true\n0,44\n-1,1\nTypeError\nRangeError: Invalid atomic access index\n0 0", o);
    }

    [Fact]
    public void WaitReturnsImmediatelyAndNotifyWakesWaitAsync()
    {
        string o = Run("""
            const i = new Int32Array(new SharedArrayBuffer(16));
            const w = Atomics.waitAsync(i, 0, 0, 1000);
            print(w.async + ' ' + Atomics.waitAsync(i, 0, 1).value + ' ' + Atomics.waitAsync(i, 0, 0, 0).value);
            print(Atomics.notify(i, 1) + ' ' + Atomics.notify(i, 0) + ' ' + Atomics.notify(i, 0));
            w.value.then(v => print('woken ' + v));
            print(Atomics.wait(i, 3, 0, 5) + ' ' + Atomics.wait(i, 3, 1));
            """);
        Assert.Equal("true not-equal timed-out\n0 1 0\ntimed-out not-equal\nwoken ok", o);
    }

    [Fact]
    public void WaitAsyncTimeoutsFireAfterTheQueueDrains()
    {
        var host = new PrintHost();
        ScriptEngine e = Engine(host);
        e.Evaluate("""
            const i = new Int32Array(new SharedArrayBuffer(8));
            Atomics.waitAsync(i, 0, 0, 50).value.then(v => print('slow ' + v));
            Atomics.waitAsync(i, 1, 0, 10).value.then(v => print('fast ' + v));
            Atomics.waitAsync(i, 0, 0).value.then(v => print('never ' + v));
            Promise.resolve().then(() => print('first'));
            """, "w.js", Ct);
        Assert.Equal("first\nfast timed-out\nslow timed-out", host.Output.ToString().TrimEnd('\n'));
    }

    // ---- limits ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("new ArrayBuffer(2 ** 53 - 1)")]
    [InlineData("new ArrayBuffer(2 ** 33)")]
    [InlineData("new ArrayBuffer(1, { maxByteLength: 2 ** 40 })")]
    [InlineData("new Uint8Array(2 ** 40)")]
    [InlineData("new Float64Array(2 ** 29)")]
    [InlineData("new Uint8Array({ length: 2 ** 53 - 1 })")]
    [InlineData("new Uint8Array(new ArrayBuffer(8), 0, 2 ** 40)")]
    [InlineData("new ArrayBuffer(8).transfer(2 ** 40)")]
    [InlineData("new DataView(new ArrayBuffer(8), 0, 2 ** 40)")]
    public void HugeOrInvalidLengthsThrowRangeError(string expression)
    {
        string o = Run("try { " + expression + "; print('no error'); } catch (e) { print(e.name); }");
        Assert.Equal("RangeError", o);
    }

    [Fact]
    public void BuffersAreChargedToTheMemoryLimit()
    {
        var e = Assert.Throws<JsLimitException>(() => Run("new ArrayBuffer(200 * 1024 * 1024)", l => l.MaxAllocatedBytes = 64L << 20));
        Assert.Equal(LimitKind.Memory, e.Kind);

        e = Assert.Throws<JsLimitException>(() => Run("const r = new ArrayBuffer(8, { maxByteLength: 1 << 30 }); r.resize(1 << 29)", l => l.MaxAllocatedBytes = 64L << 20));
        Assert.Equal(LimitKind.Memory, e.Kind);

        e = Assert.Throws<JsLimitException>(() => Run("const a = new Uint8Array(1 << 25); while (true) { a.slice(); }", l => l.MaxAllocatedBytes = 256L << 20));
        Assert.Equal(LimitKind.Memory, e.Kind);
    }

    [Fact]
    public void LoopsOverLengthChargeFuel()
    {
        var e = Assert.Throws<JsLimitException>(() => Run("const a = new Uint8Array(1 << 24); for (let i = 0; i < 1000; i++) a.fill(i);", l => l.Fuel = 5_000_000));
        Assert.Equal(LimitKind.Fuel, e.Kind);

        e = Assert.Throws<JsLimitException>(() => Run("new Uint8Array(1 << 24).toBase64().length; for (;;) new Uint8Array(1 << 24).indexOf(1);", l => l.Fuel = 5_000_000));
        Assert.Equal(LimitKind.Fuel, e.Kind);
    }

    [Fact]
    public void BoundaryErrorMessagesFollowNode()
    {
        string o = Run("""
            const t = f => { try { f(); } catch (e) { print(e.name + ': ' + e.message); } };
            t(() => new ArrayBuffer(-1));
            t(() => ArrayBuffer(1));
            t(() => new Uint32Array(new ArrayBuffer(5)));
            t(() => new Uint32Array(new ArrayBuffer(8), 1));
            t(() => new Uint8Array(new ArrayBuffer(4), 5));
            t(() => new BigInt64Array(new Uint8Array(1)));
            t(() => new Uint8Array(2).set([1, 2, 3]));
            t(() => new Uint8Array(2).with(5, 1));
            t(() => Object.getPrototypeOf(Uint8Array)());
            t(() => new DataView(1));
            t(() => Atomics.wait(new Int32Array(4), 0, 0, 0));
            """);
        Assert.Equal(
            "RangeError: Invalid array buffer length\n" +
            "TypeError: Constructor ArrayBuffer requires 'new'\n" +
            "RangeError: byte length of Uint32Array should be a multiple of 4\n" +
            "RangeError: start offset of Uint32Array should be a multiple of 4\n" +
            "RangeError: Start offset 5 is outside the bounds of the buffer\n" +
            "TypeError: Cannot mix BigInt and other types, use explicit conversions\n" +
            "RangeError: offset is out of bounds\n" +
            "RangeError: Invalid typed array index\n" +
            "TypeError: Abstract class TypedArray not directly constructable\n" +
            "TypeError: First argument to DataView constructor must be an ArrayBuffer\n" +
            "TypeError: [object Int32Array] is not a shared typed array.",
            o);
    }
}
