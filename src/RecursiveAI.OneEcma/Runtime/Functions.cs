namespace RecursiveAI.OneEcma;

/// <summary>The signature of a built-in function body.</summary>
/// <param name="thisValue">The this value.</param>
/// <param name="args">The arguments; indexes past the end read as undefined through <see cref="Ops.Arg"/>.</param>
/// <param name="newTarget">The new.target for a construct call, null for a plain call.</param>
internal delegate JsValue NativeFn(JsValue thisValue, ReadOnlySpan<JsValue> args, JsObject? newTarget);

/// <summary>Base of all callable objects (ECMA-262 10.2).</summary>
internal abstract class JsFunction : JsObject
{
    internal Realm Realm;

    protected JsFunction(Shape shape, Realm realm)
        : base(shape)
    {
        Realm = realm;
    }

    internal override string ClassName => "Function";

    internal override bool IsCallable => true;
}

/// <summary>A built-in function implemented in C#.</summary>
internal sealed class NativeFunction : JsFunction
{
    private readonly NativeFn _fn;
    private readonly bool _ctor;

    internal NativeFunction(Realm realm, string name, int length, NativeFn fn, bool isConstructor = false, JsObject? proto = null)
        : base(realm.FunctionShape(proto ?? realm.FunctionPrototype, 0), realm)
    {
        _fn = fn;
        _ctor = isConstructor;
        Slots[0] = JsValue.FromNumber(length);
        Slots[1] = JsValue.FromString(name);
    }

    internal override bool IsConstructor => _ctor;

    internal override JsValue Call(JsValue thisValue, ReadOnlySpan<JsValue> args)
    {
        Agent a = Agent.Current;
        if (ReferenceEquals(a.Realm, Realm))
        {
            return _fn(thisValue, args, null);
        }

        Realm saved = a.Realm;
        a.Realm = Realm;
        try
        {
            return _fn(thisValue, args, null);
        }
        finally
        {
            a.Realm = saved;
        }
    }

    internal override JsValue Construct(ReadOnlySpan<JsValue> args, JsObject newTarget)
    {
        Agent a = Agent.Current;
        Realm saved = a.Realm;
        a.Realm = Realm;
        try
        {
            return _fn(default, args, newTarget);
        }
        finally
        {
            a.Realm = saved;
        }
    }
}

/// <summary>A function whose body is bytecode (ECMA-262 10.2): ordinary, arrow, method, accessor, class constructor, generator, async.</summary>
internal sealed class ScriptFunction : JsFunction
{
    internal FunctionCode Code;
    internal Env? Env;
    internal JsObject? HomeObject;
    internal Frame? OuterFrame;
    internal bool IsClassConstructor;
    internal bool IsDerived;
    internal List<ClassFieldDef>? Fields;
    internal List<PrivateMethodDef>? PrivateMethods;
    internal string ModuleId = "";
    internal ModuleRecord? Module;

    internal ScriptFunction(Realm realm, FunctionCode code, Env? env, JsObject proto, bool withPrototype, bool readOnlyPrototype = false)
        : base(realm.FunctionShape(proto, withPrototype ? (readOnlyPrototype ? 2 : 1) : 0), realm)
    {
        Code = code;
        Env = env;
        Slots[0] = JsValue.FromNumber(code.Length);
        Slots[1] = JsValue.FromString(code.Name);
    }

    internal override bool IsConstructor => Code.Kind switch
    {
        FunctionKind.Normal => !Code.IsAsync && !Code.IsGenerator,
        FunctionKind.ClassConstructor or FunctionKind.DerivedConstructor => true,
        _ => false,
    };

    internal override JsValue Call(JsValue thisValue, ReadOnlySpan<JsValue> args) => Vm.CallFunction(this, thisValue, args, null);

    internal override JsValue Construct(ReadOnlySpan<JsValue> args, JsObject newTarget) => Vm.CallFunction(this, default, args, newTarget);
}

