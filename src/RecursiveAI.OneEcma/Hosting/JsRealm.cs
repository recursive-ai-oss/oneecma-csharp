using System.Numerics;
using System.Runtime.CompilerServices;

namespace RecursiveAI.OneEcma;

/// <summary>Attributes of a property defined from C#. Combine with <c>|</c>.</summary>
[Flags]
public enum JsAttributes
{
    /// <summary>Read-only, hidden from enumeration, not configurable.</summary>
    None = 0,

    /// <summary>The value can be changed by assignment.</summary>
    Writable = 1,

    /// <summary>The property shows up in <c>for-in</c>, <c>Object.keys</c> and spread.</summary>
    Enumerable = 2,

    /// <summary>The property can be deleted and redefined.</summary>
    Configurable = 4,

    /// <summary>Writable, enumerable and configurable: what plain assignment creates.</summary>
    All = Writable | Enumerable | Configurable,

    /// <summary>Writable and configurable but not enumerable: what built-in methods use. The default for host members.</summary>
    Method = Writable | Configurable,
}

/// <summary>The state of a <see cref="JsPromise"/>.</summary>
public enum JsPromiseState
{
    /// <summary>Not settled yet.</summary>
    Pending,

    /// <summary>Fulfilled with a value.</summary>
    Fulfilled,

    /// <summary>Rejected with a reason.</summary>
    Rejected,
}

/// <summary>A constructor implemented in C#. It runs for <c>new F(...)</c>, and for <c>super(...)</c> from a script subclass.</summary>
/// <param name="args">The arguments; read-only and valid only during the call.</param>
/// <param name="newTarget">The constructor <c>new</c> was applied to; pass it to <see cref="JsRealm.PrototypeFor"/> so subclasses get their prototype.</param>
/// <returns>The new object.</returns>
public delegate JsValue HostConstructor(ReadOnlySpan<JsValue> args, JsObject newTarget);

/// <summary>An object that stands for a .NET object in script. Script reads and writes reach the target only through the members its prototype exposes.</summary>
public sealed class JsHostObject : JsObject
{
    internal JsHostObject(JsObject? proto, object target)
        : base(proto)
    {
        Target = target;
    }

    /// <summary>The .NET object this script object stands for.</summary>
    public object Target { get; }
}

/// <summary>A promise created from C#. Resolve or reject it once; script sees an ordinary <c>Promise</c> in <see cref="Value"/>.</summary>
public sealed class JsPromise
{
    private readonly JsRealm _realm;
    private readonly PromiseObject _promise;

    internal JsPromise(JsRealm realm, PromiseObject promise)
    {
        _realm = realm;
        _promise = promise;
    }

    /// <summary>The promise as a script value.</summary>
    public JsValue Value => JsValue.FromObject(_promise);

    /// <summary>Whether the promise is pending, fulfilled or rejected.</summary>
    public JsPromiseState State => _promise.State switch { 1 => JsPromiseState.Fulfilled, 2 => JsPromiseState.Rejected, _ => JsPromiseState.Pending };

    /// <summary>The fulfilment value or rejection reason; undefined while pending.</summary>
    public JsValue Result => _promise.State == 0 ? default : _promise.Result;

    /// <summary>Resolves the promise; a thenable is followed. Ignored when already settled.</summary>
    /// <param name="value">The value.</param>
    public void Resolve(JsValue value) => _realm.Run(() =>
    {
        Promises.ResolveWith(_promise, value);
        return default(JsValue);
    });

    /// <summary>Rejects the promise. Ignored when already settled.</summary>
    /// <param name="reason">The reason, usually an error from <see cref="JsRealm.NewError"/>.</param>
    public void Reject(JsValue reason) => _realm.Run(() =>
    {
        Promises.Reject(_promise, reason);
        return default(JsValue);
    });

