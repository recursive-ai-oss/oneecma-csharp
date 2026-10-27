using System.Text;

namespace RecursiveAI.OneEcma;

/// <summary>A realm (ECMA-262 9.3): the intrinsics, the global object and the global lexical declarations.</summary>
internal sealed partial class Realm
{
    private readonly Dictionary<(JsObject, int), Shape> _fnShapes = new();
    private readonly Dictionary<(FunctionCode, int), JsArray> _templates = new();
    internal readonly Dictionary<string, Cell> GlobalLexicals = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, JsObject> Intrinsics = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, JsObject> ErrorPrototypes = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, ModuleRecord> Modules = new(StringComparer.Ordinal);

    internal JsObject ObjectPrototype = null!;
    internal JsObject FunctionPrototype = null!;
    internal JsObject ArrayPrototype = null!;
    internal JsObject StringPrototype = null!;
    internal JsObject NumberPrototype = null!;
    internal JsObject BooleanPrototype = null!;
    internal JsObject SymbolPrototype = null!;
    internal JsObject BigIntPrototype = null!;
    internal JsObject ErrorPrototype = null!;
    internal JsObject IteratorPrototype = null!;
    internal JsObject ArrayIteratorPrototype = null!;
    internal JsObject GeneratorPrototype = null!;
    internal JsObject GeneratorFunctionPrototype = null!;
    internal JsObject AsyncFunctionPrototype = null!;
    internal JsObject AsyncGeneratorPrototype = null!;
    internal JsObject AsyncGeneratorFunctionPrototype = null!;
    internal JsObject AsyncIteratorPrototype = null!;
    internal JsObject AsyncFromSyncIteratorPrototype = null!;
    internal JsObject PromisePrototype = null!;
    internal JsObject PromiseConstructor = null!;
    internal JsObject Global = null!;
    internal JsObject ThrowTypeError = null!;
    internal JsObject EvalFunction = null!;
    internal JsValue ArrayProtoValues;
    internal Shape IterResultShape = null!;

    internal Realm(Agent agent)
    {
        Agent = agent;
    }

    internal Agent Agent { get; }

    /// <summary>The shape of a new function object: length, name and (variant 1 or 2) prototype, cached per prototype.</summary>
    internal Shape FunctionShape(JsObject proto, int variant)
    {
        if (_fnShapes.TryGetValue((proto, variant), out Shape? s))
        {
            return s;
        }

        s = Shape.RootFor(proto).Add("length", PropFlags.Configurable).Add("name", PropFlags.Configurable);
        if (variant == 1)
        {
            s = s.Add("prototype", PropFlags.Writable);
        }
        else if (variant == 2)
        {
            s = s.Add("prototype", PropFlags.None);
        }
        else if (variant == 3)
        {
            // A function whose prototype object is not created yet: the slot is marked as an accessor so that no cached or direct slot
            // read ever treats it as a data property, and the function replaces this shape with variant 1 when the property is first used.
            s = s.Add("prototype", PropFlags.Writable | PropFlags.Accessor);
        }

        _fnShapes[(proto, variant)] = s;
        return s;
    }

    internal JsArray GetTemplateObject(FunctionCode code, int site, TemplateSite t)
    {
        if (_templates.TryGetValue((code, site), out JsArray? a))
        {
            return a;
        }

        var cooked = new JsArray(ArrayPrototype);
        var raw = new JsArray(ArrayPrototype);
        for (int i = 0; i < t.Raw.Count; i++)
        {
            cooked.Push(t.Cooked[i] is null ? default : JsValue.FromString(t.Cooked[i]!));
            raw.Push(JsValue.FromString(t.Raw[i]));
        }

        FreezeArray(raw);
        cooked.DefineDirect("raw", JsValue.FromObject(raw), PropFlags.None);
        FreezeArray(cooked);
        _templates[(code, site)] = cooked;
        return cooked;
    }

    internal static void FreezeArray(JsArray a)
    {
        var keys = new List<PropertyKey>();
        a.OwnKeys(keys);
        foreach (PropertyKey k in keys)
        {
            if (a.GetOwnProperty(k, out PropertyDescriptor d))
            {
                var nd = new PropertyDescriptor { HasConfigurable = true, Configurable = false };
                if (!d.IsAccessor)
                {
                    nd.HasWritable = true;
                    nd.Writable = false;
                }

                a.DefineOwnProperty(k, nd);
            }
        }

        a.PreventExtensions();
    }

    // ---- errors -------------------------------------------------------------------------------

    internal JsError NewError(string kind, string? message)
    {
        JsObject proto = ErrorPrototypes.TryGetValue(kind, out JsObject? p) ? p : ErrorPrototype;
        var e = new JsError(proto);
        InitError(e, message);
        return e;
    }

