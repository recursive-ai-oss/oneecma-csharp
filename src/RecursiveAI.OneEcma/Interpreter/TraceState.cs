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

/// <summary>How an undefined came to be.</summary>
internal enum UndefinedKind
{
    /// <summary>A property read found no such key anywhere on the object or its prototypes.</summary>
    MissingProperty,

    /// <summary>The key exists and holds undefined.</summary>
    PropertyHoldsUndefined,

    /// <summary>A script function returned without a value.</summary>
    CallReturned,

    /// <summary><c>find</c> or <c>findLast</c> matched nothing.</summary>
    FindNothing,
}

/// <summary>The record a tagged undefined points at: what produced it and where in the source.</summary>
internal sealed class UndefinedOrigin
{
    internal int Id;
    internal UndefinedKind Kind;
    internal SourceText Source = null!;
    internal int Start;
    internal int End;

    /// <summary>The object whose property was read.</summary>
    internal JsObject? Owner;

    /// <summary>The property name, the function name for a call, or the search method for a find.</summary>
    internal string Key = "";

    internal bool IsElement;

    /// <summary>The array a find searched.</summary>
    internal JsArray? Receiver;
}

/// <summary>Where an Invalid Date was made (`new Date(x)`), and the argument it was made from.</summary>
internal sealed class MadeDate(SourceText source, int start, int end, JsValue argument)
{
    internal readonly SourceText Source = source;
    internal readonly int Start = start;
    internal readonly int End = end;
    internal readonly JsValue Argument = argument;
}

/// <summary>A call in flight in one frame: it finishes when the frame runs its next instruction.</summary>
internal sealed class PendingCall(int pc, JsValue fn, JsValue thisValue)
{
    internal readonly int Pc = pc;
    internal readonly JsValue Fn = fn;
    internal readonly JsValue This = thisValue;
}

/// <summary>How an array made by <c>filter</c> came out: the call and the lengths before and after.</summary>
internal sealed class FilterResult(SourceText source, int start, int end, long inputLength, long keptLength)
{
    internal readonly SourceText Source = source;
    internal readonly int Start = start;
    internal readonly int End = end;
    internal readonly long InputLength = inputLength;
    internal readonly long KeptLength = keptLength;
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

    /// <summary>The operands of the latest arithmetic or bitwise operator the replay started; when such an operator throws, these are the values it threw on.</summary>
    internal JsValue LastLeft;

    internal JsValue LastRight;

    internal bool HasLastBinary;

    internal void OnBinary(JsValue left, JsValue right)
    {
        LastLeft = left;
        LastRight = right;
        HasLastBinary = true;
    }

    // ---- where an undefined came from -------------------------------------------------------------
    //
    // An undefined carries no identity, but its number field is free: in a traced replay the operation that produces an undefined
    // (a property read that finds nothing, a call that returns nothing, a find that matches nothing) writes the id of a record
    // into that field. Copies of the value, through arguments, returns, bindings and properties, keep the id, so the explainer can
    // follow an undefined back across function boundaries. Nothing else looks at the field: undefined equality compares references.

    private const int RingSize = 1 << 16;

    private readonly UndefinedOrigin?[] _undefinedRing = new UndefinedOrigin?[RingSize];
    private int _nextUndefinedId = 1;

    private readonly ConditionalWeakTable<Frame, PendingCall> _pendingCalls = new();
    private int _pendingCallCount;

    private Frame? _getFrame;
    private int _getPc;
    private JsValue _getOwner;
    private JsValue _getKey;
    private string? _getName;

    private readonly ConditionalWeakTable<JsObject, FilterResult> _filtered = new();
    private readonly ConditionalWeakTable<JsObject, MadeDate> _madeDates = new();
    private Frame? _newFrame;
    private int _newPc;
    private JsValue _newArg;

    /// <summary>Where an Invalid Date was made and from what, for a Date built with `new Date(x)` in the traced run.</summary>
    internal MadeDate? MadeDateOf(JsObject date) => _madeDates.TryGetValue(date, out MadeDate? m) ? m : null;

    /// <summary>The record behind a tagged undefined, or null for any other value.</summary>
    internal UndefinedOrigin? UndefinedOriginOf(JsValue v)
    {
        if (!v.IsUndefined || v.Num < 1)
        {
            return null;
        }

        int id = (int)v.Num;
        return _undefinedRing[id & (RingSize - 1)] is { } o && o.Id == id ? o : null;
    }

    /// <summary>What an array made by <c>filter</c> kept, for the note under a <c>find</c> that found nothing in it.</summary>
    internal FilterResult? FilterOf(JsObject array) => _filtered.TryGetValue(array, out FilterResult? r) ? r : null;

    private int Register(UndefinedOrigin o)
    {
        int id = _nextUndefinedId++;
        o.Id = id;
        _undefinedRing[id & (RingSize - 1)] = o;
        return id;
    }

