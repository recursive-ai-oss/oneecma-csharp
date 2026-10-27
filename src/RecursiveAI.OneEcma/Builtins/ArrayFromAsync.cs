namespace RecursiveAI.OneEcma;

/// <summary>
/// Array.fromAsync (23.1.2.2). The spec text is an async function; here it is a continuation-passing state machine over the
/// promise machinery, so awaiting never blocks or recurses on the host stack: every await resumes from a promise job.
/// </summary>
internal sealed class ArrayFromAsync
{
    private readonly Realm _realm;
    private readonly PromiseCapability _cap;
    private readonly JsValue _c;
    private JsValue _mapper;
    private JsValue _thisArg;
    private bool _mapping;
    private IteratorRecord? _rec;
    private JsObject _a = null!;
    private long _k;
    private JsObject? _arrayLike;
    private long _len;

    private ArrayFromAsync(Realm realm, PromiseCapability cap, JsValue c)
    {
        _realm = realm;
        _cap = cap;
        _c = c;
    }

    internal static JsValue FromAsync(JsValue t, ReadOnlySpan<JsValue> args, JsObject? nt)
    {
        Realm r = Agent.Current.Realm;
        PromiseCapability cap = Promises.NewCapability(JsValue.FromObject(r.PromiseConstructor));
        var st = new ArrayFromAsync(r, cap, t) { _mapper = Ops.Arg(args, 1), _thisArg = Ops.Arg(args, 2) };
        JsValue items = Ops.Arg(args, 0);
        st.Run(() => st.Start(items));
        return JsValue.FromObject(cap.Promise);
    }

    /// <summary>Runs a step of the async body; a throw rejects the result promise.</summary>
    private void Run(Action body)
    {
        try
        {
            body();
        }
        catch (JsException e)
        {
            Reject(e.Value);
        }
    }

    private void Reject(JsValue reason) => Ops.Call(_cap.Reject, default, [reason]);

    private void Resolve(JsValue value) => Ops.Call(_cap.Resolve, default, [value]);

    /// <summary>Await (6.2.3.1): the continuation runs from a promise job; a failure to wrap the value is reported at once.</summary>
    private void Await(JsValue v, Action<JsValue> ok, Action<JsValue> fail)
    {
        PromiseObject p;
        try
        {
            p = Promises.ForAwait(_realm, v);
        }
        catch (JsException e)
        {
            fail(e.Value);
            return;
        }

        Promises.ThenNative(p, x => Run(() => ok(x)), x => Run(() => fail(x)));
    }

    private void Start(JsValue items)
    {
        _mapping = false;
        if (!_mapper.IsUndefined)
        {
            if (!Ops.IsCallable(_mapper))
            {
                throw ArrayAlgo.NotFunction(_mapper);
            }

            _mapping = true;
        }

        JsValue usingAsync = Ops.GetMethod(items, new PropertyKey(WellKnown.AsyncIterator));
        if (usingAsync.IsUndefined)
        {
            JsValue usingSync = Ops.GetMethod(items, new PropertyKey(WellKnown.Iterator));
            if (!usingSync.IsUndefined)
            {
                _rec = AsyncFromSync.Create(Ops.GetIteratorFromMethod(items, usingSync));
            }
        }
        else
        {
            _rec = Ops.GetIteratorFromMethod(items, usingAsync);
        }

        if (_rec is not null)
        {
            _a = Ops.IsConstructor(_c) ? Ops.Construct((JsObject)_c.Ref!, default) : ArrayAlgo.ArrayCreate(0);
            _k = 0;
            IterStep();
            return;
        }

        // Neither async iterable nor iterable: an array-like.
        _arrayLike = Ops.ToObject(items);
        _len = Ops.LengthOfArrayLike(_arrayLike);
        _a = Ops.IsConstructor(_c) ? Ops.Construct((JsObject)_c.Ref!, [Ops.Num(_len)]) : ArrayAlgo.ArrayCreate(_len);
        _k = 0;
        LikeStep();
    }

    // ---- iterator path ---------------------------------------------------------------------------------------------------

    private void IterStep()
    {
        if (_k >= ArrayAlgo.MaxSafeInteger)
        {
            CloseThenReject(Errors.Type("Invalid array length").Value);
            return;
        }

        Agent.Current.Tick();
        JsValue next = Ops.Call(_rec!.Next, JsValue.FromObject(_rec.Iterator));
        Await(next, OnNextResult, Reject);
    }

    private void OnNextResult(JsValue nextResult)
    {
        if (nextResult.Ref is not JsObject ro)
        {
            throw Errors.Type("Iterator result " + Ops.DisplayValue(nextResult) + " is not an object");
        }

        if (Ops.ToBoolean(ro.Get("done")))
        {
            ArrayAlgo.SetLength(_a, _k);
            Resolve(JsValue.FromObject(_a));
            return;
        }

        JsValue nextValue = ro.Get("value");
        if (!_mapping)
        {
            Define(nextValue);
            return;
        }

        JsValue mapped;
        try
        {
            mapped = Ops.Call(_mapper, _thisArg, [nextValue, Ops.Num(_k)]);
        }
        catch (JsException e)
        {
            CloseThenReject(e.Value);
            return;
        }

        Await(mapped, Define, CloseThenReject);
    }

    private void Define(JsValue value)
    {
        try
        {
            ArrayAlgo.Create(_a, _k, value);
        }
        catch (JsException e)
        {
            CloseThenReject(e.Value);
            return;
        }

        _k++;
        IterStep();
    }

    /// <summary>AsyncIteratorClose (7.4.13) for a throw completion: call return, await it, then reject with the original error.</summary>
    private void CloseThenReject(JsValue error)
    {
        try
        {
            JsValue ret = Ops.GetMethod(JsValue.FromObject(_rec!.Iterator), "return");
            if (ret.IsUndefined)
            {
                Reject(error);
                return;
            }

            JsValue res = Ops.Call(ret, JsValue.FromObject(_rec.Iterator));
            Await(res, _ => Reject(error), _ => Reject(error));
        }
        catch (JsException)
        {
            Reject(error);
        }
    }

    // ---- array-like path -------------------------------------------------------------------------------------------------

    private void LikeStep()
    {
        if (_k >= _len)
        {
            ArrayAlgo.SetLength(_a, _len);
            Resolve(JsValue.FromObject(_a));
            return;
        }

        JsValue kValue = ArrayAlgo.Get(_arrayLike!, _k);
        Await(kValue, v =>
        {
            if (!_mapping)
            {
                LikeDefine(v);
                return;
            }

            JsValue mapped = Ops.Call(_mapper, _thisArg, [v, Ops.Num(_k)]);
            Await(mapped, LikeDefine, Reject);
        }, Reject);
    }

    private void LikeDefine(JsValue value)
    {
        ArrayAlgo.Create(_a, _k, value);
        _k++;
        LikeStep();
    }
}