    /// <param name="e">The error object.</param>
    /// <param name="message">The message, or null for none.</param>
    /// <param name="skipUntil">The <c>new.target</c> of the construction: V8 leaves the frames of a subclass constructor out of the trace, so frames up to and including this function's are dropped.</param>
    internal void InitError(JsError e, string? message, JsObject? skipUntil = null)
    {
        if (message is not null)
        {
            e.DefineDirect("message", JsValue.FromString(message), PropFlags.Hidden);
        }

        // V8 captures the frames when the error is created and formats `stack` ("Name: message" then one line per frame) when it is
        // first read, so the text is rendered once now and again on the first read, which sees a name or message set in between.
        e.Frames = Agent.CaptureFrames(10, skipUntil);
        e.DefineDirect("stack", JsValue.FromString(Errors.FormatStack(Errors.Header(e), e.Frames)), PropFlags.Hidden);
        e.StackPending = true;
    }

    // ---- helpers for defining built-ins -----------------------------------------------------------

    internal NativeFunction Fn(string name, int length, NativeFn fn) => new(this, name, length, fn);

    internal NativeFunction Method(JsObject target, string name, int length, NativeFn fn)
    {
        var f = new NativeFunction(this, name, length, fn);
        target.DefineDirect(name, JsValue.FromObject(f), PropFlags.Hidden);
        return f;
    }

    internal NativeFunction Method(JsObject target, JsSymbol sym, string name, int length, NativeFn fn, PropFlags flags = PropFlags.Hidden)
    {
        var f = new NativeFunction(this, name, length, fn);
        target.DefineDirect(new PropertyKey(sym), JsValue.FromObject(f), flags);
        return f;
    }

    internal void Getter(JsObject target, PropertyKey key, string name, NativeFn fn, NativeFn? setter = null)
    {
        var g = new NativeFunction(this, "get " + name, 0, fn);
        NativeFunction? s = setter is null ? null : new NativeFunction(this, "set " + name, 1, setter);
        target.DefineAccessorDirect(key, g, s, PropFlags.Configurable);
    }

    internal static void Value(JsObject target, string name, JsValue v, PropFlags flags = PropFlags.Hidden) => target.DefineDirect(name, v, flags);

    /// <summary>Creates a constructor function with its prototype object and links them.</summary>
    internal NativeFunction Ctor(string name, int length, NativeFn fn, JsObject prototype, JsObject? ctorProto = null)
    {
        var f = new NativeFunction(this, name, length, fn, true, ctorProto);
        f.DefineDirect("prototype", JsValue.FromObject(prototype), PropFlags.None);
        prototype.DefineDirect("constructor", JsValue.FromObject(f), PropFlags.Hidden);
        Global.DefineDirect(name, JsValue.FromObject(f), PropFlags.Hidden);
        Intrinsics["%" + name + "%"] = f;
        return f;
    }

    internal JsObject NewObject() => new(ObjectPrototype);

    /// <summary>CreateRealm (9.3.1) followed by SetDefaultGlobalBindings.</summary>
    internal static Realm Create(Agent agent)
    {
        var r = new Realm(agent);
        Realm saved = agent.Realm;
        agent.Realm = r;
        try
        {
            r.Initialize();
        }
        finally
        {
            if (saved is not null)
            {
                agent.Realm = saved;
            }
        }

        return r;
    }

    private void Initialize()
    {
        ObjectPrototype = new ObjectPrototypeObject();
        FunctionPrototype = new FunctionPrototypeObject(this, ObjectPrototype);
        Global = new JsObject(ObjectPrototype);
        IterResultShape = Shape.RootFor(ObjectPrototype).Add("value", PropFlags.Default).Add("done", PropFlags.Default);
        ArrayPrototype = new JsArray(ObjectPrototype);
        Builtins.Setup(this);
    }
}

internal sealed class ObjectPrototypeObject : JsObject
{
    internal ObjectPrototypeObject()
        : base((JsObject?)null)
    {
    }

    internal override bool ImmutablePrototype => true;
}

internal sealed class FunctionPrototypeObject : JsFunction
{
    internal FunctionPrototypeObject(Realm realm, JsObject objectProto)
        : base(Shape.RootFor(objectProto), realm)
    {
        AddProperty("length", JsValue.FromNumber(0), PropFlags.Configurable);
        AddProperty("name", JsValue.FromString(""), PropFlags.Configurable);
    }

    internal override JsValue Call(JsValue thisValue, ReadOnlySpan<JsValue> args) => default;
}
