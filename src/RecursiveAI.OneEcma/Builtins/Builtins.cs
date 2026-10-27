using System.Numerics;
using System.Text;

namespace RecursiveAI.OneEcma;

/// <summary>Built-in objects needed by the core and by the conformance harness. Each family has a Setup method called from <see cref="Setup"/>.</summary>
internal static partial class Builtins
{
    internal static void Setup(Realm r)
    {
        JsObject g = r.Global;
        Realm.Value(g, "globalThis", JsValue.FromObject(g));
        Realm.Value(g, "undefined", default, PropFlags.None);
        Realm.Value(g, "NaN", JsValue.FromNumber(double.NaN), PropFlags.None);
        Realm.Value(g, "Infinity", JsValue.FromNumber(double.PositiveInfinity), PropFlags.None);
        SetupObject(r);
        SetupFunction(r);
        SetupErrors(r);
        SetupSymbol(r);
        SetupIterators(r);
        SetupArray(r);
        SetupString(r);
        SetupNumberBooleanBigInt(r);
        SetupPromise(r);
        SetupGenerators(r);
        SetupGlobals(r);
        SetupMathJson(r);
        SetupCollections(r);
        SetupWeak(r);
        SetupReflect(r);
        SetupDate(r);
        SetupProxy(r);
        BinaryData.Setup(r);
        SetupHost(r);
        RegExpBuiltins.Setup(r);
    }

    private static JsValue A(ReadOnlySpan<JsValue> a, int i) => i < a.Length ? a[i] : default;

    // ---- property descriptors ----------------------------------------------------------------------------

    internal static PropertyDescriptor ToPropertyDescriptor(JsValue v)
    {
        if (v.Ref is not JsObject o)
        {
            throw Errors.Type("Property description must be an object: " + Ops.DisplayValue(v));
        }

        var d = new PropertyDescriptor();
        if (o.HasProperty("enumerable"))
        {
            d.HasEnumerable = true;
            d.Enumerable = Ops.ToBoolean(o.Get("enumerable"));
        }

        if (o.HasProperty("configurable"))
        {
            d.HasConfigurable = true;
            d.Configurable = Ops.ToBoolean(o.Get("configurable"));
        }

        if (o.HasProperty("value"))
        {
            d.HasValue = true;
            d.Value = o.Get("value");
        }

        if (o.HasProperty("writable"))
        {
            d.HasWritable = true;
            d.Writable = Ops.ToBoolean(o.Get("writable"));
        }

        if (o.HasProperty("get"))
        {
            JsValue gv = o.Get("get");
            if (!gv.IsUndefined && !Ops.IsCallable(gv))
            {
                throw Errors.Type("Getter must be a function: " + Ops.DisplayValue(gv));
            }

            d.HasGet = true;
            d.Get = gv;
        }

        if (o.HasProperty("set"))
        {
            JsValue sv = o.Get("set");
            if (!sv.IsUndefined && !Ops.IsCallable(sv))
            {
                throw Errors.Type("Setter must be a function: " + Ops.DisplayValue(sv));
            }

            d.HasSet = true;
            d.Set = sv;
        }

        if ((d.HasGet || d.HasSet) && (d.HasValue || d.HasWritable))
        {
            throw Errors.Type("Invalid property descriptor. Cannot both specify accessors and a value or writable attribute");
        }

        return d;
    }

    internal static JsValue FromPropertyDescriptor(Realm r, in PropertyDescriptor d)
    {
        var o = r.NewObject();
        if (d.HasValue)
        {
            o.CreateDataProperty("value", d.Value);
        }

        if (d.HasWritable)
        {
            o.CreateDataProperty("writable", Ops.Bool(d.Writable));
        }

        if (d.HasGet)
        {
            o.CreateDataProperty("get", d.Get);
        }

        if (d.HasSet)
        {
            o.CreateDataProperty("set", d.Set);
        }

        if (d.HasEnumerable)
        {
            o.CreateDataProperty("enumerable", Ops.Bool(d.Enumerable));
        }

        if (d.HasConfigurable)
        {
            o.CreateDataProperty("configurable", Ops.Bool(d.Configurable));
        }

        return JsValue.FromObject(o);
    }

    // ---- Object ----------------------------------------------------------------------------------------------

