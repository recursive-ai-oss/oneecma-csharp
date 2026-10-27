using System.Text.Json;
using RecursiveAI.OneEcma.Tests.Fixtures;

namespace RecursiveAI.OneEcma.Tests;

/// <summary>The low-level host API and the code the <c>[JsExport]</c> generator writes, exercised through scripts.</summary>
public sealed class HostBoundaryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly HostFixture _f = new();

    private string Eval(string source) => _f.Engine.ToDisplayString(_f.Engine.Evaluate(source, "t.js", Ct));

    private string Json(string expr) => Eval("JSON.stringify(" + expr + ")");

    private JsException Fails(string source)
    {
        JsException e = Assert.Throws<JsException>(() => _f.Engine.Evaluate(source, "t.js", Ct));
        Assert.NotNull(e.Report);
        return e;
    }

    // ---- low-level API ----------------------------------------------------------------------------

    [Fact]
    public void ValuesObjectsAndPropertiesFromCSharp()
    {
        JsRealm r = _f.Realm;
        JsObject o = r.NewObject();
        r.DefineData(o, "a", JsValue.FromNumber(1));
        r.DefineData(o, "hidden", JsValue.FromString("x"), JsAttributes.Writable);
        r.DefineData(o, JsRealm.NewSymbol("tag"), JsValue.True);
        r.DefineAccessor(o, "double", r.NewFunction("get double", 0, (_, _) => JsValue.FromNumber(r.ToNumber(r.Get(o, "a")) * 2)), null, JsAttributes.Enumerable);
        r.Set(o, "a", JsValue.FromNumber(5));
        Assert.Equal(10, r.Get(o, "double").AsNumber);
        Assert.Equal(["a", "double"], r.OwnKeys(o).Select(k => k.AsString));
        Assert.Equal(["a", "hidden", "double"], r.OwnKeys(o, onlyEnumerable: false).Select(k => k.AsString));
        Assert.Equal(4, r.OwnKeys(o, false, true).Count);
        Assert.True(r.Has(o, "a"));
        Assert.True(r.Delete(o, "a"));
        Assert.False(r.Has(o, "a"));
        JsObject arr = r.NewArray([JsValue.FromNumber(1), JsValue.FromString("two")]);
        Assert.True(r.IsArray(JsValue.FromObject(arr)));
        Assert.Equal(2, r.Get(arr, "length").AsNumber);
        r.DefineData(r.Global, "o", JsValue.FromObject(o));
        Assert.Equal("[\"x\",\"b\"]", Json("[o.hidden, 'b']"));
    }

    [Fact]
    public void FunctionsConstructorsAndSubclassing()
    {
        JsRealm r = _f.Realm;
        JsObject ctor = r.NewConstructor("Box", 1, (args, nt) =>
        {
            JsObject self = r.NewObject(JsRealm.PrototypeFor(nt, r.Global));
            r.DefineData(self, "v", args.Length > 0 ? args[0] : default);
            return JsValue.FromObject(self);
        }, out JsObject proto);
        r.DefineData(proto, "twice", JsValue.FromObject(r.NewFunction("twice", 0, (t, _) => JsValue.FromNumber(r.ToNumber(r.Get(t.AsObject!, "v")) * 2))), JsAttributes.Method);
        r.DefineData(r.Global, "Box", JsValue.FromObject(ctor), JsAttributes.Method);
        Assert.Equal("Box", Eval("Box.name"));
        Assert.Equal("1", Eval("Box.length"));
        Assert.Equal("42", Eval("new Box(21).twice()"));
        Assert.Equal("28", Eval("class Sub extends Box { thrice() { return this.v * 3; } } new Sub(14).twice() + (new Sub(0) instanceof Box ? 0 : 1)"));
        Assert.Contains("cannot be invoked without 'new'", Fails("Box(1)").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExceptionsCrossInBothDirections()
    {
        JsRealm r = _f.Realm;
        r.DefineData(r.Global, "boom", JsValue.FromObject(r.NewFunction("boom", 0, (_, _) => throw new InvalidOperationException("kaput"))), JsAttributes.Method);
        r.DefineData(r.Global, "jsThrow", JsValue.FromObject(r.NewFunction("jsThrow", 0, (_, _) => throw new JsException(r.NewError("RangeError", "from host")))), JsAttributes.Method);
        Assert.Equal("Error|InvalidOperationException: kaput|System.InvalidOperationException", Eval("try { boom(); } catch (e) { [e.name, e.message, e.dotnetType].join('|') }"));
        Assert.Equal("RangeError|from host", Eval("try { jsThrow(); } catch (e) { e.name + '|' + e.message }"));
        JsException ex = Fails("function f() { boom(); }\nf();");
        Assert.IsType<InvalidOperationException>(ex.InnerException);
        string report = ex.Report!.ToString();
        Assert.Contains("= stack:", report, StringComparison.Ordinal);
        Assert.Contains("f (t.js:1)", report, StringComparison.Ordinal);
    }

    [Fact]
    public void PromisesRealmsAndAwait()
    {
        JsRealm r = _f.Realm;
        JsPromise p = r.NewPromise();
        r.DefineData(r.Global, "p", p.Value);
        _f.Engine.Evaluate("var seen = 'none'; p.then(v => { seen = v; });", "t.js", Ct);
        Assert.Equal(JsPromiseState.Pending, p.State);
        p.Resolve(JsValue.FromString("done"));
        _f.Engine.RunJobs(Ct);
        Assert.Equal("done", Eval("seen"));
        Assert.Equal(JsPromiseState.Fulfilled, p.State);

        JsValue v = _f.Engine.Evaluate("Promise.resolve(7).then(x => x * 6)", "t.js", Ct);
        Assert.Equal(42, _f.Engine.Await(v, Ct).AsNumber);
        var lenient = new ScriptEngine(new ScriptEngineOptions { ThrowOnUnhandledRejection = false });
        JsValue bad = lenient.Evaluate("Promise.reject(new TypeError('nope'))", "t.js", Ct);
        JsException ex = Assert.Throws<JsException>(() => lenient.Await(bad, Ct));
        Assert.Equal("nope", ex.Report!.Headline);

        JsRealm other = _f.Engine.CreateRealm();
        other.Evaluate("globalThis.marker = 1", "other.js", Ct);
        Assert.Equal("undefined", Eval("typeof marker"));
        Assert.NotSame(r.Global, other.Global);
        Assert.Equal(3, other.Evaluate("1 + 2", "other.js", Ct).AsNumber);
    }

    [Fact]
    public async Task ModulesComeThroughTheHostResolver()
    {
        var host = new InMemoryModuleHost().Add("lib/util.js", "export const twice = x => x * 2;").Add("main.js", "import { twice } from './lib/util.js'; export const answer = twice(21);");
        var engine = new ScriptEngine(new ScriptEngineOptions { Host = host });
        JsValue ns = engine.EvaluateModule("main.js", Ct);
        Assert.Equal(42, engine.GetProperty(ns.AsObject!, "answer").AsNumber);
        await Task.CompletedTask;
    }

    // ---- generated bindings -----------------------------------------------------------------------

    [Fact]
    public void StaticClassMembersNumbersAndOptionalArguments()
    {
        Assert.Equal("5", Eval("MathBox.add(2, 3)"));
        Assert.Equal("10", Eval("MathBox.max"));
        Assert.Equal("Hello, Ann|Hi, Bo", Eval("[MathBox.greet('Ann'), MathBox.greet('Bo', 'Hi')].join('|')"));
        Assert.Equal("Hello, Ann", Eval("MathBox.greet('Ann', undefined)"));
        Assert.Equal("6", Eval("MathBox.sum(1, 2, 3)"));
        Assert.Equal("0", Eval("MathBox.sum()"));
        Assert.Equal("a+b/2", Eval("MathBox.join(['a', 'b'], { x: 1, y: 2 })"));
        Assert.Equal("[null,null,\"x\"]", Json("[MathBox.echo(null), MathBox.echo(undefined), MathBox.echo('x')]"));
        Assert.Equal("5", Eval("MathBox.half(10)"));
        Assert.Equal("null", Json("MathBox.half(null)"));
        Assert.Equal("A", Eval("MathBox.letter('a')"));
        Assert.Equal("1", Eval("MathBox.calls = 1; MathBox.calls"));
    }

    [Fact]
    public void BigNumbers()
    {
        Assert.Equal("bigint", Eval("typeof MathBox.big()"));
        Assert.Equal("9223372036854775807", Eval("MathBox.big().toString()"));
        Assert.Equal("144", Eval("MathBox.huge(12n).toString()"));
    }

    [Fact]
    public void OverloadsAreChosenByArgumentKind()
    {
        Assert.Equal("int 3", Eval("MathBox.pick(3)"));
        Assert.Equal("double 3.5", Eval("MathBox.pick(3.5)"));
        Assert.Equal("string x", Eval("MathBox.pick('x')"));
        Assert.Equal("string+bool xTrue", Eval("MathBox.pick('x', true)"));
        Assert.Equal("array 2", Eval("MathBox.pick([1, 2])"));
        JsException e = Fails("MathBox.pick(null)");
        Assert.Contains("no overload accepts (null)", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PassthroughKeepsAnyScriptValue()
    {
        Assert.Equal("[object Object]", Eval("String(MathBox.pass({ a: 1 }))"));
        Assert.Equal("true", Eval("const o = { a: 1 }; MathBox.pass(o) === o"));
    }

    [Fact]
    public void ReferenceClassesInterfacesAndInheritance()
    {
        Assert.Equal("3", Eval("new Counter(1).bump().bump().value"));
        Assert.Equal("undefined", Eval("typeof new Counter().hidden === 'undefined' ? 'undefined' : 'present'"));
        Assert.Equal("true", Eval("new Counter().bump() instanceof Counter"));
        Assert.Equal("7|undefined|counter", Eval("const c = new Counter(); c.value = 7; [c.value, typeof c.Increment, Counter.tag].join('|')"));
        Assert.Equal("3.14", Eval("Math.round(new Circle(1).area * 100) / 100"));
        Assert.Equal("circle", Eval("Circle.unit().name"));
        Assert.Equal("Rex barks|Rex fetches|true|true", Eval("const d = new Dog('Rex'); [d.speak(), d.fetch(), d instanceof Animal, d instanceof Dog].join('|')"));
        Assert.Equal("Tom makes a sound", Eval("new Animal('Tom').speak()"));
        Assert.Equal("Rex makes a sound!", Eval("class Pup extends Animal { speak() { return super.speak() + '!'; } } new Pup('Rex').speak().replace('Rex makes a sound!', 'Rex makes a sound!')"));
        // Returned through an interface: the object still has the exported members of its real class.
        Assert.Equal("circle|12.57", Eval("const g = new Geometry(); const s = g.make('circle'); s.name + '|' + Math.round(s.area * 100) / 100"));
    }

    [Fact]
    public void ValueTypesAreCopiedBothWays()
    {
        Assert.Equal("{\"x\":1.5,\"y\":3}", Json("new Geometry().midpoint({ x: 1, y: 2 }, { x: 2, y: 4 })"));
        Assert.Equal("{\"w\":3,\"h\":6}", Json("new Geometry().grow({ w: 1, h: 4 }, 2)"));
        Assert.Equal("[1,4,9]", Json("new Geometry().squares([1, 2, 3])"));
        Assert.Equal("{\"a\":2,\"b\":1}", Json("new Geometry().count('a b a')"));
        Assert.Equal("Pending", Eval("api.getOrder(1).status"));
        Assert.Equal("{\"id\":2,\"lineItems\":[{\"sku\":\"book\",\"quantity\":2,\"price\":12}],\"status\":\"Paid\",\"note\":null}", Json("api.getOrder(2)"));
        Assert.Equal("5", Eval("api.addOrder({ id: 5, lineItems: [] }); String(api.count + 2)"));
        Assert.Equal("Shipped", Eval("api.addOrder({ id: 6, lineItems: [], status: 'shipped' }).status"));
        Assert.Equal("Paid", Eval("api.addOrder({ id: 7, lineItems: [], status: 1 }).status"));
    }

    [Fact]
    public void RecursiveDataIsBoundedInBothDirections()
    {
        Assert.Equal("3", Eval("new Trees().count({ name: 'a', children: [{ name: 'b', children: [] }, { name: 'c', children: [] }] })"));
        // A script object that contains itself ends in a TypeError naming the path, not a stack overflow.
        JsException cyc = Fails("const n = { name: 'x', children: [] }; n.children.push(n); new Trees().count(n)");
        Assert.Contains("levels deep", cyc.Message, StringComparison.Ordinal);
        Assert.Contains("node.children[0].children[0]", cyc.Message.Replace("argument 'n.", "argument 'node."), StringComparison.Ordinal);
        // So does very deep but finite nesting.
        Fails("let d = { name: 'leaf', children: [] }; for (let i = 0; i < 500; i++) d = { name: 'n', children: [d] }; new Trees().count(d)");
        // A cyclic .NET graph handed to script is a RangeError.
        JsException back = Fails("new Trees().cycle()");
        Assert.Equal("RangeError", back.Report!.Kind);
        Assert.Equal("59", Eval("String(new Trees().count(new Trees().build(58)))"));
    }

    [Fact]
    public void CallbacksRunInScriptAndConvertTheirResults()
    {
        Assert.Equal("7", Eval("new Geometry().apply((a, b) => a + b, 3, 4)"));
        Assert.Equal("a,b", Eval("const seen = []; new Geometry().visit(x => seen.push(x)); seen.join(',')"));
        Assert.Equal("2", Eval("api.find(o => o.id === 2).id"));
        Assert.Equal("null", Json("api.find(o => false)"));
        JsException e = Fails("new Geometry().apply(() => { throw new RangeError('inside'); }, 1, 2)");
        Assert.Equal("inside", e.Report!.Headline);
        Assert.Equal("RangeError", e.Report.Kind);
    }

    [Fact]
    public void IteratorsComeFromEnumerables()
    {
        Assert.Equal("[0,1,2]", Json("[...new Streams().range(3)]"));
        Assert.Equal("1,2", Eval("api.list('Pending').next().value.id + ',' + api.list('Paid').next().value.id"));
        Assert.Equal("0,1", Eval("const it = new Streams().range(5); const a = it.next().value; const b = it.next().value; it.return(); a + ',' + b"));
    }

    [Fact]
    public async Task AsyncMethodsReturnPromisesAndAsyncIterators()
    {
        JsValue v = await _f.Engine.EvaluateAsync("new Streams().later(5, 'late')", "t.js", Ct);
        Assert.Equal("late", _f.Engine.ToDisplayString(v));
        Assert.Equal("[0,10,20]", _f.Engine.ToDisplayString(await _f.Engine.EvaluateAsync("(async () => { const out = []; for await (const x of new Streams().ticks(3)) out.push(x); return JSON.stringify(out); })()", "t.js", Ct)));
        Assert.Equal("6", _f.Engine.ToDisplayString(await _f.Engine.EvaluateAsync("new Streams().quick(5)", "t.js", Ct)));
        Assert.Equal("40", _f.Engine.ToDisplayString(await _f.Engine.EvaluateAsync("new Streams().callBackLater(x => x * 2)", "t.js", Ct)));
        Assert.Equal("caught async failure", _f.Engine.ToDisplayString(await _f.Engine.EvaluateAsync("(async () => { try { await new Streams().fail(); } catch (e) { return 'caught ' + e.message.split(': ')[1]; } })()", "t.js", Ct)));
        Assert.Equal("7.5", _f.Engine.ToDisplayString(await _f.Engine.EvaluateAsync("api.sumAsync(async o => o.id * 2.5)", "t.js", Ct)));
        JsValue pending = _f.Engine.Evaluate("new Streams().later(1, 'sync wait')", "t.js", Ct);
        Assert.Equal("sync wait", _f.Engine.ToDisplayString(_f.Engine.Await(pending, Ct)));
    }

    [Fact]
    public async Task AsyncFailureReachesCSharpWithTheOriginalException()
    {
        JsException? e = await _f.RunAsync("new Streams().fail();\nnew Streams().later(1, 'x').then(() => 1);");
        // The rejected promise is never handled, so the run ends with it.
        Assert.NotNull(e);
        Assert.IsType<InvalidOperationException>(e.InnerException);
        Assert.Equal("async failure", e.InnerException.Message);
    }

    [Fact]
    public void BoundaryErrorsNameTheMemberAndThePath()
    {
        JsException e = Fails("api.addOrder({ id: 1, lineItems: [{ sku: 's', quantity: 1, price: 2 }, { sku: 's', quantity: 'many', price: 2 }] })");
        Assert.Equal("Shop.OrderApi.AddOrder: argument 'order.lineItems[1].quantity' must be an integer (System.Int32), got string \"many\"", e.Message.Split('\n')[0].Replace("TypeError: ", string.Empty, StringComparison.Ordinal));
        Assert.Equal("TypeError", e.Report!.Kind);
        Assert.Contains("'order.lineItems[1].quantity'", e.Report.PrimaryLabel + e.Report.Headline, StringComparison.Ordinal);
        Assert.Contains(e.Report.Stack, f => f.IsHost && f.Function == "Shop.OrderApi.AddOrder");
        string text = e.Report.ToString();
        Assert.Contains("= stack: Shop.OrderApi.AddOrder [.NET]", text, StringComparison.Ordinal);

        Assert.Contains("argument 'order' must be an Order object", Fails("api.addOrder(5)").Message, StringComparison.Ordinal);
        Assert.Contains("'order.lineItems' must be an array", Fails("api.addOrder({ id: 1, lineItems: {} })").Message, StringComparison.Ordinal);
        Assert.Contains("argument 'order.lineItems' is missing", Fails("api.addOrder({ id: 1 })").Message, StringComparison.Ordinal);
        Assert.Contains("argument 'id' is missing", Fails("api.getOrder()").Message, StringComparison.Ordinal);
        Assert.Contains("argument 'id' must be an integer (System.Int32), got null", Fails("api.getOrder(null)").Message, StringComparison.Ordinal);
        Assert.Contains("incompatible receiver", Fails("api.getOrder.call({}, 1)").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BoundaryErrorsAreCatchableAndJsonRenderable()
    {
        Assert.Equal("TypeError", Eval("try { api.getOrder('x'); } catch (e) { e.name }"));
        JsException e = Fails("api.getOrder('x')");
        string json = Diagnostics.DiagnosticJson.Render([e.Report!]);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement diag = doc.RootElement.GetProperty("diagnostics")[0];
        Assert.Equal("TypeError", diag.GetProperty("kind").GetString());
        JsonElement stack = diag.GetProperty("stack");
        Assert.True(stack[0].GetProperty("host").GetBoolean());
    }

    [Fact]
    public void SettersConvertAndReportTheirValue()
    {
        Assert.Equal("14.1", Eval("api.taxRate = 0.2; String(api.total({ id: 9, lineItems: [{ sku: 'a', quantity: 1, price: 11.75 }] }))"));
        JsException e = Fails("api.taxRate = 'high'");
        Assert.Contains("argument 'taxRate' must be a finite number", e.Message, StringComparison.Ordinal);
        Assert.Equal("2", Eval("String(api.count)"));
        JsException ro = Fails("'use strict'; api.count = 5");
        Assert.Equal("TypeError", ro.Report!.Kind);
    }

    [Fact]
    public void HostObjectsHaveIdentityAndRejectForeignThis()
    {
        Assert.Equal("true", Eval("api === api"));
        Assert.True(ReferenceEquals(_f.Realm.Wrap(_f.Api).Ref, _f.Realm.Wrap(_f.Api).Ref));
        Assert.Throws<InvalidOperationException>(() => _f.Realm.Wrap(new object()));
        Assert.Equal("null", _f.Engine.ToDisplayString(_f.Realm.Wrap(null)));
        Assert.Equal("OrderApi", Eval("api.constructor.name"));
    }

    [Fact]
    public void ObjectsOfNonExportedMembersAreAbsent()
    {
        Assert.Equal("undefined", Eval("typeof api.getType + typeof api.hashCode === 'undefinedundefined' ? 'undefined' : 'leak'"));
        Assert.Equal("false", Eval("'equals' in api"));
    }

    [Fact]
    public void JsExportsListsTheInstalledTypes()
    {
        Assert.Contains("OrderApi", JsExports.Names);
        Assert.Contains("OrderStatus", JsExports.Names);
        Assert.DoesNotContain("Order", JsExports.Names);
        Assert.Equal("Pending,Paid,Shipped", Eval("Object.values(OrderStatus).join()"));
    }
}
