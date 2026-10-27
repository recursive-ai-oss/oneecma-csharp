using System.Text;

namespace RecursiveAI.OneEcma;

/// <summary>Object.hasOwn and Object.groupBy, Error.isError, and Promise.try.</summary>
internal static partial class Builtins
{
    private static void SetupCoreExtras(Realm r)
    {
        JsObject objectCtor = r.Intrinsics["%Object%"];

        // ECMA-262 20.1.2.13 Object.hasOwn ( O, P )
        r.Method(objectCtor, "hasOwn", 2, (t, a, nt) =>
        {
            JsObject o = Ops.ToObject(A(a, 0));
            PropertyKey key = Ops.ToPropertyKey(A(a, 1));
            return Ops.Bool(o.HasOwnProperty(key));
        });

        // ECMA-262 20.1.2.11 Object.groupBy ( items, callbackfn ) and GroupBy (7.3.35)
        r.Method(objectCtor, "groupBy", 2, (t, a, nt) =>
        {
            JsValue items = A(a, 0);
            JsValue cb = A(a, 1);
            if (items.IsNullish)
            {
                throw Errors.Type("Object.groupBy called on null or undefined");
            }

            if (!Ops.IsCallable(cb))
            {
                throw Errors.Type(Ops.DisplayValue(cb) + " is not a function");
            }

            IteratorRecord rec = Ops.GetIterator(items);
            var keys = new List<PropertyKey>();
            var groups = new Dictionary<PropertyKey, List<JsValue>>(PropertyKeyComparer.Instance);
            double k = 0;
            while (Ops.IteratorStepValue(rec, out JsValue value))
            {
                Agent.Current.Tick();
                PropertyKey key;
                try
                {
                    key = Ops.ToPropertyKey(Ops.Call(cb, default, [value, JsValue.FromNumber(k)]));
                }
                catch (JsException)
                {
                    Ops.IteratorCloseSilently(rec);
                    throw;
                }

                if (!groups.TryGetValue(key, out List<JsValue>? list))
                {
                    list = [];
                    groups[key] = list;
                    keys.Add(key);
                }

                list.Add(value);
                k++;
            }

            var result = new JsObject((JsObject?)null);
            foreach (PropertyKey key in keys)
            {
                result.CreateDataPropertyOrThrow(key, JsValue.FromObject(Ops.CreateArray(groups[key])));
            }

            return JsValue.FromObject(result);
        });

        // ECMA-262 20.5.2.1 Error.isError ( arg ): true for objects with an [[ErrorData]] internal slot.
        r.Method(r.Intrinsics["%Error%"], "isError", 1, (t, a, nt) => Ops.Bool(A(a, 0).Ref is JsError));

        // ECMA-262 27.2.4.8 Promise.try ( callbackfn, ...args )
        r.Method(r.PromiseConstructor, "try", 1, (t, a, nt) =>
        {
            if (!t.IsObject)
            {
                throw Errors.Type("Promise.try called on non-object");
            }

            PromiseCapability cap = Promises.NewCapability(t);
            JsValue status;
            try
            {
                JsValue[] rest = a.Length > 1 ? a[1..].ToArray() : [];
                status = Ops.Call(A(a, 0), default, rest);
            }
            catch (JsException e)
            {
                Ops.Call(cap.Reject, default, [e.Value]);
                return JsValue.FromObject(cap.Promise);
            }

            Ops.Call(cap.Resolve, default, [status]);
            return JsValue.FromObject(cap.Promise);
        });
    }
}