    private static void SetupObject(Realm r)
    {
        JsObject proto = r.ObjectPrototype;
        NativeFunction ctor = r.Ctor("Object", 1, (t, a, nt) =>
        {
            if (nt is not null && !ReferenceEquals(nt, r.Intrinsics["%Object%"]))
            {
                return JsValue.FromObject(Ops.OrdinaryCreateFromConstructor(nt, static x => x.ObjectPrototype));
            }

            JsValue v = A(a, 0);
            return v.IsNullish ? JsValue.FromObject(r.NewObject()) : JsValue.FromObject(Ops.ToObject(v));
        }, proto);

        r.Method(ctor, "getPrototypeOf", 1, (t, a, nt) => { JsObject? p = Ops.ToObject(A(a, 0)).GetPrototypeOf(); return p is null ? JsValue.Null : JsValue.FromObject(p); });
        r.Method(ctor, "setPrototypeOf", 2, (t, a, nt) =>
        {
            JsValue o = Ops.RequireObjectCoercible(A(a, 0));
            JsValue p = A(a, 1);
            if (!p.IsObject && !p.IsNull)
            {
                throw Errors.Type("Object prototype may only be an Object or null: " + Ops.DisplayValue(p));
            }

            if (o.Ref is JsObject oo && !oo.SetPrototypeOf(p.AsObject))
            {
                throw oo is ProxyObject ? ProxyObject.Falsish("setPrototypeOf", "undefined") : Errors.Type(oo.IsExtensibleOp() ? "Cyclic __proto__ value" : "#<Object> is not extensible");
            }

            return o;
        });
        r.Method(ctor, "create", 2, (t, a, nt) =>
        {
            JsValue p = A(a, 0);
            if (!p.IsObject && !p.IsNull)
            {
                throw Errors.Type("Object prototype may only be an Object or null: " + Ops.DisplayValue(p));
            }

            var o = new JsObject(p.AsObject);
            if (!A(a, 1).IsUndefined)
            {
                DefineProperties(o, A(a, 1));
            }

            return JsValue.FromObject(o);
        });
        r.Method(ctor, "defineProperty", 3, (t, a, nt) =>
        {
            if (A(a, 0).Ref is not JsObject o)
            {
                throw Errors.Type("Object.defineProperty called on non-object");
            }

            PropertyKey k = Ops.ToPropertyKey(A(a, 1));
            o.DefinePropertyOrThrow(k, ToPropertyDescriptor(A(a, 2)));
            return A(a, 0);
        });
        r.Method(ctor, "defineProperties", 2, (t, a, nt) =>
        {
            if (A(a, 0).Ref is not JsObject o)
            {
                throw Errors.Type("Object.defineProperties called on non-object");
            }

            DefineProperties(o, A(a, 1));
            return A(a, 0);
        });
        r.Method(ctor, "getOwnPropertyDescriptor", 2, (t, a, nt) =>
        {
            JsObject o = Ops.ToObject(A(a, 0));
            return o.GetOwnProperty(Ops.ToPropertyKey(A(a, 1)), out PropertyDescriptor d) ? FromPropertyDescriptor(r, d) : default;
        });
        r.Method(ctor, "getOwnPropertyDescriptors", 1, (t, a, nt) =>
        {
            JsObject o = Ops.ToObject(A(a, 0));
            var res = r.NewObject();
            var keys = new List<PropertyKey>();
            o.OwnKeys(keys);
            foreach (PropertyKey k in keys)
            {
                if (o.GetOwnProperty(k, out PropertyDescriptor d))
                {
                    res.CreateDataProperty(k, FromPropertyDescriptor(r, d));
                }
            }

            return JsValue.FromObject(res);
        });
        r.Method(ctor, "getOwnPropertyNames", 1, (t, a, nt) => KeysArray(r, Ops.ToObject(A(a, 0)), false, true, 0));
        r.Method(ctor, "getOwnPropertySymbols", 1, (t, a, nt) => KeysArray(r, Ops.ToObject(A(a, 0)), true, false, 0));
        r.Method(ctor, "keys", 1, (t, a, nt) => KeysArray(r, Ops.ToObject(A(a, 0)), false, true, 1, true));
        r.Method(ctor, "values", 1, (t, a, nt) => KeysArray(r, Ops.ToObject(A(a, 0)), false, true, 2, true));
        r.Method(ctor, "entries", 1, (t, a, nt) => KeysArray(r, Ops.ToObject(A(a, 0)), false, true, 3, true));
        r.Method(ctor, "assign", 2, (t, a, nt) =>
        {
            JsObject to = Ops.ToObject(A(a, 0));
            for (int i = 1; i < a.Length; i++)
            {
                if (a[i].IsNullish)
                {
                    continue;
                }

                JsObject from = Ops.ToObject(a[i]);
                var keys = new List<PropertyKey>();
                from.OwnKeys(keys);
                foreach (PropertyKey k in keys)
                {
                    if (from.GetOwnProperty(k, out PropertyDescriptor d) && d.Enumerable)
                    {
                        to.SetOrThrow(k, from.Get(k));
                    }
                }
            }

            return JsValue.FromObject(to);
        });
        r.Method(ctor, "is", 2, (t, a, nt) => Ops.Bool(Ops.SameValue(A(a, 0), A(a, 1))));
        r.Method(ctor, "preventExtensions", 1, (t, a, nt) =>
        {
            if (A(a, 0).Ref is JsObject o && !o.PreventExtensions())
            {
                throw o is ProxyObject ? ProxyObject.Falsish("preventExtensions", null) : Errors.Type("Cannot prevent extensions");
            }

            return A(a, 0);
        });
        r.Method(ctor, "isExtensible", 1, (t, a, nt) => Ops.Bool(A(a, 0).Ref is JsObject o && o.IsExtensibleOp()));
        r.Method(ctor, "freeze", 1, (t, a, nt) => SetIntegrity(A(a, 0), true));
        r.Method(ctor, "seal", 1, (t, a, nt) => SetIntegrity(A(a, 0), false));
        r.Method(ctor, "isFrozen", 1, (t, a, nt) => Ops.Bool(TestIntegrity(A(a, 0), true)));
        r.Method(ctor, "isSealed", 1, (t, a, nt) => Ops.Bool(TestIntegrity(A(a, 0), false)));
        r.Method(ctor, "fromEntries", 1, (t, a, nt) =>
        {
            Ops.RequireObjectCoercible(A(a, 0));
            var o = r.NewObject();
            IteratorRecord rec = Ops.GetIterator(A(a, 0));
            while (Ops.IteratorStepValue(rec, out JsValue e))
            {
                try
                {
                    if (e.Ref is not JsObject eo)
                    {
                        throw Errors.Type("Iterator value " + Ops.DisplayValue(e) + " is not an entry object");
                    }

                    o.CreateDataPropertyOrThrow(Ops.ToPropertyKey(eo.Get("0")), eo.Get("1"));
                }
                catch (JsException)
                {
                    Ops.IteratorCloseSilently(rec);
                    throw;
                }
            }

            return JsValue.FromObject(o);
        });

        r.Method(proto, "hasOwnProperty", 1, (t, a, nt) =>
        {
            PropertyKey k = Ops.ToPropertyKey(A(a, 0));
            return Ops.Bool(Ops.ToObject(t).HasOwnProperty(k));
        });
        r.Method(proto, "isPrototypeOf", 1, (t, a, nt) =>
        {
            if (A(a, 0).Ref is not JsObject v)
            {
                return JsValue.False;
            }

            JsObject o = Ops.ToObject(t);
            for (JsObject? p = v.GetPrototypeOf(); p is not null; p = p.GetPrototypeOf())
            {
                if (ReferenceEquals(p, o))
                {
                    return JsValue.True;
                }
            }

            return JsValue.False;
        });
        r.Method(proto, "propertyIsEnumerable", 1, (t, a, nt) =>
        {
            PropertyKey k = Ops.ToPropertyKey(A(a, 0));
            return Ops.Bool(Ops.ToObject(t).GetOwnProperty(k, out PropertyDescriptor d) && d.Enumerable);
        });
        r.Method(proto, "valueOf", 0, (t, a, nt) => JsValue.FromObject(Ops.ToObject(t)));
        r.Method(proto, "toLocaleString", 0, (t, a, nt) => Ops.Invoke(t, "toString", default));
        r.Method(proto, "toString", 0, (t, a, nt) => Ops.Str(ObjectToString(t)));
        r.Getter(proto, "__proto__", "__proto__", (t, a, nt) => { JsObject? p = Ops.ToObject(t).GetPrototypeOf(); return p is null ? JsValue.Null : JsValue.FromObject(p); }, (t, a, nt) =>
        {
            Ops.RequireObjectCoercible(t);
            JsValue p = A(a, 0);
            if ((p.IsObject || p.IsNull) && t.Ref is JsObject o && !o.SetPrototypeOf(p.AsObject))
            {
                throw Errors.Type("Cyclic __proto__ value");
            }

            return default;
        });
        r.Method(proto, "__defineGetter__", 2, (t, a, nt) =>
        {
            JsObject o = Ops.ToObject(t);
            if (!Ops.IsCallable(A(a, 1)))
            {
                throw Errors.Type("Object.prototype.__defineGetter__: Expecting function");
            }

            o.DefinePropertyOrThrow(Ops.ToPropertyKey(A(a, 0)), new PropertyDescriptor { Get = A(a, 1), HasGet = true, Enumerable = true, HasEnumerable = true, Configurable = true, HasConfigurable = true });
            return default;
        });
        r.Method(proto, "__defineSetter__", 2, (t, a, nt) =>
        {
            JsObject o = Ops.ToObject(t);
            if (!Ops.IsCallable(A(a, 1)))
            {
                throw Errors.Type("Object.prototype.__defineSetter__: Expecting function");
            }

            o.DefinePropertyOrThrow(Ops.ToPropertyKey(A(a, 0)), new PropertyDescriptor { Set = A(a, 1), HasSet = true, Enumerable = true, HasEnumerable = true, Configurable = true, HasConfigurable = true });
            return default;
        });
        r.Method(proto, "__lookupGetter__", 1, (t, a, nt) => LookupAccessor(t, A(a, 0), true));
        r.Method(proto, "__lookupSetter__", 1, (t, a, nt) => LookupAccessor(t, A(a, 0), false));
    }