internal sealed class ClassFieldDef
{
    internal PropertyKey Key;
    internal PrivateName? Private;
    internal ScriptFunction? Init;
    internal bool AnonymousFn;
}

internal sealed class PrivateMethodDef
{
    internal PrivateName Name = null!;
    internal JsObject? Method;
    internal JsObject? Getter;
    internal JsObject? Setter;
}

/// <summary>A Private Name (6.2.12): identity per class evaluation.</summary>
internal sealed class PrivateName
{
    internal PrivateName(string description) => Description = description;

    internal string Description { get; }
}

/// <summary>A bound function exotic object (10.4.1).</summary>
internal sealed class BoundFunction : JsFunction
{
    internal readonly JsObject Target;
    internal readonly JsValue BoundThis;
    internal readonly JsValue[] BoundArgs;

    internal BoundFunction(Realm realm, JsObject target, JsValue boundThis, JsValue[] boundArgs, JsObject? proto)
        : base(Shape.RootFor(proto), realm)
    {
        Target = target;
        BoundThis = boundThis;
        BoundArgs = boundArgs;
    }

    internal override bool IsConstructor => Target.IsConstructor;

    private JsValue[] Merge(ReadOnlySpan<JsValue> args)
    {
        var all = new JsValue[BoundArgs.Length + args.Length];
        BoundArgs.CopyTo(all, 0);
        args.CopyTo(all.AsSpan(BoundArgs.Length));
        return all;
    }

    internal override JsValue Call(JsValue thisValue, ReadOnlySpan<JsValue> args) => Target.Call(BoundThis, Merge(args));

    internal override JsValue Construct(ReadOnlySpan<JsValue> args, JsObject newTarget) =>
        Target.Construct(Merge(args), ReferenceEquals(newTarget, this) ? Target : newTarget);
}

/// <summary>An error instance ([[ErrorData]]) with the frames captured when it was created.</summary>
internal sealed class JsError : JsObject
{
    internal List<StackFrameInfo>? Frames;

    internal JsError(JsObject? proto)
        : base(proto)
    {
    }

    internal override string ClassName => "Error";
}

/// <summary>A Boolean, Number, Symbol or BigInt wrapper object.</summary>
internal sealed class JsPrimitiveObject : JsObject
{
    internal readonly JsValue Value;
    private readonly string _class;

    internal JsPrimitiveObject(JsObject? proto, JsValue value, string className)
        : base(proto)
    {
        Value = value;
        _class = className;
    }

    internal override string ClassName => _class;
}

/// <summary>A String exotic object (10.4.3).</summary>
internal sealed class JsStringObject : JsObject
{
    internal readonly string Value;

    internal JsStringObject(JsObject? proto, string value)
        : base(proto)
    {
        Value = value;
        AddProperty("length", JsValue.FromNumber(value.Length), PropFlags.None);
    }

    internal override string ClassName => "String";

    internal override bool IsPlainStorage => false;

    internal override bool GetOwnProperty(PropertyKey key, out PropertyDescriptor desc)
    {
        if (base.GetOwnProperty(key, out desc))
        {
            return true;
        }

        if (key.TryGetIndex(out uint i) && i < (uint)Value.Length)
        {
            desc = PropertyDescriptor.Data(JsValue.FromString(Value[(int)i].ToString()), PropFlags.Enumerable);
            return true;
        }

        return false;
    }

    internal override bool TryGetOwn(PropertyKey key, JsValue receiver, out JsValue value)
    {
        if (key.TryGetIndex(out uint i) && i < (uint)Value.Length)
        {
            value = JsValue.FromString(Value[(int)i].ToString());
            return true;
        }

        return base.TryGetOwn(key, receiver, out value);
    }

