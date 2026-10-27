using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;

namespace RecursiveAI.OneEcma;

/// <summary>
/// Registers .NET code as script functions whose argument is checked against a JSON Schema at the boundary. A call with a wrong
/// argument throws a TypeError that names the tool, the path (<c>args.items[2].price</c>), what the schema expects and what was passed,
/// exactly like the errors of exported members. This is how a host exposes tools to scripts written by an agent.
/// Supported keywords: <c>type</c>, <c>enum</c>, <c>const</c>, <c>properties</c>, <c>required</c>, <c>additionalProperties</c>, <c>items</c>,
/// <c>minItems</c>, <c>maxItems</c>, <c>minLength</c>, <c>maxLength</c>, <c>minimum</c>, <c>maximum</c>, <c>exclusiveMinimum</c>,
/// <c>exclusiveMaximum</c>, <c>multipleOf</c>, <c>pattern</c>, <c>allOf</c>, <c>anyOf</c>, <c>oneOf</c>, <c>not</c> and local <c>$ref</c>
/// (<c>#</c>, <c>#/$defs/x</c>, <c>#/definitions/x</c>). Other keywords are ignored.
/// </summary>
public static class JsonSchemaTools
{
    private const int MaxDepth = 64;

    /// <summary>Defines a global-style function: returns the function object; publish it with <see cref="JsRealm.DefineData(JsObject, string, JsValue, JsAttributes)"/>.</summary>
    /// <param name="realm">The realm.</param>
    /// <param name="name">The function name in script.</param>
    /// <param name="inputSchemaJson">The JSON Schema of the single argument.</param>
    /// <param name="handler">Receives the validated argument as JSON and returns the result as JSON.</param>
    /// <returns>The function.</returns>
    /// <exception cref="ArgumentException">The schema is not valid JSON.</exception>
    public static JsObject Define(JsRealm realm, string name, string inputSchemaJson, Func<JsonElement, JsonElement> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Make(realm, name, inputSchemaJson, (arg, _) => JsonToValue(realm, handler(arg)));
    }

    /// <summary>Like <see cref="Define"/>, but the handler is asynchronous and the script function returns a promise.</summary>
    /// <param name="realm">The realm.</param>
    /// <param name="name">The function name in script.</param>
    /// <param name="inputSchemaJson">The JSON Schema of the single argument.</param>
    /// <param name="handler">Receives the validated argument and returns the result as JSON.</param>
    public static JsObject DefineAsync(JsRealm realm, string name, string inputSchemaJson, Func<JsonElement, Task<JsonElement>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Make(realm, name, inputSchemaJson, (arg, _) => JsMarshal.FromTask(realm, name, () => handler(arg), JsonToValue));
    }

    /// <summary>Defines a function over .NET types converted with source-generated <c>System.Text.Json</c> metadata.</summary>
    /// <typeparam name="TArgs">The argument type.</typeparam>
    /// <typeparam name="TResult">The result type.</typeparam>
    /// <param name="realm">The realm.</param>
    /// <param name="name">The function name in script.</param>
    /// <param name="inputSchemaJson">The JSON Schema of the argument.</param>
    /// <param name="argsInfo">Metadata for <typeparamref name="TArgs"/> from a source-generated context.</param>
    /// <param name="resultInfo">Metadata for <typeparamref name="TResult"/> from a source-generated context.</param>
    /// <param name="handler">The implementation.</param>
    public static JsObject Define<TArgs, TResult>(JsRealm realm, string name, string inputSchemaJson, JsonTypeInfo<TArgs> argsInfo, JsonTypeInfo<TResult> resultInfo, Func<TArgs, TResult> handler)
    {
        ArgumentNullException.ThrowIfNull(argsInfo);
        ArgumentNullException.ThrowIfNull(resultInfo);
        ArgumentNullException.ThrowIfNull(handler);
        return Make(realm, name, inputSchemaJson, (arg, _) =>
        {
            TArgs? typed = arg.Deserialize(argsInfo);
            using JsonDocument doc = JsonSerializer.SerializeToDocument(handler(typed!), resultInfo);
            return JsonToValue(realm, doc.RootElement);
        });
    }