    private static JsValue LookupAccessor(JsValue t, JsValue key, bool getter)
    {
        JsObject o = Ops.ToObject(t);
        PropertyKey k = Ops.ToPropertyKey(key);
        for (JsObject? p = o; p is not null; p = p.GetPrototypeOf())
        {
            if (p.GetOwnProperty(k, out PropertyDescriptor d))
            {
                return d.IsAccessor ? (getter ? d.Get : d.Set) : default;
            }
        }

        return default;
    }

    internal static string ObjectToString(JsValue t)
    {
        if (t.IsUndefined)
        {
            return "[object Undefined]";
        }

        if (t.IsNull)
        {
            return "[object Null]";
        }

        JsObject o = Ops.ToObject(t);
        if (o.ProxyTarget(out _, out bool revoked) && revoked)
        {
            throw Errors.Type("Cannot perform 'Object.prototype.toString' on a proxy that has been revoked");
        }

        string tag = Ops.IsArray(JsValue.FromObject(o)) ? "Array" : o switch
        {
            JsArguments => "Arguments",
            JsFunction => "Function",
            JsError => "Error",
            JsPrimitiveObject p when p.ClassName is "Boolean" or "Number" => p.ClassName,
            JsStringObject => "String",
            _ => o.ClassName is "Date" or "RegExp" ? o.ClassName : o.IsCallable ? "Function" : "Object",
        };
        JsValue tt = o.Get(new PropertyKey(WellKnown.ToStringTag));
        if (tt.IsString)
        {
            tag = tt.AsString;
        }

        return "[object " + tag + "]";
    }

