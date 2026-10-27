using System.Text.Json;
using System.Text.Json.Serialization;

namespace RecursiveAI.OneEcma.Tests;

public sealed record SearchArgs(string Query, int Limit = 10);

public sealed record SearchHit(string Title, int Score);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SearchArgs))]
[JsonSerializable(typeof(SearchHit[]))]
internal sealed partial class ToolJson : JsonSerializerContext;

/// <summary>The JSON Schema tool helper: validation messages, results, async tools, typed tools.</summary>
public sealed class JsonSchemaToolTests
{
    private const string OrderSchema = """
        {
          "type": "object",
          "required": ["id", "items"],
          "additionalProperties": false,
          "properties": {
            "id": { "type": "integer", "minimum": 1 },
            "status": { "enum": ["open", "closed"] },
            "items": { "type": "array", "minItems": 1, "items": { "type": "object", "required": ["sku", "price"], "properties": { "sku": { "type": "string", "pattern": "^[a-z]+$" }, "price": { "type": "number", "exclusiveMinimum": 0 } } } },
            "tags": { "type": "array", "items": { "$ref": "#/$defs/tag" } }
          },
          "$defs": { "tag": { "type": "string", "maxLength": 5 } }
        }
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly ScriptEngine _engine = new();

    private JsRealm Realm => _engine.MainRealm;

    private JsException Fails(string source) => Assert.Throws<JsException>(() => _engine.Evaluate(source, "agent.js", Ct));

    private void DefineOrderTool(List<string>? seen = null) =>
        Realm.DefineData(Realm.Global, "createOrder", JsValue.FromObject(JsonSchemaTools.Define(Realm, "createOrder", OrderSchema, arg =>
        {
            seen?.Add(arg.GetRawText());
            using JsonDocument d = JsonDocument.Parse("{\"ok\":true,\"count\":" + arg.GetProperty("items").GetArrayLength() + ",\"nested\":[1,\"a\",null]}");
            return d.RootElement.Clone();
        })), JsAttributes.Method);

    [Fact]
    public void ValidCallPassesJsonInAndOut()
    {
        var seen = new List<string>();
        DefineOrderTool(seen);
        JsValue v = _engine.Evaluate("createOrder({ id: 3, status: 'open', items: [{ sku: 'pen', price: 1.5 }], tags: ['a'], ignored: undefined, f() {} })", "agent.js", Ct);
        Assert.Equal("{\"id\":3,\"status\":\"open\",\"items\":[{\"sku\":\"pen\",\"price\":1.5}],\"tags\":[\"a\"]}", seen[0]);
        Assert.Equal("{\"ok\":true,\"count\":1,\"nested\":[1,\"a\",null]}", _engine.ToDisplayString(_engine.Evaluate("JSON.stringify(createOrder({ id: 1, items: [{ sku: 'a', price: 2 }] }))", "a.js", Ct)));
        Assert.True(v.IsObject);
    }

    [Theory]
    [InlineData("createOrder({ id: 1, items: [{ sku: 'a', price: '2' }] })", "argument 'args.items[0].price' must be a number, got string \"2\"")]
    [InlineData("createOrder({ id: 0, items: [{ sku: 'a', price: 2 }] })", "argument 'args.id' must be a number >= 1, got number 0")]
    [InlineData("createOrder({ id: 1.5, items: [{ sku: 'a', price: 2 }] })", "argument 'args.id' must be an integer, got number 1.5")]
    [InlineData("createOrder({ items: [] })", "argument 'args.id' is missing; expected an integer (required property)")]
    [InlineData("createOrder({ id: 1, items: [] })", "argument 'args.items' must be an array of at least 1 items, got an array of 0 items")]
    [InlineData("createOrder({ id: 1, items: [{ sku: 'A', price: 2 }] })", "argument 'args.items[0].sku' must be a string matching /^[a-z]+$/, got string \"A\"")]
    [InlineData("createOrder({ id: 1, status: 'opne', items: [{ sku: 'a', price: 2 }] })", "argument 'args.status' must be one of \"open\", \"closed\", got string \"opne\"")]
    [InlineData("createOrder({ id: 1, itemz: [], items: [{ sku: 'a', price: 2 }] })", "argument 'args' has no property 'itemz'")]
    [InlineData("createOrder({ id: 1, items: [{ sku: 'a', price: 2 }], tags: ['ok', 'toolong'] })", "argument 'args.tags[1]' must be a string of at most 5 characters, got string \"toolong\"")]
    [InlineData("createOrder()", "argument 'args' must be an object, got undefined")]
    [InlineData("createOrder([1])", "argument 'args' must be an object, got an array of 1 item")]
    public void InvalidCallsNameThePathAndWhatWasExpected(string script, string expected)
    {
        DefineOrderTool();
        JsException e = Fails(script);
        Assert.Equal("createOrder: " + expected, e.Report!.Headline);
        Assert.Equal("TypeError", e.Report.Kind);
        Assert.Contains("= stack: createOrder [.NET]", e.Report.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void MisspelledEnumAndPropertyGetDidYouMean()
    {
        DefineOrderTool();
        Assert.Contains("did you mean \"open\"?", Fails("createOrder({ id: 1, status: 'opne', items: [{ sku: 'a', price: 2 }] })").Report!.ToString(), StringComparison.Ordinal);
        Assert.Contains("did you mean 'items'?", Fails("createOrder({ id: 1, itemz: [{ sku: 'a', price: 2 }], items: [{ sku: 'a', price: 2 }] })").Report!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void CombinatorsAndConstraints()
    {
        JsonSchemaTools.Check(Realm, "t", """{"anyOf":[{"type":"string"},{"type":"integer"}]}""", JsValue.FromNumber(3));
        Assert.Throws<JsException>(() => JsonSchemaTools.Check(Realm, "t", """{"anyOf":[{"type":"string"},{"type":"integer"}]}""", JsValue.True));
        Assert.Throws<JsException>(() => JsonSchemaTools.Check(Realm, "t", """{"oneOf":[{"type":"number"},{"type":"integer"}]}""", JsValue.FromNumber(3)));
        Assert.Throws<JsException>(() => JsonSchemaTools.Check(Realm, "t", """{"not":{"type":"null"}}""", JsValue.Null));
        Assert.Throws<JsException>(() => JsonSchemaTools.Check(Realm, "t", """{"type":"number","multipleOf":0.5}""", JsValue.FromNumber(0.3)));
        Assert.Throws<JsException>(() => JsonSchemaTools.Check(Realm, "t", """{"const":"a"}""", JsValue.FromString("b")));
        JsonSchemaTools.Check(Realm, "t", """{"type":["string","null"]}""", JsValue.Null);
        JsonSchemaTools.Check(Realm, "t", """{}""", JsValue.FromNumber(1));
        JsonSchemaTools.Check(Realm, "t", """{"type":"object","additionalProperties":{"type":"integer"}}""", _engine.Evaluate("({ a: 1, b: 2 })", "x.js", Ct));
        Assert.Throws<JsException>(() => JsonSchemaTools.Check(Realm, "t", """{"type":"object","additionalProperties":{"type":"integer"}}""", _engine.Evaluate("({ a: 1, b: 'x' })", "x.js", Ct)));
        Assert.Throws<ArgumentException>(() => JsonSchemaTools.Check(Realm, "t", "{ not json", default));
    }

    [Fact]
    public async Task AsyncToolsReturnPromises()
    {
        Realm.DefineData(Realm.Global, "slow", JsValue.FromObject(JsonSchemaTools.DefineAsync(Realm, "slow", """{"type":"object","required":["ms"],"properties":{"ms":{"type":"integer"}}}""", async arg =>
        {
            await Task.Delay(arg.GetProperty("ms").GetInt32(), Ct);
            using JsonDocument d = JsonDocument.Parse("\"woke\"");
            return d.RootElement.Clone();
        })), JsAttributes.Method);
        Assert.Equal("woke", _engine.ToDisplayString(await _engine.EvaluateAsync("slow({ ms: 3 })", "a.js", Ct)));
        JsException? e = null;
        try
        {
            await _engine.EvaluateAsync("slow({ ms: 'soon' })", "a.js", Ct);
        }
        catch (JsException ex)
        {
            e = ex;
        }

        Assert.Equal("slow: argument 'args.ms' must be an integer, got string \"soon\"", e!.Report!.Headline);
    }

    [Fact]
    public void TypedToolsUseSourceGeneratedJsonMetadata()
    {
        Realm.DefineData(Realm.Global, "search", JsValue.FromObject(JsonSchemaTools.Define(Realm, "search", """{"type":"object","required":["query"],"properties":{"query":{"type":"string"},"limit":{"type":"integer"}}}""",
            ToolJson.Default.SearchArgs, ToolJson.Default.SearchHitArray,
            args => [new SearchHit(args.Query.ToUpperInvariant(), args.Limit)])), JsAttributes.Method);
        Assert.Equal("[{\"title\":\"CATS\",\"score\":10}]", _engine.ToDisplayString(_engine.Evaluate("JSON.stringify(search({ query: 'cats' }))", "a.js", Ct)));
        Assert.Contains("argument 'args.query' must be a string", Fails("search({ query: 5 })").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidatedWrapsAnExportedMethodWithAStricterSchema()
    {
        JsRealm r = Realm;
        r.DefineData(r.Global, "target", JsValue.FromObject(r.NewFunction("target", 1, (_, a) => a[0])), JsAttributes.Method);
        JsObject wrapped = JsonSchemaTools.Validated(r, "target2", """{"type":"integer","minimum":0}""", r.Get(r.Global, "target"));
        r.DefineData(r.Global, "target2", JsValue.FromObject(wrapped), JsAttributes.Method);
        Assert.Equal("5", _engine.ToDisplayString(_engine.Evaluate("target2(5)", "a.js", Ct)));
        Assert.Contains("must be a number >= 0", Fails("target2(-1)").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsAreCatchableInScript()
    {
        DefineOrderTool();
        Assert.Equal("TypeError:createOrder: argument 'args.id' must be an integer, got string \"x\"", _engine.ToDisplayString(_engine.Evaluate("try { createOrder({ id: 'x', items: [{ sku: 'a', price: 1 }] }) } catch (e) { e.name + ':' + e.message }", "a.js", Ct)));
    }
}