    internal override bool DefineOwnProperty(PropertyKey key, in PropertyDescriptor desc)
    {
        if (key.TryGetIndex(out uint i) && i < (uint)Value.Length)
        {
            // 10.4.3.2 step 3: an existing string index is compatible only with an identical descriptor
            // (IsCompatiblePropertyDescriptor against {value: the character, writable: false, enumerable: true, configurable: false}).
            // Nothing is stored: the character is not a property of the object's storage.
            return !desc.IsAccessor
                && !(desc.HasConfigurable && desc.Configurable)
                && !(desc.HasEnumerable && !desc.Enumerable)
                && !(desc.HasWritable && desc.Writable)
                && !(desc.HasValue && !Ops.SameValue(desc.Value, JsValue.FromString(Value[(int)i].ToString())));
        }

        return base.DefineOwnProperty(key, desc);
    }

    internal override bool Delete(PropertyKey key) => key.TryGetIndex(out uint i) && i < (uint)Value.Length ? false : base.Delete(key);

    internal override void OwnKeys(List<PropertyKey> keys)
    {
        for (int i = 0; i < Value.Length; i++)
        {
            keys.Add(new PropertyKey(i.ToString(System.Globalization.NumberFormatInfo.InvariantInfo)));
        }

        base.OwnKeys(keys);
    }
}

/// <summary>An arguments object (10.4.4); mapped when <see cref="MapEnv"/> is set.</summary>
internal sealed class JsArguments : JsObject
{
    internal Env? MapEnv;
    internal int[]? Map;

    internal JsArguments(JsObject? proto)
        : base(proto)
    {
    }

    internal override string ClassName => "Arguments";

    internal override bool IsPlainStorage => false;

    private bool IsMapped(PropertyKey key, out int slot)
    {
        slot = -1;
        if (Map is not null && key.TryGetIndex(out uint i) && i < (uint)Map.Length && Map[i] >= 0)
        {
            slot = Map[i];
            return true;
        }

        return false;
    }

    private void Unmap(PropertyKey key)
    {
        if (key.TryGetIndex(out uint i) && Map is not null && i < (uint)Map.Length)
        {
            Map[i] = -1;
        }
    }

    internal override bool GetOwnProperty(PropertyKey key, out PropertyDescriptor desc)
    {
        if (!base.GetOwnProperty(key, out desc))
        {
            return false;
        }

        if (IsMapped(key, out int slot))
        {
            desc.Value = MapEnv!.Slots[slot];
        }

        return true;
    }

    internal override bool TryGetOwn(PropertyKey key, JsValue receiver, out JsValue value)
    {
        if (IsMapped(key, out int slot))
        {
            value = MapEnv!.Slots[slot];
            return true;
        }

        return base.TryGetOwn(key, receiver, out value);
    }

    internal override bool DefineOwnProperty(PropertyKey key, in PropertyDescriptor desc)
    {
        bool mapped = IsMapped(key, out int slot);
        PropertyDescriptor d = desc;
        if (mapped && desc.IsData && !desc.HasValue && desc.HasWritable && !desc.Writable)
        {
            d.Value = MapEnv!.Slots[slot];
            d.HasValue = true;
        }

        if (!base.DefineOwnProperty(key, d))
        {
            return false;
        }

        if (mapped)
        {
            if (desc.IsAccessor)
            {
                Unmap(key);
            }
            else
            {
                if (desc.HasValue)
                {
                    MapEnv!.Slots[slot] = desc.Value;
                }

                if (desc.HasWritable && !desc.Writable)
                {
                    Unmap(key);
                }
            }
        }

        return true;
    }

    internal override bool Set(PropertyKey key, JsValue value, JsValue receiver)
    {
        if (ReferenceEquals(receiver.Ref, this) && IsMapped(key, out int slot))
        {
            MapEnv!.Slots[slot] = value;
        }

        return base.Set(key, value, receiver);
    }

    internal override bool Delete(PropertyKey key)
    {
        bool mapped = IsMapped(key, out _);
        if (!base.Delete(key))
        {
            return false;
        }

        if (mapped)
        {
            Unmap(key);
        }

        return true;
    }
}