    private static void DefineProperties(JsObject o, JsValue props)
    {
        JsObject p = Ops.ToObject(props);
        var keys = new List<PropertyKey>();
        p.OwnKeys(keys);
        var descs = new List<(PropertyKey, PropertyDescriptor)>();
        foreach (PropertyKey k in keys)
        {
            if (p.GetOwnProperty(k, out PropertyDescriptor pd) && pd.Enumerable)
            {
                descs.Add((k, ToPropertyDescriptor(p.Get(k))));
            }
        }

        foreach ((PropertyKey k, PropertyDescriptor d) in descs)
        {
            o.DefinePropertyOrThrow(k, d);
        }
    }

    /// <summary>EnumerableOwnProperties (7.3.23) and own key listing. kind: 0 names/symbols, 1 keys, 2 values, 3 entries.</summary>
    private static JsValue KeysArray(Realm r, JsObject o, bool symbols, bool strings, int kind, bool enumerableOnly = false)
    {
        var keys = new List<PropertyKey>();
        o.OwnKeys(keys);
        var res = new JsArray(r.ArrayPrototype);
        foreach (PropertyKey k in keys)
        {
            if (k.IsSymbol ? !symbols : !strings)
            {
                continue;
            }

            if (enumerableOnly && !(o.GetOwnProperty(k, out PropertyDescriptor d) && d.Enumerable))
            {
                continue;
            }

            switch (kind)
            {
                case 2:
                    res.Push(o.Get(k));
                    break;
                case 3:
                    res.Push(JsValue.FromObject(Ops.CreateArray([k.ToValue(), o.Get(k)])));
                    break;
                default:
                    res.Push(k.ToValue());
                    break;
            }
        }

        return JsValue.FromObject(res);
    }

    private static JsValue SetIntegrity(JsValue v, bool frozen)
    {
        if (v.Ref is not JsObject o)
        {
            return v;
        }

        if (!o.PreventExtensions())
        {
            throw o is ProxyObject ? ProxyObject.Falsish("preventExtensions", null) : Errors.Type("Cannot prevent extensions");
        }

        var keys = new List<PropertyKey>();
        o.OwnKeys(keys);
        foreach (PropertyKey k in keys)
        {
            PropertyDescriptor nd = new() { HasConfigurable = true, Configurable = false };
            if (frozen && o.GetOwnProperty(k, out PropertyDescriptor cur) && !cur.IsAccessor)
            {
                nd.HasWritable = true;
                nd.Writable = false;
            }
            else if (frozen && !o.HasOwnProperty(k))
            {
                continue;
            }

            o.DefinePropertyOrThrow(k, nd);
        }

        return v;
    }

    private static bool TestIntegrity(JsValue v, bool frozen)
    {
        if (v.Ref is not JsObject o)
        {
            return true;
        }

        if (o.IsExtensibleOp())
        {
            return false;
        }

        var keys = new List<PropertyKey>();
        o.OwnKeys(keys);
        foreach (PropertyKey k in keys)
        {
            if (o.GetOwnProperty(k, out PropertyDescriptor d) && (d.Configurable || (frozen && d.IsData && d.Writable)))
            {
                return false;
            }
        }

        return true;
    }

    // ---- Function ------------------------------------------------------------------------------------------------