    /// <summary>Runs <paramref name="onSettled"/> as a promise job once the promise settles (immediately queued if it already has). Counts as handling a rejection.</summary>
    /// <param name="onSettled">Receives the state and the value or reason.</param>
    public void OnSettled(Action<JsPromiseState, JsValue> onSettled)
    {
        ArgumentNullException.ThrowIfNull(onSettled);
        _realm.Run(() =>
        {
            Promises.ThenNative(_promise, v => onSettled(JsPromiseState.Fulfilled, v), r => onSettled(JsPromiseState.Rejected, r));
            return default(JsValue);
        });
    }
}

/// <summary>
/// A realm: a global object with its own set of built-ins, owned by a <see cref="ScriptEngine"/>. This is the low-level host API:
/// create values, define properties and functions, call into script, make promises. Every operation here may throw
/// <see cref="JsException"/> when script code it triggers (a getter, a proxy trap) throws. Call these on the engine's thread, either
/// inside a host callback or from the code that owns the engine.
/// </summary>
public sealed class JsRealm
{
    private readonly Realm _realm;
    private readonly Dictionary<Type, JsHostTypeState> _hostTypes = [];
    private readonly ConditionalWeakTable<object, JsHostObject> _wrappers = new();

    internal JsRealm(ScriptEngine engine, Realm realm)
    {
        Engine = engine;
        _realm = realm;
    }

    internal Realm Inner => _realm;

    /// <summary>The engine this realm belongs to.</summary>
    public ScriptEngine Engine { get; }

    /// <summary>The global object.</summary>
    public JsObject Global => _realm.Global;

    internal T Run<T>(Func<T> body) => Engine.Guard(body, default, _realm);

    /// <summary>Runs a classic script in this realm, then drains the job queue (see <see cref="ScriptEngine.Evaluate"/>).</summary>
    /// <param name="source">The script text.</param>
    /// <param name="fileName">The name reports and <c>err.stack</c> use.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The completion value.</returns>
    /// <exception cref="JsException">The script has a syntax error or threw.</exception>
    /// <exception cref="JsLimitException">A resource limit ended the run.</exception>
    public JsValue Evaluate(string source, string fileName = "<anonymous>", CancellationToken cancellationToken = default)
        => Engine.EvaluateIn(_realm, source, fileName, cancellationToken);

    // ---- creating values ------------------------------------------------------------------------

    /// <summary>Creates an empty plain object (prototype <c>Object.prototype</c>).</summary>
    public JsObject NewObject() => _realm.NewObject();

    /// <summary>Creates an empty object with the given prototype.</summary>
    /// <param name="prototype">The prototype, or <see langword="null"/> for none.</param>
    public JsObject NewObject(JsObject? prototype)
    {
        Engine.ChargeAllocation(64);
        return new JsObject(prototype);
    }

    /// <summary>Creates an array holding <paramref name="items"/>.</summary>
    /// <param name="items">The elements, in order.</param>
    public JsObject NewArray(IEnumerable<JsValue> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var list = items as IReadOnlyList<JsValue> ?? new List<JsValue>(items);
        Engine.ChargeAllocation(16L * (list.Count + 4));
        return JsArray.FromList(_realm.ArrayPrototype, list);
    }

    /// <summary>Creates a new symbol (not in the global registry).</summary>
    /// <param name="description">The description; may be <see langword="null"/>.</param>
    public static JsValue NewSymbol(string? description) => JsValue.FromSymbol(new JsSymbol(description));

    /// <summary>Returns a well-known symbol such as <c>iterator</c>, <c>asyncIterator</c>, <c>toStringTag</c> or <c>hasInstance</c>.</summary>
    /// <param name="name">The name without the <c>Symbol.</c> prefix.</param>
    /// <exception cref="ArgumentException">There is no such well-known symbol.</exception>
    public JsValue WellKnownSymbol(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        JsValue symbol = _realm.Global.Get("Symbol") is { Ref: JsObject sym } ? sym.Get(name) : default;
        return symbol.IsSymbol ? symbol : throw new ArgumentException("There is no well-known symbol named '" + name + "'.", nameof(name));
    }