    /// <summary>Called before every operation of a traced replay: finishes the read or call that the previous operation of this frame started, then starts the next one.</summary>
    internal void Observe(Frame frame, Instr ins, int pc, JsValue[] st, int sp)
    {
        if (_getFrame is not null)
        {
            Frame gf = _getFrame;
            _getFrame = null;
            if (ReferenceEquals(gf, frame) && pc == _getPc + 1 && sp >= 1 && st[sp - 1].IsUndefined && st[sp - 1].Num == 0)
            {
                CompleteGet(frame, st, sp);
            }
        }

        if (_newFrame is not null)
        {
            Frame nf = _newFrame;
            _newFrame = null;
            if (ReferenceEquals(nf, frame) && pc == _newPc + 1 && sp >= 1 && st[sp - 1].Ref is JsDate { Tv: double tv } made && double.IsNaN(tv))
            {
                _madeDates.AddOrUpdate(made, new MadeDate(frame.Code.Source, frame.Code.SpanStart[_newPc], frame.Code.SpanEnd[_newPc], _newArg));
            }
        }

        if (_pendingCallCount > 0 && _pendingCalls.TryGetValue(frame, out PendingCall? call))
        {
            _pendingCalls.Remove(frame);
            _pendingCallCount--;
            if (pc == call.Pc + 1 && sp >= 1)
            {
                CompleteCall(frame, call, st, sp);
            }
        }

        switch (ins.Op)
        {
            case Op.GetProp when sp >= 1:
                _getFrame = frame;
                _getPc = pc;
                _getOwner = st[sp - 1];
                _getKey = default;
                _getName = frame.Code.Names[ins.A];
                break;
            case Op.GetElem when sp >= 2:
                _getFrame = frame;
                _getPc = pc;
                _getOwner = st[sp - 2];
                _getKey = st[sp - 1];
                _getName = null;
                break;
            case Op.New when ins.A == 1 && sp >= 2 && st[sp - 2].Ref is NativeFunction:
                _newFrame = frame;
                _newPc = pc;
                _newArg = st[sp - 1];
                break;
            case Op.Call when sp >= ins.A + 2:
                {
                    int bs = sp - ins.A - 2;
                    _pendingCalls.AddOrUpdate(frame, new PendingCall(pc, st[bs], st[bs + 1]));
                    _pendingCallCount++;
                    break;
                }
        }
    }

    private void CompleteGet(Frame frame, JsValue[] st, int sp)
    {
        if (_getOwner.Ref is not JsObject owner || (owner is ProxyObject))
        {
            return;
        }

        string? key = _getName ?? (_getKey.IsString ? _getKey.AsString : _getKey.IsNumber ? Ops.NumberToStr(_getKey.Num) : null);
        if (key is null)
        {
            return;
        }

        bool present = false;
        int guard = 0;
        for (JsObject? o = owner; o is not null && guard++ < 64 && !present; o = o.Proto)
        {
            if (o is ProxyObject)
            {
                return;
            }

            present = o.GetOwnProperty(new PropertyKey(key), out PropertyDescriptor d) && !d.IsAccessor;
            if (!present && o.GetOwnProperty(new PropertyKey(key), out PropertyDescriptor ad) && ad.IsAccessor)
            {
                return;
            }
        }

        FunctionCode fc = frame.Code;
        var origin = new UndefinedOrigin
        {
            Kind = present ? UndefinedKind.PropertyHoldsUndefined : UndefinedKind.MissingProperty,
            Source = fc.Source,
            Start = fc.SpanStart[_getPc],
            End = ExtendToMember(fc.Source.Text, fc.SpanEnd[_getPc], key, _getName is null),
            Owner = owner,
            Key = key,
            IsElement = _getName is null,
        };
        st[sp - 1] = new JsValue(null, Register(origin));
    }

    private void CompleteCall(Frame frame, PendingCall call, JsValue[] st, int sp)
    {
        JsValue result = st[sp - 1];
        FunctionCode fc = frame.Code;
        if (result.Ref is JsArray filtered && call.Fn.Ref is NativeFunction { } nf && NativeName(nf) == "filter" && call.This.Ref is JsArray input)
        {
            _filtered.AddOrUpdate(filtered, new FilterResult(fc.Source, fc.SpanStart[call.Pc], fc.SpanEnd[call.Pc], input.Length, filtered.Length));
            return;
        }

        if (!result.IsUndefined || result.Num != 0)
        {
            return;
        }

        UndefinedOrigin? origin = null;
        if (call.Fn.Ref is NativeFunction nat && NativeName(nat) is "find" or "findLast" && call.This.Ref is JsArray array)
        {
            origin = new UndefinedOrigin { Kind = UndefinedKind.FindNothing, Receiver = array, Key = NativeName(nat) };
        }
        else if (call.Fn.Ref is ScriptFunction sf)
        {
            origin = new UndefinedOrigin { Kind = UndefinedKind.CallReturned, Key = sf.Code.Name };
        }

        if (origin is null)
        {
            return;
        }

        origin.Source = fc.Source;
        origin.Start = fc.SpanStart[call.Pc];
        origin.End = fc.SpanEnd[call.Pc];
        st[sp - 1] = new JsValue(null, Register(origin));
    }

    /// <summary>The recorded span of a property read stops after the object; this takes in the <c>.name</c> or <c>[key]</c> that follows.</summary>
    private static int ExtendToMember(string text, int end, string key, bool element)
    {
        int i = end;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        if (element)
        {
            if (i >= text.Length || text[i] != '[')
            {
                return end;
            }

            int depth = 0;
            for (int j = i; j < text.Length; j++)
            {
                depth += text[j] == '[' ? 1 : text[j] == ']' ? -1 : 0;
                if (depth == 0)
                {
                    return j + 1;
                }
            }

            return end;
        }

        if (i < text.Length && text[i] == '?')
        {
            i++;
        }

        if (i >= text.Length || text[i] != '.')
        {
            return end;
        }

        i++;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        return string.CompareOrdinal(text, i, key, 0, key.Length) == 0 ? i + key.Length : end;
    }

    private static string NativeName(NativeFunction f) => f.GetOwnProperty(new PropertyKey("name"), out PropertyDescriptor d) && !d.IsAccessor && d.Value.IsString ? d.Value.AsString : string.Empty;

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
        Agent.CurrentOrNull?.Trace?.Observe(frame, ins, pc, st, sp);
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
            case >= Op.Add and <= Op.UShr:
                if (sp >= 2)
                {
                    Agent.CurrentOrNull?.Trace?.OnBinary(st[sp - 2], st[sp - 1]);
                }

                break;
        }
    }
}