    private static void SetupFunction(Realm r)
    {
        JsObject proto = r.FunctionPrototype;
        NativeFunction ctor = r.Ctor("Function", 1, (t, a, nt) => DynamicFunction(r, a, nt, "function"), proto);
        r.Method(proto, "call", 1, (t, a, nt) =>
        {
            if (t.Ref is not JsObject f || !f.IsCallable)
            {
                throw Errors.Type("Function.prototype.call called on non-function");
            }

            return f.Call(A(a, 0), a.Length > 1 ? a[1..] : default);
        });
        r.Method(proto, "apply", 2, (t, a, nt) =>
        {
            if (t.Ref is not JsObject f || !f.IsCallable)
            {
                throw Errors.Type("Function.prototype.apply was called on " + Ops.DisplayValue(t) + ", which is " + t.TypeOf + " and not a function");
            }

            JsValue arr = A(a, 1);
            return f.Call(A(a, 0), arr.IsNullish ? default : Ops.ListFromArrayLike(arr));
        });
        r.Method(proto, "bind", 1, (t, a, nt) =>
        {
            if (t.Ref is not JsObject target || !target.IsCallable)
            {
                throw Errors.Type("Bind must be called on a function");
            }

            var bound = new BoundFunction(r, target, A(a, 0), a.Length > 1 ? a[1..].ToArray() : [], target.GetPrototypeOf());
            double len = 0;
            if (target.HasOwnProperty("length"))
            {
                JsValue lv = target.Get("length");
                if (lv.IsNumber)
                {
                    len = double.IsPositiveInfinity(lv.Num) ? double.PositiveInfinity : double.IsNegativeInfinity(lv.Num) ? 0 : Math.Max(0, Math.Truncate(lv.Num) - Math.Max(0, a.Length - 1));
                    if (double.IsNaN(lv.Num))
                    {
                        len = 0;
                    }
                }
            }

            bound.DefineDirect("length", JsValue.FromNumber(len), PropFlags.Configurable);
            JsValue nv = target.Get("name");
            bound.DefineDirect("name", Ops.Str("bound " + (nv.IsString ? nv.AsString : string.Empty)), PropFlags.Configurable);
            return JsValue.FromObject(bound);
        });
        r.Method(proto, "toString", 0, (t, a, nt) =>
        {
            if (t.Ref is ScriptFunction sf)
            {
                return Ops.Str(sf.Code.SourceText);
            }

            if (t.Ref is JsObject o && o.IsCallable)
            {
                string name = o.GetOwnProperty("name", out PropertyDescriptor d) && d.Value.IsString ? d.Value.AsString : string.Empty;
                return Ops.Str("function " + name + "() { [native code] }");
            }

            throw Errors.Type("Function.prototype.toString requires that 'this' be a Function");
        });
        r.Method(proto, WellKnown.HasInstance, "[Symbol.hasInstance]", 1, (t, a, nt) => Ops.Bool(Ops.OrdinaryHasInstance(t, A(a, 0))), PropFlags.None);
        r.ThrowTypeError = new NativeFunction(r, string.Empty, 0, (t, a, nt) => throw Errors.Type("'caller', 'callee', and 'arguments' properties may not be accessed on strict mode functions or the arguments objects for calls to them"));
        r.ThrowTypeError.DefineDirect("length", JsValue.FromNumber(0), PropFlags.None);
        r.ThrowTypeError.DefineDirect("name", Ops.Str(string.Empty), PropFlags.None);
        r.ThrowTypeError.PreventExtensions();
        proto.DefineAccessorDirect("caller", r.ThrowTypeError, r.ThrowTypeError, PropFlags.Configurable);
        proto.DefineAccessorDirect("arguments", r.ThrowTypeError, r.ThrowTypeError, PropFlags.Configurable);
        _ = ctor;
    }

    /// <summary>CreateDynamicFunction (20.2.1.1.1) for Function, GeneratorFunction, AsyncFunction and AsyncGeneratorFunction.</summary>
    internal static JsValue DynamicFunction(Realm r, ReadOnlySpan<JsValue> a, JsObject? newTarget, string kind)
    {
        var ps = new StringBuilder();
        for (int i = 0; i < a.Length - 1; i++)
        {
            if (i > 0)
            {
                ps.Append(',');
            }

            ps.Append(Ops.ToStr(a[i]));
        }

        string body = a.Length > 0 ? Ops.ToStr(a[^1]) : string.Empty;
        string prefix = kind switch { "generator" => "function*", "async" => "async function", "asyncGenerator" => "async function*", _ => "function" };
        string src = prefix + " anonymous(" + ps + "\n) {\n" + body + "\n}";
        // The parameters and the body must each be valid on their own (no "}) , (function(){" tricks).
        string check1 = "(" + prefix + " (" + ps + "\n) {\n})";
        string check2 = "(" + prefix + " () {\n" + body + "\n})";
        foreach (string c in new[] { check1, check2 })
        {
            Syntax.ParseResult cr = Syntax.JsParser.Parse(c, new Syntax.ParseOptions { FileName = "<anonymous>" });
            if (cr.HasErrors)
            {
                throw Errors.Syntax(cr.Diagnostics[0].Headline);
            }
        }

        Syntax.ParseResult pr = Syntax.JsParser.Parse("(" + src + ")", new Syntax.ParseOptions { FileName = "<anonymous>" });
        if (pr.HasErrors)
        {
            throw Errors.Syntax(pr.Diagnostics[0].Headline);
        }

        FunctionCode code = Compiler.CompileScript(pr.Program, pr.Source);
        var frame = new Frame(code, r) { Env = null, This = JsValue.FromObject(r.Global), Func = null };
        JsValue fv = Vm.RunEntry(r.Agent, frame);
        if (fv.Ref is ScriptFunction sf)
        {
            // The text of a dynamic function starts at "function", not at the wrapping parenthesis.
            sf.Code.SrcStart += 1;
            sf.Code.SrcEnd = sf.Code.SrcStart + src.Length;
            if (newTarget is not null && !ReferenceEquals(newTarget, r.Intrinsics.GetValueOrDefault("%" + (kind == "function" ? "Function" : kind) + "%")))
            {
                JsObject proto = Ops.GetPrototypeFromConstructor(newTarget, rr => kind switch
                {
                    "generator" => rr.GeneratorFunctionPrototype,
                    "async" => rr.AsyncFunctionPrototype,
                    "asyncGenerator" => rr.AsyncGeneratorFunctionPrototype,
                    _ => rr.FunctionPrototype,
                });
                sf.SetPrototypeOf(proto);
            }
        }

        return fv;
    }