    /// <summary>Creates an error object of the given kind with the V8 <c>stack</c> text. Throw it with <c>throw new JsException(value)</c>.</summary>
    /// <param name="kind">The constructor name: <c>Error</c>, <c>TypeError</c>, <c>RangeError</c>, <c>SyntaxError</c>, <c>ReferenceError</c>, <c>EvalError</c> or <c>URIError</c>.</param>
    /// <param name="message">The message.</param>
    public JsValue NewError(string kind, string message)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(message);
        return Run(() => JsValue.FromObject(_realm.NewError(kind, message)));
    }

    /// <summary>Creates a function backed by a delegate. A .NET exception it throws reaches script as an <c>Error</c> naming the .NET type.</summary>
    /// <param name="name">The function's <c>name</c>.</param>
    /// <param name="length">The function's <c>length</c>.</param>
    /// <param name="function">The implementation.</param>
    public JsObject NewFunction(string name, int length, HostFunction function)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(function);
        return _realm.Fn(name, length, HostCalls.Wrap(function, name));
    }

    /// <summary>
    /// Creates a constructor function with a fresh prototype object (not installed anywhere; use <see cref="DefineData(JsObject, string, JsValue, JsAttributes)"/>
    /// to publish it). Calling it without <c>new</c> throws a TypeError. A script class can extend it.
    /// </summary>
    /// <param name="name">The constructor's name.</param>
    /// <param name="length">The constructor's <c>length</c>.</param>
    /// <param name="construct">Builds the new object.</param>
    /// <param name="prototype">Receives the prototype object; define the instance members on it.</param>
    /// <param name="basePrototype">The prototype's prototype; <see langword="null"/> means <c>Object.prototype</c>.</param>
    public JsObject NewConstructor(string name, int length, HostConstructor construct, out JsObject prototype, JsObject? basePrototype = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(construct);
        JsObject proto = new(basePrototype ?? _realm.ObjectPrototype);
        var f = new NativeFunction(_realm, name, length, (t, a, nt) =>
        {
            if (nt is null)
            {
                throw Errors.Type("Class constructor " + name + " cannot be invoked without 'new'");
            }

            try
            {
                return construct(a, nt);
            }
            catch (Exception e) when (e is not (JsException or JsLimitException or OperationCanceledException))
            {
                throw HostErrors.FromException(e, name + " constructor");
            }
        }, true);
        f.DefineDirect("prototype", JsValue.FromObject(proto), PropFlags.None);
        proto.DefineDirect("constructor", JsValue.FromObject(f), PropFlags.Hidden);
        prototype = proto;
        return f;
    }

    /// <summary>The prototype a constructor's <paramref name="newTarget"/> asks for (its <c>prototype</c> property), or <paramref name="fallback"/>.</summary>
    /// <param name="newTarget">The <c>newTarget</c> given to a <see cref="HostConstructor"/>.</param>
    /// <param name="fallback">Used when <c>newTarget.prototype</c> is not an object.</param>
    public static JsObject PrototypeFor(JsObject newTarget, JsObject fallback)
    {
        ArgumentNullException.ThrowIfNull(newTarget);
        ArgumentNullException.ThrowIfNull(fallback);
        return Ops.GetPrototypeFromConstructor(newTarget, _ => fallback);
    }

    /// <summary>Creates a script object standing for <paramref name="target"/>, with the given prototype. The object is remembered as the one <see cref="Wrap"/> returns for this target.</summary>
    /// <param name="target">The .NET object.</param>
    /// <param name="prototype">The prototype that exposes its members.</param>
    public JsHostObject NewHostObject(object target, JsObject prototype)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(prototype);
        Engine.ChargeAllocation(64);
        var wrapper = new JsHostObject(prototype, target);
        _wrappers.AddOrUpdate(target, wrapper);
        return wrapper;
    }

    /// <summary>Creates a pending promise that C# settles with <see cref="JsPromise.Resolve"/> or <see cref="JsPromise.Reject"/>.</summary>
    public JsPromise NewPromise() => new(this, Promises.NewPromise(_realm));

    /// <summary>Returns the promise a value is, or <see langword="null"/> if it is not a promise.</summary>
    /// <param name="value">Any value.</param>
    public JsPromise? AsPromise(JsValue value) => value.Ref is PromiseObject p ? new JsPromise(this, p) : null;

    // ---- inspecting values -------------------------------------------------------------------------

    /// <summary>Whether the value can be called.</summary>
    public static bool IsCallable(JsValue value) => Ops.IsCallable(value);

    /// <summary>Whether the value is an array (<c>Array.isArray</c>, so a proxy of an array counts).</summary>
    public bool IsArray(JsValue value) => Run(() => Ops.IsArray(value));

    /// <summary>Converts a value to a string the way <c>String(value)</c> does; symbols give <c>Symbol(description)</c>.</summary>
    /// <exception cref="JsException">A <c>toString</c> or <c>valueOf</c> threw.</exception>
    public string ToDisplayString(JsValue value) => Engine.ToDisplayString(value);

    /// <summary>Converts a value to a number the way <c>Number(value)</c> does.</summary>
    /// <exception cref="JsException">A <c>valueOf</c> threw, or the value is a symbol or BigInt.</exception>
    public double ToNumber(JsValue value) => Run(() => Ops.ToNumber(value));

    /// <summary>Converts a value to a boolean (JavaScript truthiness).</summary>
    public static bool ToBoolean(JsValue value) => Ops.ToBoolean(value);

    /// <summary>A short description of a value for messages: <c>number 5</c>, <c>string "abc"</c>, <c>an array of 3 items</c>, <c>an object with keys id, total</c>.</summary>
    public string Describe(JsValue value) => Run(() => JsMarshal.Describe(value));

    // ---- properties ---------------------------------------------------------------------------------

    /// <summary>Reads a property (running getters).</summary>
    /// <param name="target">The object.</param>
    /// <param name="name">The property name.</param>
    public JsValue Get(JsObject target, string name)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(name);
        return Run(() => target.Get(name));
    }

    /// <summary>Reads a property by key (a string or symbol value).</summary>
    public JsValue Get(JsObject target, JsValue key)
    {
        ArgumentNullException.ThrowIfNull(target);
        return Run(() => target.Get(Ops.ToPropertyKey(key)));
    }

    /// <summary>Writes a property with sloppy-mode semantics (a refused write is ignored); returns whether it took effect.</summary>
    public bool Set(JsObject target, string name, JsValue value)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(name);
        return Run(() => target.Set(name, value, JsValue.FromObject(target)));
    }

    /// <summary>Whether the object or its prototype chain has the property.</summary>
    public bool Has(JsObject target, string name)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(name);
        return Run(() => target.HasProperty(name));
    }

    /// <summary>Deletes an own property; returns false if it is not configurable.</summary>
    public bool Delete(JsObject target, string name)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(name);
        return Run(() => target.Delete(name));
    }

    /// <summary>Defines (or redefines) an own data property.</summary>
    /// <param name="target">The object.</param>
    /// <param name="name">The property name.</param>
    /// <param name="value">The value.</param>
    /// <param name="attributes">Attributes; default <see cref="JsAttributes.All"/>.</param>
    /// <exception cref="JsException">The property exists and is not configurable.</exception>
    public void DefineData(JsObject target, string name, JsValue value, JsAttributes attributes = JsAttributes.All)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(name);
        DefineData(target, JsValue.FromString(name), value, attributes);
    }

    /// <summary>Defines an own data property by key (a string or symbol value).</summary>
    public void DefineData(JsObject target, JsValue key, JsValue value, JsAttributes attributes = JsAttributes.All)
    {
        ArgumentNullException.ThrowIfNull(target);
        Run(() =>
        {
            target.DefinePropertyOrThrow(Ops.ToPropertyKey(key), PropertyDescriptor.Data(value, ToFlags(attributes)));
            return default(JsValue);
        });
    }

    /// <summary>Defines an own accessor property.</summary>
    /// <param name="target">The object.</param>
    /// <param name="key">The property key: a string or symbol value.</param>
    /// <param name="getter">The getter function (from <see cref="NewFunction"/>), or <see langword="null"/>.</param>
    /// <param name="setter">The setter function, or <see langword="null"/>.</param>
    /// <param name="attributes">Only <see cref="JsAttributes.Enumerable"/> and <see cref="JsAttributes.Configurable"/> apply. Default: configurable.</param>
    public void DefineAccessor(JsObject target, JsValue key, JsObject? getter, JsObject? setter, JsAttributes attributes = JsAttributes.Configurable)
    {
        ArgumentNullException.ThrowIfNull(target);
        Run(() =>
        {
            PropFlags f = ToFlags(attributes) & ~PropFlags.Writable;
            target.DefinePropertyOrThrow(Ops.ToPropertyKey(key), PropertyDescriptor.Accessor(getter is null ? default : JsValue.FromObject(getter), setter is null ? default : JsValue.FromObject(setter), f));
            return default(JsValue);
        });
    }

    /// <summary>Defines an own accessor property by name.</summary>
    public void DefineAccessor(JsObject target, string name, JsObject? getter, JsObject? setter, JsAttributes attributes = JsAttributes.Configurable)
        => DefineAccessor(target, JsValue.FromString(name), getter, setter, attributes);

    /// <summary>Lists own property keys in the spec's order: integer keys ascending, then strings, then (if asked) symbols, in creation order.</summary>
    /// <param name="target">The object (a proxy runs its <c>ownKeys</c> trap).</param>
    /// <param name="onlyEnumerable">Keep only enumerable properties. Default true.</param>
    /// <param name="includeSymbols">Include symbol keys. Default false.</param>
    /// <returns>String and symbol values.</returns>
    public IReadOnlyList<JsValue> OwnKeys(JsObject target, bool onlyEnumerable = true, bool includeSymbols = false)
    {
        ArgumentNullException.ThrowIfNull(target);
        return Run(() =>
        {
            var keys = new List<PropertyKey>();
            target.OwnKeys(keys);
            var result = new List<JsValue>(keys.Count);
            foreach (PropertyKey k in keys)
            {
                if (k.IsSymbol && !includeSymbols)
                {
                    continue;
                }

                if (onlyEnumerable && (!target.GetOwnProperty(k, out PropertyDescriptor d) || !d.Enumerable))
                {
                    continue;
                }

                result.Add(k.ToValue());
            }

            return (IReadOnlyList<JsValue>)result;
        });
    }

    /// <summary>The object's prototype, or <see langword="null"/>.</summary>
    public JsObject? GetPrototypeOf(JsObject target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return Run(() => target.GetPrototypeOf());
    }

    // ---- calling ----------------------------------------------------------------------------------------

    /// <summary>Calls a function value. Must be called on the engine's thread.</summary>
    /// <param name="function">The function; anything else throws a TypeError.</param>
    /// <param name="thisValue">The <c>this</c> value.</param>
    /// <param name="args">The arguments.</param>
    /// <exception cref="JsException">The function threw.</exception>
    /// <exception cref="InvalidOperationException">Called from a thread that is not running this engine.</exception>
    public JsValue Call(JsValue function, JsValue thisValue, params ReadOnlySpan<JsValue> args)
    {
        JsValue[] copy = args.ToArray();
        return Run(() => Ops.Call(function, thisValue, copy));
    }

    /// <summary>Calls a constructor with <c>new</c>.</summary>
    /// <param name="constructor">The constructor.</param>
    /// <param name="args">The arguments.</param>
    /// <exception cref="JsException">Not a constructor, or it threw.</exception>
    public JsValue Construct(JsValue constructor, params ReadOnlySpan<JsValue> args)
    {
        JsValue[] copy = args.ToArray();
        return Run(() => constructor.Ref is JsObject c && c.IsConstructor
            ? JsValue.FromObject(Ops.Construct(c, copy))
            : throw Errors.Type(Ops.DisplayValue(constructor) + " is not a constructor"));
    }

    // ---- .NET objects in script ---------------------------------------------------------------------

    /// <summary>
    /// Returns the script object for a .NET object whose class was installed with <see cref="Install"/>: the same object every time
    /// for the same target, with the members the class exports.
    /// </summary>
    /// <param name="target">The .NET object; <see langword="null"/> gives <c>null</c>.</param>
    /// <exception cref="InvalidOperationException">The object's class (or a base class or interface of it) was not installed in this realm.</exception>
    public JsValue Wrap(object? target)
    {
        if (target is null)
        {
            return JsValue.Null;
        }

        if (_wrappers.TryGetValue(target, out JsHostObject? existing))
        {
            return JsValue.FromObject(existing);
        }

        JsObject? proto = null;
        for (Type? t = target.GetType(); t is not null && proto is null; t = t.BaseType)
        {
            proto = _hostTypes.TryGetValue(t, out JsHostTypeState? st) ? st.Prototype : null;
        }

        if (proto is null)
        {
            foreach (KeyValuePair<Type, JsHostTypeState> kv in _hostTypes)
            {
                if (kv.Key.IsInterface && kv.Key.IsInstanceOfType(target))
                {
                    proto = kv.Value.Prototype;
                    break;
                }
            }
        }

        if (proto is null)
        {
            throw new InvalidOperationException("The .NET type " + target.GetType().FullName + " has not been installed in this realm. Mark it [JsExport] and call the generated JsExports.InstallAll(realm), or install its JsHostType.");
        }

        return JsValue.FromObject(NewHostObject(target, proto));
    }

    /// <summary>Installs an exported .NET type: creates its constructor and prototype, defines the class as a global, and returns its prototype. Does nothing if already installed here.</summary>
    /// <param name="type">The type, from a generated binding's <c>JsType</c> field.</param>
    /// <returns>The prototype object of the type's instances (the constructor object for a static class).</returns>
    public JsObject Install(JsHostType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (_hostTypes.TryGetValue(type.ClrType, out JsHostTypeState? st))
        {
            return st.Prototype;
        }

        // Register before running the installer so types that refer to each other do not recurse.
        st = new JsHostTypeState();
        _hostTypes[type.ClrType] = st;
        st.Prototype = Run(() => type.InstallCore(this, st));
        return st.Prototype;
    }

    /// <summary>Whether the type is installed in this realm.</summary>
    public bool IsInstalled(JsHostType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return _hostTypes.ContainsKey(type.ClrType);
    }

    private static PropFlags ToFlags(JsAttributes a)
    {
        PropFlags f = PropFlags.None;
        if ((a & JsAttributes.Writable) != 0)
        {
            f |= PropFlags.Writable;
        }

        if ((a & JsAttributes.Enumerable) != 0)
        {
            f |= PropFlags.Enumerable;
        }

        if ((a & JsAttributes.Configurable) != 0)
        {
            f |= PropFlags.Configurable;
        }

        return f;
    }

    internal static JsValue FromBigInt(BigInteger b) => JsValue.FromBigInt(b);
}

internal sealed class JsHostTypeState
{
    internal JsObject Prototype = null!;
}

/// <summary>
/// An exported .NET type, as generated by <c>[JsExport]</c>. Generated code creates one per type; install it into a realm with
/// <see cref="JsRealm.Install"/>.
/// </summary>
public sealed class JsHostType
{
    private readonly Func<JsRealm, JsObject> _install;

    /// <summary>Creates a descriptor. Generated code calls this.</summary>
    /// <param name="clrType">The .NET type.</param>
    /// <param name="jsName">The global name in script.</param>
    /// <param name="install">Builds the constructor, prototype and members in the given realm and returns the prototype.</param>
    public JsHostType(Type clrType, string jsName, Func<JsRealm, JsObject> install)
    {
        ClrType = clrType;
        JsName = jsName;
        _install = install;
    }

    /// <summary>The .NET type.</summary>
    public Type ClrType { get; }

    /// <summary>The name of the class in script.</summary>
    public string JsName { get; }

    internal JsObject InstallCore(JsRealm realm, JsHostTypeState state) => _install(realm);
}