    /// <summary>Like the typed <c>Define</c>, but the handler is asynchronous and the script function returns a promise.</summary>
    /// <typeparam name="TArgs">The argument type.</typeparam>
    /// <typeparam name="TResult">The result type.</typeparam>
    /// <param name="realm">The realm.</param>
    /// <param name="name">The function name in script.</param>
    /// <param name="inputSchemaJson">The JSON Schema of the argument.</param>
    /// <param name="argsInfo">Metadata for <typeparamref name="TArgs"/>.</param>
    /// <param name="resultInfo">Metadata for <typeparamref name="TResult"/>.</param>
    /// <param name="handler">The implementation.</param>
    public static JsObject DefineAsync<TArgs, TResult>(JsRealm realm, string name, string inputSchemaJson, JsonTypeInfo<TArgs> argsInfo, JsonTypeInfo<TResult> resultInfo, Func<TArgs, Task<TResult>> handler)
    {
        ArgumentNullException.ThrowIfNull(argsInfo);
        ArgumentNullException.ThrowIfNull(resultInfo);
        ArgumentNullException.ThrowIfNull(handler);
        return Make(realm, name, inputSchemaJson, (arg, _) =>
        {
            TArgs? typed = arg.Deserialize(argsInfo);
            return JsMarshal.FromTask(realm, name, () => handler(typed!), (r, result) =>
            {
                using JsonDocument doc = JsonSerializer.SerializeToDocument(result, resultInfo);
                return JsonToValue(r, doc.RootElement);
            });
        });
    }

    /// <summary>
    /// Wraps an existing script function (for example an exported .NET method fetched from a host object) so its first argument is
    /// validated against a schema before the call. The remaining arguments and <c>this</c> pass through.
    /// </summary>
    /// <param name="realm">The realm.</param>
    /// <param name="name">The name used in error messages and for the new function.</param>
    /// <param name="inputSchemaJson">The JSON Schema of the first argument.</param>
    /// <param name="function">The function to call after validation.</param>
    /// <param name="thisValue">The <c>this</c> value for the call, such as the host object a method belongs to.</param>
    public static JsObject Validated(JsRealm realm, string name, string inputSchemaJson, JsValue function, JsValue thisValue = default)
    {
        ArgumentNullException.ThrowIfNull(realm);
        if (!JsRealm.IsCallable(function))
        {
            throw new ArgumentException("The value is not a function.", nameof(function));
        }

        JsonDocument schema = Parse(inputSchemaJson);
        return realm.NewFunction(name, 1, (_, args) =>
        {
            Validate(realm, name, schema.RootElement, args.Length > 0 ? args[0] : default);
            return realm.Call(function, thisValue, args);
        });
    }

    /// <summary>Checks a script value against a schema without calling anything; throws the same TypeError a tool call would.</summary>
    /// <param name="realm">The realm.</param>
    /// <param name="tool">The tool name used in the message.</param>
    /// <param name="inputSchemaJson">The schema.</param>
    /// <param name="value">The value.</param>
    /// <exception cref="JsException">The value does not match the schema.</exception>
    public static void Check(JsRealm realm, string tool, string inputSchemaJson, JsValue value)
    {
        JsonDocument schema = Parse(inputSchemaJson);
        realm.Run(() =>
        {
            Validate(realm, tool, schema.RootElement, value);
            return 0;
        });
    }

    private static JsObject Make(JsRealm realm, string name, string schemaJson, Func<JsonElement, JsonElement, JsValue> run)
    {
        ArgumentNullException.ThrowIfNull(realm);
        ArgumentNullException.ThrowIfNull(name);
        JsonDocument schema = Parse(schemaJson);
        return realm.NewFunction(name, 1, (_, args) =>
        {
            JsValue arg = args.Length > 0 ? args[0] : default;
            Validate(realm, name, schema.RootElement, arg);
            using JsonDocument doc = ValueToJson(realm, name, arg);
            return run(doc.RootElement, schema.RootElement);
        });
    }