    // ---- Error ------------------------------------------------------------------------------------------------------

    private static void SetupErrors(Realm r)
    {
        JsObject proto = r.NewObject();
        r.ErrorPrototype = proto;
        r.ErrorPrototypes["Error"] = proto;
        NativeFunction errorCtor = MakeErrorCtor(r, "Error", proto, null);
        Realm.Value(proto, "name", Ops.Str("Error"));
        Realm.Value(proto, "message", Ops.Str(string.Empty));
        r.Method(proto, "toString", 0, (t, a, nt) =>
        {
            if (t.Ref is not JsObject o)
            {
                throw Errors.Type("Error.prototype.toString requires that 'this' be an Object");
            }

            JsValue nv = o.Get("name");
            string name = nv.IsUndefined ? "Error" : Ops.ToStr(nv);
            JsValue mv = o.Get("message");
            string msg = mv.IsUndefined ? string.Empty : Ops.ToStr(mv);
            return Ops.Str(name.Length == 0 ? msg : msg.Length == 0 ? name : name + ": " + msg);
        });
        Realm.Value(errorCtor, "stackTraceLimit", JsValue.FromNumber(10), PropFlags.Default);
        foreach (string n in new[] { "TypeError", "RangeError", "ReferenceError", "SyntaxError", "EvalError", "URIError" })
        {
            JsObject p = new(proto);
            r.ErrorPrototypes[n] = p;
            MakeErrorCtor(r, n, p, errorCtor);
            Realm.Value(p, "name", Ops.Str(n));
            Realm.Value(p, "message", Ops.Str(string.Empty));
        }

        JsObject ap = new(proto);
        r.ErrorPrototypes["AggregateError"] = ap;
        NativeFunction agg = new(r, "AggregateError", 2, (t, a, nt) =>
        {
            JsObject newTarget = nt ?? (JsObject)r.Intrinsics["%AggregateError%"];
            JsObject p = Ops.GetPrototypeFromConstructor(newTarget, rr => rr.ErrorPrototypes["AggregateError"]);
            var e = new JsError(p);
            InitErrorWithOptions(r, e, A(a, 1), A(a, 2));
            List<JsValue> errs = Ops.IterableToList(A(a, 0), Ops.GetMethod(A(a, 0), new PropertyKey(WellKnown.Iterator)).IsUndefined ? throw Errors.Type(Ops.DisplayValue(A(a, 0)) + " is not iterable") : Ops.GetMethod(A(a, 0), new PropertyKey(WellKnown.Iterator)));
            e.DefineDirect("errors", JsValue.FromObject(Ops.CreateArray(errs)), PropFlags.Hidden);
            return JsValue.FromObject(e);
        }, true, errorCtor);
        agg.DefineDirect("prototype", JsValue.FromObject(ap), PropFlags.None);
        ap.DefineDirect("constructor", JsValue.FromObject(agg), PropFlags.Hidden);
        r.Global.DefineDirect("AggregateError", JsValue.FromObject(agg), PropFlags.Hidden);
        r.Intrinsics["%AggregateError%"] = agg;
        Realm.Value(ap, "name", Ops.Str("AggregateError"));
        Realm.Value(ap, "message", Ops.Str(string.Empty));
    }

    private static void InitErrorWithOptions(Realm r, JsError e, JsValue message, JsValue options)
    {
        string? msg = message.IsUndefined ? null : Ops.ToStr(message);
        r.InitError(e, msg);
        if (options.Ref is JsObject oo && oo.HasProperty("cause"))
        {
            e.DefineDirect("cause", oo.Get("cause"), PropFlags.Hidden);
        }
    }

    private static NativeFunction MakeErrorCtor(Realm r, string name, JsObject proto, JsObject? ctorProto)
    {
        NativeFunction f = null!;
        f = r.Ctor(name, 1, (t, a, nt) =>
        {
            JsObject newTarget = nt ?? f;
            JsObject p = Ops.GetPrototypeFromConstructor(newTarget, rr => rr.ErrorPrototypes[name]);
            var e = new JsError(p);
            InitErrorWithOptions(r, e, A(a, 0), A(a, 1));
            return JsValue.FromObject(e);
        }, proto, ctorProto);
        return f;
    }

    // ---- Symbol -------------------------------------------------------------------------------------------------------

