using System.Runtime.CompilerServices;
using RecursiveAI.OneEcma.Text;

namespace RecursiveAI.OneEcma;

/// <summary>Where a value was written: the span of the expression that produced it, and the earlier place the same object was written.</summary>
internal sealed class Origin(SourceText source, int start, int end, Origin? previous)
{
    internal readonly SourceText Source = source;
    internal readonly int Start = start;
    internal readonly int End = end;

    /// <summary>For an object, the origin it had before this store (it was copied from another binding or property).</summary>
    internal readonly Origin? Previous = previous;
}

/// <summary>
/// What a traced replay remembers about where values came from. The recording tracer fills it while the script runs; the
/// explainer reads it when the error is turned into a report. Held only during a replay, so the lean run pays nothing.
/// </summary>
internal sealed class TraceState
{
    /// <summary>The longest chain of origins kept for one value.</summary>
    internal const int MaxChain = 8;

    private readonly ConditionalWeakTable<Env, Origin?[]> _bindings = new();
    private readonly Dictionary<string, Origin> _globals = new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<JsObject, Dictionary<string, Origin>> _props = new();
    private readonly ConditionalWeakTable<JsObject, Origin> _objects = new();

    internal Origin? BindingOrigin(Env env, int slot) => _bindings.TryGetValue(env, out Origin?[]? a) && slot < a.Length ? a[slot] : null;

    internal Origin? GlobalOrigin(string name) => _globals.TryGetValue(name, out Origin? o) ? o : null;

    internal Origin? PropertyOrigin(JsObject obj, string key) => _props.TryGetValue(obj, out Dictionary<string, Origin>? d) && d.TryGetValue(key, out Origin? o) ? o : null;

    /// <summary>Notes the value a store instruction writes, by the instruction at <paramref name="pc"/> when the compiler recorded the value's span.</summary>
    internal void OnStore(Frame f, Instr ins, int pc, JsValue[] st, int sp)
    {
        if (f.Code.ValueSpans is not { } spans || !spans.TryGetValue(pc, out (int Start, int End) span) || sp < 1)
        {
            // A write whose source is not tracked (compound assignment, destructuring) replaces the value, so an older origin would mislead.
            if (ins.Op == Op.SetProp && sp >= 2 && st[sp - 2].Ref is JsObject stale && _props.TryGetValue(stale, out Dictionary<string, Origin>? staleProps))
            {
                staleProps.Remove(f.Code.Names[ins.A]);
            }

            return;
        }

        JsValue value = st[sp - 1];
        Origin? prior = value.Ref is JsObject vo && _objects.TryGetValue(vo, out Origin? po) ? po : null;
        var origin = new Origin(f.Code.Source, span.Start, span.End, Trim(prior));
        if (value.Ref is JsObject o)
        {
            _objects.AddOrUpdate(o, origin);
        }

        FunctionCode fc = f.Code;
        switch (ins.Op)
        {
            case Op.InitSlot:
            case Op.StoreSlot:
            case Op.StoreSlotChecked:
                {
                    Env? e = f.Env;
                    for (int h = ins.A; h > 0 && e is not null; h--)
                    {
                        e = e.Parent;
                    }

                    if (e is not null)
                    {
                        Origin?[] arr = _bindings.GetValue(e, static env => new Origin?[env.Slots.Length]);
                        if (ins.B < arr.Length)
                        {
                            arr[ins.B] = origin;
                        }
                    }

                    break;
                }

            case Op.InitGlobalLexical:
            case Op.StoreGlobal:
                _globals[fc.Names[ins.A]] = origin;
                break;
            case Op.DefineField:
            case Op.SetProp:
                if (sp >= 2 && st[sp - 2].Ref is JsObject target)
                {
                    Dictionary<string, Origin> d = _props.GetOrCreateValue(target);
                    d[fc.Names[ins.A]] = origin;
                }

                break;
            case Op.SetElem:
                // Stack before the store: object, key, value.
                if (sp >= 3 && st[sp - 3].Ref is JsObject elemTarget && (st[sp - 2].IsString || st[sp - 2].IsNumber))
                {
                    Dictionary<string, Origin> d = _props.GetOrCreateValue(elemTarget);
                    d[st[sp - 2].IsString ? st[sp - 2].AsString : Ops.NumberToStr(st[sp - 2].Num)] = origin;
                }

                break;
        }
    }

    private static Origin? Trim(Origin? o)
    {
        // Keep the chain bounded: a loop that copies a value around must not grow it without end.
        int n = 0;
        for (Origin? q = o; q is not null; q = q.Previous)
        {
            n++;
        }

        return n < MaxChain ? o : null;
    }
}

/// <summary>The tracer of a replay: records where bindings and properties got their values.</summary>
internal struct RecordingTrace : ITracer
{
    public readonly void OnOp(Frame frame, Instr ins, int pc, JsValue[] st, int sp)
    {
        switch (ins.Op)
        {
            case Op.InitSlot:
            case Op.StoreSlot:
            case Op.StoreSlotChecked:
            case Op.InitGlobalLexical:
            case Op.StoreGlobal:
            case Op.DefineField:
            case Op.SetProp:
            case Op.SetElem:
                Agent.CurrentOrNull?.Trace?.OnStore(frame, ins, pc, st, sp);
                break;
        }
    }
}