    private static JsonDocument Parse(string schemaJson)
    {
        ArgumentNullException.ThrowIfNull(schemaJson);
        try
        {
            return JsonDocument.Parse(schemaJson);
        }
        catch (JsonException e)
        {
            throw new ArgumentException("The input schema is not valid JSON: " + e.Message, nameof(schemaJson), e);
        }
    }

    // ---- validation ----------------------------------------------------------------------------------

    private static void Validate(JsRealm realm, string tool, JsonElement schema, JsValue value)
    {
        var conv = new JsConv(realm, tool, tool + "(args) with a JSON Schema input");
        conv.Arg("args");
        if (!Check(schema, schema, value, ref conv, 0))
        {
            throw conv.ToException();
        }
    }

    private static bool Check(JsonElement root, JsonElement schema, JsValue v, ref JsConv c, int depth)
    {
        if (schema.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (schema.ValueKind != JsonValueKind.Object)
        {
            return schema.ValueKind != JsonValueKind.False || c.Fail("no value (the schema forbids it)", v);
        }

        if (depth > MaxDepth)
        {
            return c.Fail("a value nested less than " + MaxDepth.ToString(CultureInfo.InvariantCulture) + " levels deep", v);
        }

        if (schema.TryGetProperty("$ref", out JsonElement r) && r.ValueKind == JsonValueKind.String)
        {
            JsonElement target = Resolve(root, r.GetString()!);
            if (!Check(root, target, v, ref c, depth + 1))
            {
                return false;
            }
        }

        if (schema.TryGetProperty("type", out JsonElement t) && !CheckType(t, v, ref c))
        {
            return false;
        }

        if (schema.TryGetProperty("const", out JsonElement k) && !JsonEquals(k, v))
        {
            return c.Fail("the constant " + k.GetRawText(), v);
        }

        if (schema.TryGetProperty("enum", out JsonElement en) && en.ValueKind == JsonValueKind.Array)
        {
            bool found = false;
            var names = new List<string>();
            foreach (JsonElement option in en.EnumerateArray())
            {
                names.Add(option.GetRawText());
                found |= JsonEquals(option, v);
            }

            if (!found)
            {
                return c.Fail("one of " + string.Join(", ", names), v, v.IsString ? NearestOption(v.AsString, en) : null);
            }
        }

        if (v.IsNumber && !CheckNumber(schema, v.AsNumber, v, ref c))
        {
            return false;
        }

        if (v.IsString && !CheckString(schema, v, ref c))
        {
            return false;
        }

        if (v.Ref is JsArray arr && !CheckArray(root, schema, arr, v, ref c, depth))
        {
            return false;
        }

        if (JsMarshal.IsPlainObject(v) && !CheckObject(root, schema, (JsObject)v.Ref!, ref c, depth))
        {
            return false;
        }

        return CheckCombinators(root, schema, v, ref c, depth);
    }

    private static string? NearestOption(string value, JsonElement options)
    {
        var strings = new List<string>();
        foreach (JsonElement o in options.EnumerateArray())
        {
            if (o.ValueKind == JsonValueKind.String)
            {
                strings.Add(o.GetString()!);
            }
        }

        string? near = JsMarshal.Nearest(value, strings);
        return near is null ? null : "did you mean \"" + near + "\"?";
    }

    private static JsonElement Resolve(JsonElement root, string reference)
    {
        if (reference == "#")
        {
            return root;
        }

        if (!reference.StartsWith("#/", StringComparison.Ordinal))
        {
            return default;
        }

        JsonElement cur = root;
        foreach (string part in reference[2..].Split('/'))
        {
            string key = part.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(key, out cur))
            {
                return default;
            }
        }

        return cur;
    }

    private static bool CheckType(JsonElement type, JsValue v, ref JsConv c)
    {
        var names = new List<string>();
        if (type.ValueKind == JsonValueKind.String)
        {
            names.Add(type.GetString()!);
        }
        else if (type.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement e in type.EnumerateArray())
            {
                names.Add(e.GetString() ?? string.Empty);
            }
        }