    private static void SetupSymbol(Realm r)
    {
        JsObject proto = r.NewObject();
        r.SymbolPrototype = proto;
        NativeFunction ctor = r.Ctor("Symbol", 0, (t, a, nt) =>
        {
            if (nt is not null)
            {
                throw Errors.Type("Symbol is not a constructor");
            }

            return JsValue.FromSymbol(new JsSymbol(A(a, 0).IsUndefined ? null : Ops.ToStr(A(a, 0))));
        }, proto);
        foreach ((string n, JsSymbol s) in new[]
        {
            ("asyncIterator", WellKnown.AsyncIterator), ("hasInstance", WellKnown.HasInstance), ("isConcatSpreadable", WellKnown.IsConcatSpreadable),
            ("iterator", WellKnown.Iterator), ("match", WellKnown.Match), ("matchAll", WellKnown.MatchAll), ("replace", WellKnown.Replace),
            ("search", WellKnown.Search), ("species", WellKnown.Species), ("split", WellKnown.Split), ("toPrimitive", WellKnown.ToPrimitive),
            ("toStringTag", WellKnown.ToStringTag), ("unscopables", WellKnown.Unscopables),
        })
        {
            Realm.Value(ctor, n, JsValue.FromSymbol(s), PropFlags.None);
        }

        r.Method(ctor, "for", 1, (t, a, nt) =>
        {
            string key = Ops.ToStr(A(a, 0));
            Agent ag = Agent.Current;
            if (!ag.SymbolRegistry.TryGetValue(key, out JsSymbol? s))
            {
                s = new JsSymbol(key) { RegistryKey = key };
                ag.SymbolRegistry[key] = s;
            }

            return JsValue.FromSymbol(s);
        });
        r.Method(ctor, "keyFor", 1, (t, a, nt) =>
        {
            if (A(a, 0).Ref is not JsSymbol s)
            {
                throw Errors.Type(Ops.DisplayValue(A(a, 0)) + " is not a symbol");
            }

            return s.RegistryKey is null ? default : Ops.Str(s.RegistryKey);
        });
        JsSymbol ThisSym(JsValue t) => t.Ref as JsSymbol ?? (t.Ref as JsPrimitiveObject)?.Value.Ref as JsSymbol ?? throw Errors.Type("Symbol.prototype method called on incompatible receiver " + Ops.DisplayValue(t));
        r.Method(proto, "toString", 0, (t, a, nt) => Ops.Str(ThisSym(t).ToString()));
        r.Method(proto, "valueOf", 0, (t, a, nt) => JsValue.FromSymbol(ThisSym(t)));
        r.Getter(proto, "description", "description", (t, a, nt) => ThisSym(t).Description is { } d ? Ops.Str(d) : default);
        r.Method(proto, WellKnown.ToPrimitive, "[Symbol.toPrimitive]", 1, (t, a, nt) => JsValue.FromSymbol(ThisSym(t)), PropFlags.Configurable);
        proto.DefineDirect(new PropertyKey(WellKnown.ToStringTag), Ops.Str("Symbol"), PropFlags.Configurable);
    }

    // ---- Boolean, Number, BigInt ----------------------------------------------------------------------------------------

