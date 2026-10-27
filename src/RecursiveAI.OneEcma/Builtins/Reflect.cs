namespace RecursiveAI.OneEcma;

internal static partial class Builtins
{
    private static JsObject ReflectTarget(JsValue v, string method) =>
        v.Ref as JsObject ?? throw Errors.Type("Reflect." + method + " called on non-object");

    private static void SetupReflect(Realm r)
    {
        // 28.1 The Reflect Object: an ordinary object (not a function) with a @@toStringTag.
        var reflect = r.NewObject();
        r.Global.DefineDirect("Reflect", JsValue.FromObject(reflect), PropFlags.Hidden);
        r.Intrinsics["%Reflect%"] = reflect;
        reflect.DefineDirect(new PropertyKey(WellKnown.ToStringTag), Ops.Str("Reflect"), PropFlags.Configurable);

        r.Method(reflect, "apply", 3, (t, a, nt) =>
        {
            JsValue f = A(a, 0);
            if (!Ops.IsCallable(f))
            {
                throw Errors.Type("Function.prototype.apply was called on " + Ops.DisplayValue(f) + ", which is " + TypeNoun(f) + " and not a function");
            }

            JsValue[] args = Ops.ListFromArrayLike(A(a, 2));
            return ((JsObject)f.Ref!).Call(A(a, 1), args);
        });

        r.Method(reflect, "construct", 2, (t, a, nt) =>
        {
            if (!Ops.IsConstructor(A(a, 0)))
            {
                throw Errors.Type(Ops.DisplayValue(A(a, 0)) + " is not a constructor");
            }

            var target = (JsObject)A(a, 0).Ref!;
            JsObject newTarget = target;
            if (a.Length > 2)
            {
                if (!Ops.IsConstructor(a[2]))
                {
                    throw Errors.Type(Ops.DisplayValue(a[2]) + " is not a constructor");
                }

                newTarget = (JsObject)a[2].Ref!;
            }

            JsValue[] args = Ops.ListFromArrayLike(A(a, 1));
            return target.Construct(args, newTarget);
        });

        r.Method(reflect, "defineProperty", 3, (t, a, nt) =>
        {
            JsObject o = ReflectTarget(A(a, 0), "defineProperty");
            PropertyKey key = Ops.ToPropertyKey(A(a, 1));
            PropertyDescriptor d = ToPropertyDescriptor(A(a, 2));
            return Ops.Bool(o.DefineOwnProperty(key, d));
        });

        r.Method(reflect, "deleteProperty", 2, (t, a, nt) =>
        {
            JsObject o = ReflectTarget(A(a, 0), "deleteProperty");
            return Ops.Bool(o.Delete(Ops.ToPropertyKey(A(a, 1))));
        });

        r.Method(reflect, "get", 2, (t, a, nt) =>
        {
            JsObject o = ReflectTarget(A(a, 0), "get");
            PropertyKey key = Ops.ToPropertyKey(A(a, 1));
            return o.Get(key, a.Length > 2 ? a[2] : A(a, 0));
        });

        r.Method(reflect, "getOwnPropertyDescriptor", 2, (t, a, nt) =>
        {
            JsObject o = ReflectTarget(A(a, 0), "getOwnPropertyDescriptor");
            PropertyKey key = Ops.ToPropertyKey(A(a, 1));
            return o.GetOwnProperty(key, out PropertyDescriptor d) ? FromPropertyDescriptor(r, d) : default;
        });

        r.Method(reflect, "getPrototypeOf", 1, (t, a, nt) =>
        {
            JsObject? p = ReflectTarget(A(a, 0), "getPrototypeOf").GetPrototypeOf();
            return p is null ? JsValue.Null : JsValue.FromObject(p);
        });

        r.Method(reflect, "has", 2, (t, a, nt) =>
        {
            JsObject o = ReflectTarget(A(a, 0), "has");
            return Ops.Bool(o.HasProperty(Ops.ToPropertyKey(A(a, 1))));
        });

        r.Method(reflect, "isExtensible", 1, (t, a, nt) => Ops.Bool(ReflectTarget(A(a, 0), "isExtensible").IsExtensibleOp()));

        r.Method(reflect, "ownKeys", 1, (t, a, nt) =>
        {
            JsObject o = ReflectTarget(A(a, 0), "ownKeys");
            var keys = new List<PropertyKey>();
            o.OwnKeys(keys);
            var values = new List<JsValue>(keys.Count);
            foreach (PropertyKey k in keys)
            {
                values.Add(k.ToValue());
            }

            return JsValue.FromObject(Ops.CreateArray(values));
        });

        r.Method(reflect, "preventExtensions", 1, (t, a, nt) => Ops.Bool(ReflectTarget(A(a, 0), "preventExtensions").PreventExtensions()));

        r.Method(reflect, "set", 3, (t, a, nt) =>
        {
            JsObject o = ReflectTarget(A(a, 0), "set");
            PropertyKey key = Ops.ToPropertyKey(A(a, 1));
            return Ops.Bool(o.Set(key, A(a, 2), a.Length > 3 ? a[3] : A(a, 0)));
        });

        r.Method(reflect, "setPrototypeOf", 2, (t, a, nt) =>
        {
            JsObject o = ReflectTarget(A(a, 0), "setPrototypeOf");
            JsValue p = A(a, 1);
            if (!p.IsObject && !p.IsNull)
            {
                throw Errors.Type("Object prototype may only be an Object or null: " + Ops.DisplayValue(p));
            }

            return Ops.Bool(o.SetPrototypeOf(p.AsObject));
        });
    }

    /// <summary>The "a number" / "an object" part of V8's message for applying a non-function.</summary>
    private static string TypeNoun(JsValue v) => v.TypeOf switch
    {
        "undefined" => "undefined",
        "object" when v.IsNull => "null",
        "object" => "an object",
        string s => "a " + s,
    };
}
