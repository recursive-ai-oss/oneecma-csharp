using RecursiveAI.OneEcma;
using RecursiveAI.OneEcma.Diagnostics;
using Shop;

// A host that exposes an order service to scripts. Everything crossing the boundary is checked, and a mistake in a script
// comes back as a report that names the .NET member, the argument path and what was expected.
var engine = new ScriptEngine();
JsRealm realm = engine.MainRealm;
JsExports.InstallAll(realm);
realm.DefineData(realm.Global, "api", realm.Wrap(new OrderApi()));
engine.DefineGlobalFunction("print", 1, (_, a) =>
{
    Console.WriteLine(engine.ToDisplayString(a[0]));
    return JsValue.Undefined;
});

string[] scripts =
[
    "print(api.getOrder(1).lineItems.length + ' lines, total ' + api.total(api.getOrder(1)));",
    "(async () => { const o = await api.fetchOrderAsync(2); print('fetched ' + o.id + ' ' + o.status); })()",
    "for (const o of api.list('Pending')) print('pending ' + o.id); api.forEach(o => print('visit ' + o.id));",
    "print(api.describe(7) + ', ' + api.describe('x'));",
    "try { api.getOrder(99); } catch (e) { print(e.message + ' [' + e.dotnetType + ']'); }",
    "api.addOrder({ id: 3, lineItems: [{ sku: 'pen', quantity: 1, price: 1 }, { sku: 'ink', quantity: 2, price: '7.25' }] });",
    "api.addOrder({ id: 4, lineItem: [] });",
    "api.getOrder(99);",
];

int failures = 0;
foreach (string script in scripts)
{
    try
    {
        await engine.EvaluateAsync(script, "script.js");
    }
    catch (JsException e)
    {
        failures++;
        Console.WriteLine(e.Report is { } r ? DiagnosticRenderer.Render(r) : e.Message);
        if (e.InnerException is { } inner)
        {
            Console.WriteLine("(.NET cause: " + inner.GetType().Name + ")");
        }
    }
}

return failures == 3 ? 0 : 1;