        foreach (string n in names)
        {
            if (TypeMatches(n, v))
            {
                return true;
            }
        }

        return c.Fail(string.Join(" or ", names.Select(Article)), v);
    }

    private static string Article(string type) => type switch
    {
        "integer" => "an integer",
        "number" => "a number",
        "string" => "a string",
        "boolean" => "a boolean",
        "array" => "an array",
        "object" => "an object",
        "null" => "null",
        _ => "a " + type,
    };

    private static bool TypeMatches(string type, JsValue v) => type switch
    {
        "string" => v.IsString,
        "number" => v.IsNumber,
        "integer" => JsMarshal.IsInteger(v),
        "boolean" => v.IsBoolean,
        "null" => v.IsNull,
        "array" => v.Ref is JsArray,
        "object" => JsMarshal.IsPlainObject(v),
        _ => false,
    };

    private static bool CheckNumber(JsonElement schema, double d, JsValue v, ref JsConv c)
    {
        if (schema.TryGetProperty("minimum", out JsonElement min) && min.TryGetDouble(out double mn) && d < mn)
        {
            return c.Fail("a number >= " + Ops.NumberToStr(mn), v);
        }

        if (schema.TryGetProperty("maximum", out JsonElement max) && max.TryGetDouble(out double mx) && d > mx)
        {
            return c.Fail("a number <= " + Ops.NumberToStr(mx), v);
        }

        if (schema.TryGetProperty("exclusiveMinimum", out JsonElement emin) && emin.TryGetDouble(out double en) && d <= en)
        {
            return c.Fail("a number > " + Ops.NumberToStr(en), v);
        }

        if (schema.TryGetProperty("exclusiveMaximum", out JsonElement emax) && emax.TryGetDouble(out double ex) && d >= ex)
        {
            return c.Fail("a number < " + Ops.NumberToStr(ex), v);
        }

        if (schema.TryGetProperty("multipleOf", out JsonElement mo) && mo.TryGetDouble(out double m) && m > 0 && Math.Abs(d / m - Math.Round(d / m)) > 1e-9)
        {
            return c.Fail("a multiple of " + Ops.NumberToStr(m), v);
        }

        return true;
    }

    private static bool CheckString(JsonElement schema, JsValue v, ref JsConv c)
    {
        string s = v.AsString;
        if (schema.TryGetProperty("minLength", out JsonElement min) && min.TryGetInt32(out int mn) && s.Length < mn)
        {
            return c.Fail("a string of at least " + mn.ToString(CultureInfo.InvariantCulture) + " characters", v);
        }

        if (schema.TryGetProperty("maxLength", out JsonElement max) && max.TryGetInt32(out int mx) && s.Length > mx)
        {
            return c.Fail("a string of at most " + mx.ToString(CultureInfo.InvariantCulture) + " characters", v);
        }

        if (schema.TryGetProperty("pattern", out JsonElement p) && p.ValueKind == JsonValueKind.String)
        {
            string pattern = p.GetString()!;
            bool ok;
            try
            {
                ok = Regex.IsMatch(s, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
            }
            catch (RegexMatchTimeoutException)
            {
                ok = false;
            }

            if (!ok)
            {
                return c.Fail("a string matching /" + pattern + "/", v);
            }
        }

        return true;
    }

    private static bool CheckArray(JsonElement root, JsonElement schema, JsArray arr, JsValue v, ref JsConv c, int depth)
    {
        uint n = arr.Length;
        if (schema.TryGetProperty("minItems", out JsonElement min) && min.TryGetUInt32(out uint mn) && n < mn)
        {
            return c.Fail("an array of at least " + mn.ToString(CultureInfo.InvariantCulture) + " items", v);
        }

        if (schema.TryGetProperty("maxItems", out JsonElement max) && max.TryGetUInt32(out uint mx) && n > mx)
        {
            return c.Fail("an array of at most " + mx.ToString(CultureInfo.InvariantCulture) + " items", v);
        }

        if (schema.TryGetProperty("items", out JsonElement items))
        {
            for (uint i = 0; i < n; i++)
            {
                if (!Check(root, items, arr.Get(i.ToString(CultureInfo.InvariantCulture)), ref c, depth + 1))
                {
                    return c.InIndex((int)i);
                }
            }
        }

        return true;
    }

    private static bool CheckObject(JsonElement root, JsonElement schema, JsObject o, ref JsConv c, int depth)
    {
        if (schema.TryGetProperty("required", out JsonElement req) && req.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement name in req.EnumerateArray())
            {
                string key = name.GetString() ?? string.Empty;
                if (o.Get(key).IsUndefined)
                {
                    string expected = "a value";
                    if (schema.TryGetProperty("properties", out JsonElement ps) && ps.ValueKind == JsonValueKind.Object && ps.TryGetProperty(key, out JsonElement ps1) && ps1.TryGetProperty("type", out JsonElement pt) && pt.ValueKind == JsonValueKind.String)
                    {
                        expected = Article(pt.GetString()!);
                    }

                    c.InProperty(key);
                    return c.Missing(expected + " (required property)");
                }
            }
        }

        var known = new List<string>();
        if (schema.TryGetProperty("properties", out JsonElement props) && props.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty p in props.EnumerateObject())
            {
                known.Add(p.Name);
                JsValue pv = o.Get(p.Name);
                if (!pv.IsUndefined && !Check(root, p.Value, pv, ref c, depth + 1))
                {
                    return c.InProperty(p.Name);
                }
            }
        }

        if (schema.TryGetProperty("additionalProperties", out JsonElement ap) && ap.ValueKind != JsonValueKind.True)
        {
            var keys = new List<PropertyKey>();
            o.OwnKeys(keys);
            foreach (PropertyKey k in keys)
            {
                if (k.IsSymbol || !o.GetOwnProperty(k, out PropertyDescriptor d) || !d.Enumerable || known.Contains(k.ToString()) || IsDropped(o.Get(k)))
                {
                    continue;
                }

                if (ap.ValueKind == JsonValueKind.False)
                {
                    return c.Unknown(k.ToString(), known);
                }

                if (!Check(root, ap, o.Get(k), ref c, depth + 1))
                {
                    return c.InProperty(k.ToString());
                }
            }
        }

        return true;
    }

    /// <summary>Undefined and function values are left out of the JSON form, so they are not extra properties.</summary>
    private static bool IsDropped(JsValue v) => v.IsUndefined || JsMarshal.IsFunctionValue(v);

    private static bool CheckCombinators(JsonElement root, JsonElement schema, JsValue v, ref JsConv c, int depth)
    {
        if (schema.TryGetProperty("allOf", out JsonElement all) && all.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement s in all.EnumerateArray())
            {
                if (!Check(root, s, v, ref c, depth + 1))
                {
                    return false;
                }
            }
        }

        if (schema.TryGetProperty("anyOf", out JsonElement any) && any.ValueKind == JsonValueKind.Array && CountMatches(root, any, v, depth) == 0)
        {
            return c.Fail("a value matching at least one of the allowed shapes", v);
        }

        if (schema.TryGetProperty("oneOf", out JsonElement one) && one.ValueKind == JsonValueKind.Array)
        {
            int n = CountMatches(root, one, v, depth);
            if (n != 1)
            {
                return c.Fail(n == 0 ? "a value matching one of the allowed shapes" : "a value matching exactly one of the allowed shapes (it matches " + n.ToString(CultureInfo.InvariantCulture) + ")", v);
            }
        }

        if (schema.TryGetProperty("not", out JsonElement not))
        {
            var probe = new JsConv();
            if (Check(root, not, v, ref probe, depth + 1))
            {
                return c.Fail("a value the schema's \"not\" rule excludes", v);
            }
        }

        return true;
    }

    private static int CountMatches(JsonElement root, JsonElement options, JsValue v, int depth)
    {
        int n = 0;
        foreach (JsonElement s in options.EnumerateArray())
        {
            var probe = new JsConv();
            if (Check(root, s, v, ref probe, depth + 1))
            {
                n++;
            }
        }

        return n;
    }

    private static bool JsonEquals(JsonElement e, JsValue v) => e.ValueKind switch
    {
        JsonValueKind.String => v.IsString && v.AsString == e.GetString(),
        JsonValueKind.Number => v.IsNumber && e.TryGetDouble(out double d) && d == v.AsNumber,
        JsonValueKind.True => v.IsBoolean && v.AsBoolean,
        JsonValueKind.False => v.IsBoolean && !v.AsBoolean,
        JsonValueKind.Null => v.IsNull,
        _ => false,
    };

    // ---- script <-> JSON ---------------------------------------------------------------------------------

    private static JsonDocument ValueToJson(JsRealm realm, string tool, JsValue value)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            var conv = new JsConv(realm, tool, tool + "(args)");
            conv.Arg("args");
            if (!Write(w, value, ref conv, 0))
            {
                throw conv.ToException();
            }
        }

        return JsonDocument.Parse(stream.ToArray());
    }

    private static bool Write(Utf8JsonWriter w, JsValue v, ref JsConv c, int depth)
    {
        if (depth > MaxDepth)
        {
            return c.Fail("data nested less than " + MaxDepth.ToString(CultureInfo.InvariantCulture) + " levels deep", v);
        }

        if (v.IsUndefined || v.IsNull)
        {
            w.WriteNullValue();
        }
        else if (v.IsBoolean)
        {
            w.WriteBooleanValue(v.AsBoolean);
        }
        else if (v.IsNumber)
        {
            if (double.IsFinite(v.AsNumber))
            {
                w.WriteNumberValue(v.AsNumber);
            }
            else
            {
                w.WriteNullValue();
            }
        }
        else if (v.IsString)
        {
            w.WriteStringValue(v.AsString);
        }
        else if (v.Ref is JsArray arr)
        {
            w.WriteStartArray();
            for (uint i = 0; i < arr.Length; i++)
            {
                if (!Write(w, arr.Get(i.ToString(CultureInfo.InvariantCulture)), ref c, depth + 1))
                {
                    return c.InIndex((int)i);
                }
            }

            w.WriteEndArray();
        }
        else if (JsMarshal.IsPlainObject(v))
        {
            var o = (JsObject)v.Ref!;
            var keys = new List<PropertyKey>();
            o.OwnKeys(keys);
            w.WriteStartObject();
            foreach (PropertyKey k in keys)
            {
                if (k.IsSymbol || !o.GetOwnProperty(k, out PropertyDescriptor d) || !d.Enumerable)
                {
                    continue;
                }

                JsValue pv = o.Get(k);
                if (pv.IsUndefined || pv.IsSymbol || JsMarshal.IsFunctionValue(pv))
                {
                    continue;
                }

                w.WritePropertyName(k.ToString());
                if (!Write(w, pv, ref c, depth + 1))
                {
                    return c.InProperty(k.ToString());
                }
            }

            w.WriteEndObject();
        }
        else
        {
            return c.Fail("JSON-compatible data", v);
        }

        return true;
    }

    private static JsValue JsonToValue(JsRealm realm, JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                return JsValue.FromString(e.GetString()!);
            case JsonValueKind.Number:
                return JsValue.FromNumber(e.GetDouble());
            case JsonValueKind.True:
                return JsValue.True;
            case JsonValueKind.False:
                return JsValue.False;
            case JsonValueKind.Array:
                var items = new List<JsValue>();
                foreach (JsonElement item in e.EnumerateArray())
                {
                    items.Add(JsonToValue(realm, item));
                }

                return JsValue.FromObject(realm.NewArray(items));
            case JsonValueKind.Object:
                JsObject o = realm.NewObject();
                foreach (JsonProperty p in e.EnumerateObject())
                {
                    realm.DefineData(o, p.Name, JsonToValue(realm, p.Value));
                }

                return JsValue.FromObject(o);
            case JsonValueKind.Undefined:
                return default;
            default:
                return JsValue.Null;
        }
    }
}