    private static void SetupNumberBooleanBigInt(Realm r)
    {
        var bproto = new JsPrimitiveObject(r.ObjectPrototype, JsValue.False, "Boolean");
        r.BooleanPrototype = bproto;
        r.Ctor("Boolean", 1, (t, a, nt) =>
        {
            bool b = Ops.ToBoolean(A(a, 0));
            if (nt is null)
            {
                return Ops.Bool(b);
            }

            return JsValue.FromObject(new JsPrimitiveObject(Ops.GetPrototypeFromConstructor(nt, x => x.BooleanPrototype), Ops.Bool(b), "Boolean"));
        }, bproto);
        bool ThisBool(JsValue t) => t.IsBoolean ? t.IsTrue : t.Ref is JsPrimitiveObject { ClassName: "Boolean" } p ? p.Value.IsTrue : throw Errors.Type("Boolean.prototype.valueOf requires that 'this' be a Boolean");
        r.Method(bproto, "toString", 0, (t, a, nt) => Ops.Str(ThisBool(t) ? "true" : "false"));
        r.Method(bproto, "valueOf", 0, (t, a, nt) => Ops.Bool(ThisBool(t)));

        var nproto = new JsPrimitiveObject(r.ObjectPrototype, JsValue.FromNumber(0), "Number");
        r.NumberPrototype = nproto;
        NativeFunction nctor = r.Ctor("Number", 1, (t, a, nt) =>
        {
            double n = 0;
            if (a.Length > 0)
            {
                JsValue p = Ops.ToNumeric(a[0]);
                n = p.IsBigInt ? (double)(BigInteger)p.Ref! : p.Num;
            }

            if (nt is null)
            {
                return Ops.Num(n);
            }

            return JsValue.FromObject(new JsPrimitiveObject(Ops.GetPrototypeFromConstructor(nt, x => x.NumberPrototype), Ops.Num(n), "Number"));
        }, nproto);
        double ThisNum(JsValue t) => t.IsNumber ? t.Num : t.Ref is JsPrimitiveObject { ClassName: "Number" } p ? p.Value.Num : throw Errors.Type("Number.prototype.valueOf requires that 'this' be a Number");
        r.Method(nproto, "valueOf", 0, (t, a, nt) => Ops.Num(ThisNum(t)));
        r.Method(nproto, "toString", 1, (t, a, nt) =>
        {
            double n = ThisNum(t);
            double radix = A(a, 0).IsUndefined ? 10 : Ops.ToIntegerOrInfinity(A(a, 0));
            if (radix < 2 || radix > 36)
            {
                throw Errors.Range("toString() radix must be between 2 and 36");
            }

            return Ops.Str(radix == 10 ? Ops.NumberToStr(n) : NumberConv.ToRadixString(n, (int)radix));
        });
        Realm.Value(nctor, "MAX_SAFE_INTEGER", Ops.Num(9007199254740991d), PropFlags.None);
        Realm.Value(nctor, "MIN_SAFE_INTEGER", Ops.Num(-9007199254740991d), PropFlags.None);
        Realm.Value(nctor, "MAX_VALUE", Ops.Num(double.MaxValue), PropFlags.None);
        Realm.Value(nctor, "MIN_VALUE", Ops.Num(double.Epsilon), PropFlags.None);
        Realm.Value(nctor, "EPSILON", Ops.Num(Math.Pow(2, -52)), PropFlags.None);
        Realm.Value(nctor, "NaN", Ops.Num(double.NaN), PropFlags.None);
        Realm.Value(nctor, "POSITIVE_INFINITY", Ops.Num(double.PositiveInfinity), PropFlags.None);
        Realm.Value(nctor, "NEGATIVE_INFINITY", Ops.Num(double.NegativeInfinity), PropFlags.None);
        r.Method(nctor, "isNaN", 1, (t, a, nt) => Ops.Bool(A(a, 0).IsNumber && double.IsNaN(A(a, 0).Num)));
        r.Method(nctor, "isFinite", 1, (t, a, nt) => Ops.Bool(A(a, 0).IsNumber && double.IsFinite(A(a, 0).Num)));
        r.Method(nctor, "isInteger", 1, (t, a, nt) => Ops.Bool(A(a, 0).IsNumber && double.IsFinite(A(a, 0).Num) && Math.Floor(A(a, 0).Num) == A(a, 0).Num));
        r.Method(nctor, "isSafeInteger", 1, (t, a, nt) => Ops.Bool(A(a, 0).IsNumber && double.IsFinite(A(a, 0).Num) && Math.Floor(A(a, 0).Num) == A(a, 0).Num && Math.Abs(A(a, 0).Num) <= 9007199254740991d));

        var biproto = r.NewObject();
        r.BigIntPrototype = biproto;
        r.Ctor("BigInt", 1, (t, a, nt) =>
        {
            if (nt is not null)
            {
                throw Errors.Type("BigInt is not a constructor");
            }

            JsValue p = Ops.ToPrimitive(A(a, 0), "number");
            if (p.IsNumber)
            {
                if (!double.IsFinite(p.Num) || Math.Floor(p.Num) != p.Num)
                {
                    throw Errors.Range("The number " + Ops.NumberToStr(p.Num) + " cannot be converted to a BigInt because it is not an integer");
                }

                return JsValue.FromBigInt(new BigInteger(p.Num));
            }

            return JsValue.FromBigInt(ToBigInt(p));
        }, biproto);
        BigInteger ThisBig(JsValue t) => t.Ref is BigInteger b ? b : t.Ref is JsPrimitiveObject { ClassName: "BigInt" } p ? (BigInteger)p.Value.Ref! : throw Errors.Type("BigInt.prototype.valueOf requires that 'this' be a BigInt");
        r.Method(biproto, "valueOf", 0, (t, a, nt) => JsValue.FromBigInt(ThisBig(t)));
        r.Method(biproto, "toString", 0, (t, a, nt) =>
        {
            BigInteger b = ThisBig(t);
            double radix = A(a, 0).IsUndefined ? 10 : Ops.ToIntegerOrInfinity(A(a, 0));
            if (radix < 2 || radix > 36)
            {
                throw Errors.Range("toString() radix must be between 2 and 36");
            }

            if (radix == 10)
            {
                return Ops.Str(b.ToString(System.Globalization.NumberFormatInfo.InvariantInfo));
            }

            var sb = new StringBuilder();
            BigInteger v = BigInteger.Abs(b);
            if (v.IsZero)
            {
                sb.Append('0');
            }

            while (v > 0)
            {
                sb.Insert(0, "0123456789abcdefghijklmnopqrstuvwxyz"[(int)(v % (int)radix)]);
                v /= (int)radix;
            }

            return Ops.Str((b.Sign < 0 ? "-" : string.Empty) + sb);
        });
        biproto.DefineDirect(new PropertyKey(WellKnown.ToStringTag), Ops.Str("BigInt"), PropFlags.Configurable);
    }

    internal static BigInteger ToBigInt(JsValue v)
    {
        JsValue p = Ops.ToPrimitive(v, "number");
        switch (p.Ref)
        {
            case BigInteger b:
                return b;
            case string or Rope:
                if (NumberConv.TryStringToBigInt(p.AsString, out BigInteger r))
                {
                    return r;
                }

                throw Errors.Syntax("Cannot convert " + p.AsString + " to a BigInt");
            case JsSymbol:
                throw Errors.Type("Cannot convert a Symbol value to a bigint");
            default:
                if (p.IsBoolean)
                {
                    return p.IsTrue ? BigInteger.One : BigInteger.Zero;
                }

                throw Errors.Type("Cannot convert " + Ops.DisplayValue(p) + " to a BigInt");
        }
    }
}
