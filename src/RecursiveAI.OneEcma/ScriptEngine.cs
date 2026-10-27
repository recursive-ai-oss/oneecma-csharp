using RecursiveAI.OneEcma.Diagnostics;
using RecursiveAI.OneEcma.Syntax;

namespace RecursiveAI.OneEcma;

/// <summary>A host function callable from script: receives <c>this</c> and the arguments, returns the result.</summary>
/// <param name="thisValue">The <c>this</c> value of the call.</param>
/// <param name="args">The arguments; read-only and valid only during the call.</param>
/// <returns>The call result; <see cref="JsValue.Undefined"/> for none.</returns>
public delegate JsValue HostFunction(JsValue thisValue, ReadOnlySpan<JsValue> args);

/// <summary>Settings for a <see cref="ScriptEngine"/>.</summary>
public sealed class ScriptEngineOptions
{
    /// <summary>The host the engine reaches the outside world through. Default: <see cref="DefaultHost"/> (system clock, no modules).</summary>
    public IJsHost? Host { get; set; }

    /// <summary>Resource limits. Default: <see cref="EngineLimits"/> defaults.</summary>
    public EngineLimits? Limits { get; set; }

    /// <summary>
    /// When true (the default, as in Node) a promise that is rejected and never handled by the time the job queue is empty ends the
    /// run with a <see cref="JsException"/> carrying the rejection reason; when false such rejections are ignored.
    /// </summary>
    public bool ThrowOnUnhandledRejection { get; set; } = true;

    /// <summary>When true the global object gets the test-harness additions <c>print</c> and <c>$262</c>. Default false.</summary>
    public bool InstallTest262Host { get; set; }
}

/// <summary>
/// An ECMAScript engine instance: one agent with one realm, a job queue and resource limits. Instances are not thread-safe;
/// use one per thread. Errors leave as <see cref="JsException"/> or <see cref="JsLimitException"/>, each carrying a
/// <see cref="Diagnostic"/> in <c>Report</c>.
/// </summary>
public sealed class ScriptEngine
{
    /// <summary>The library version, as a constant so reading it needs no reflection.</summary>
    public const string Version = "0.0.0-dev";

    private readonly Agent _agent;
    private readonly bool _throwOnUnhandled;
    private readonly Realm _realm;

    /// <summary>Creates an engine with the default host and limits.</summary>
    public ScriptEngine()
        : this(null)
    {
    }

    /// <summary>Creates an engine.</summary>
    /// <param name="options">Host, limits and switches; <see langword="null"/> means all defaults.</param>
    public ScriptEngine(ScriptEngineOptions? options)
    {
        _agent = new Agent(options?.Host ?? new DefaultHost(), options?.Limits ?? new EngineLimits()) { InstallTest262Host = options?.InstallTest262Host ?? false };
        _throwOnUnhandled = options?.ThrowOnUnhandledRejection ?? true;
        Agent? old = Agent.Enter(_agent);
        try
        {
            _realm = Realm.Create(_agent);
            _agent.Realm = _realm;
        }
        finally
        {
            Agent.Leave(old);
        }
    }

    /// <summary>The global object of the engine's realm.</summary>
    public JsObject Global => _realm.Global;

    /// <summary>Runs a classic script, then drains the job queue (promise reactions, async continuations).</summary>
    /// <param name="source">The script text.</param>
    /// <param name="fileName">The name reports and <c>err.stack</c> use for the script.</param>
    /// <param name="cancellationToken">Cancels the run; the call then throws <see cref="OperationCanceledException"/>.</param>
    /// <returns>The completion value of the script.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsException">The script has a syntax error or threw; <see cref="JsException.Report"/> explains it.</exception>
    /// <exception cref="JsLimitException">A resource limit ended the run.</exception>
    public JsValue Evaluate(string source, string fileName = "<anonymous>", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Guard(() =>
        {
            ParseResult pr = JsParser.Parse(source, new ParseOptions { FileName = fileName });
            if (pr.HasErrors)
            {
                throw SyntaxFailure(pr.Diagnostics[0]);
            }

            FunctionCode code = Compiler.CompileScript(pr.Program, pr.Source);
            var frame = new Frame(code, _realm) { Env = null, This = JsValue.FromObject(_realm.Global) };
            JsValue v = Vm.RunEntry(_agent, frame);
            DrainJobs();
            return v;
        }, cancellationToken);
    }

    /// <summary>Loads, links and evaluates the module the host resolves for <paramref name="specifier"/>, then drains the job queue.</summary>
    /// <param name="specifier">The module specifier, resolved through <see cref="IJsHost.ResolveModule"/> with no referrer.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The module namespace object.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="specifier"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsException">The module is missing, has an error, or its evaluation threw.</exception>
    /// <exception cref="JsLimitException">A resource limit ended the run.</exception>
    public JsValue EvaluateModule(string specifier, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specifier);
        return Guard(() =>
        {
            HostModule hm = _agent.Host.ResolveModule(specifier, null, new Dictionary<string, string>())
                ?? throw Errors.Make("Error", "Cannot find module '" + specifier + "'");
            ModuleRecord m = ModuleLoader.Load(_realm, hm);
            ModuleLoader.Link(m);
            PromiseObject p = ModuleLoader.Evaluate(m);
            _agent.RunJobs();
            if (p.State == 2)
            {
                p.IsHandled = true;
                throw new JsException(p.Result);
            }

            if (p.State == 0)
            {
                throw Errors.Make("Error", "Module evaluation did not finish: it is waiting on a promise that never settles");
            }

            return JsValue.FromObject(ModuleLoader.GetNamespace(m));
        }, cancellationToken);
    }

    /// <summary>Runs queued promise jobs until the queue is empty.</summary>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <exception cref="JsException">A job threw outside any promise.</exception>
    /// <exception cref="JsLimitException">A resource limit ended the run.</exception>
    public void RunJobs(CancellationToken cancellationToken = default) => Guard(() =>
    {
        DrainJobs();
        return default(JsValue);
    }, cancellationToken);

    private void DrainJobs()
    {
        _agent.RunJobs();
        List<PromiseObject> pending = _agent.UnhandledRejections;
        PromiseObject? first = pending.Find(static p => !p.IsHandled);
        pending.Clear();
        if (first is not null && _throwOnUnhandled)
        {
            first.IsHandled = true;
            throw new JsException(first.Result);
        }
    }

    /// <summary>Calls a function value.</summary>
    /// <param name="function">The function; anything else throws a TypeError.</param>
    /// <param name="thisValue">The <c>this</c> value.</param>
    /// <param name="args">The arguments.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The function result.</returns>
    /// <exception cref="JsException">The function threw.</exception>
    /// <exception cref="JsLimitException">A resource limit ended the run.</exception>
    public JsValue Call(JsValue function, JsValue thisValue, ReadOnlySpan<JsValue> args = default, CancellationToken cancellationToken = default)
    {
        JsValue[] copy = args.ToArray();
        return Guard(() => Ops.Call(function, thisValue, copy), cancellationToken);
    }

    /// <summary>Reads a property of an object (a property get, so getters run).</summary>
    /// <param name="target">The object.</param>
    /// <param name="name">The property name.</param>
    /// <returns>The property value, or undefined.</returns>
    /// <exception cref="JsException">A getter threw.</exception>
    public JsValue GetProperty(JsObject target, string name)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(name);
        return Guard(() => target.Get(name), default);
    }

    /// <summary>Writes a property of an object (a property set, sloppy semantics: a refused write is ignored).</summary>
    /// <param name="target">The object.</param>
    /// <param name="name">The property name.</param>
    /// <param name="value">The value.</param>
    /// <exception cref="JsException">A setter threw.</exception>
    public void SetProperty(JsObject target, string name, JsValue value)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(name);
        Guard(() =>
        {
            target.Set(name, value, JsValue.FromObject(target));
            return default(JsValue);
        }, default);
    }

    /// <summary>Defines a global function backed by a host callback. A .NET exception thrown by the callback reaches script as an <c>Error</c> naming the .NET type.</summary>
    /// <param name="name">The global name.</param>
    /// <param name="length">The function's <c>length</c>.</param>
    /// <param name="function">The callback.</param>
    public void DefineGlobalFunction(string name, int length, HostFunction function)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(function);
        var f = _realm.Fn(name, length, (t, a, nt) =>
        {
            try
            {
                return function(t, a);
            }
            catch (Exception e) when (e is not (JsException or JsLimitException or OperationCanceledException))
            {
                throw Errors.Make("Error", e.GetType().Name + ": " + e.Message);
            }
        });
        _realm.Global.DefineDirect(name, JsValue.FromObject(f), PropFlags.Hidden);
    }

    /// <summary>Converts a value to its <c>String(value)</c> text (symbols print as <c>Symbol(desc)</c>; conversion errors propagate).</summary>
    /// <param name="value">The value.</param>
    /// <returns>The text.</returns>
    /// <exception cref="JsException">A <c>toString</c> or <c>valueOf</c> threw.</exception>
    public string ToDisplayString(JsValue value) => Guard(() => JsValue.FromString(value.IsSymbol ? ((JsSymbol)value.Ref!).ToString() : Ops.ToStr(value)), default).AsString;

    private JsException SyntaxFailure(Diagnostic d)
    {
        Agent? old = Agent.Enter(_agent);
        try
        {
            var ex = Errors.Syntax(d.Headline);
            ex.Report = d;
            return ex;
        }
        finally
        {
            Agent.Leave(old);
        }
    }

    private int _active;

    private T Guard<T>(Func<T> body, CancellationToken token)
    {
        if (_active > 0)
        {
            // Re-entered from a host callback: the outer call owns the agent state, limits and error reporting.
            return body();
        }

        _active++;
        Agent? old = Agent.Enter(_agent);
        _agent.SetCancellation(token);
        _agent.Frame = null;
        _agent.CallDepth = 0;
        _agent.NativeDepth = 0;
        _agent.Realm = _realm;
        try
        {
            return body();
        }
        catch (JsException e)
        {
            e.Report ??= ErrorReport.ForException(e);
            throw;
        }
        catch (JsLimitException e) when (e.Kind == LimitKind.Cancelled)
        {
            throw new OperationCanceledException(e.Message, e, token);
        }
        catch (JsLimitException e)
        {
            e.Report ??= ErrorReport.ForLimit(e);
            throw;
        }
        catch (Exception e) when (e is not (OperationCanceledException or ArgumentException))
        {
            throw new InvalidOperationException("Internal engine error: " + e.GetType().Name + ": " + e.Message, e);
        }
        finally
        {
            _active--;
            _agent.SetCancellation(default);
            Agent.Leave(old);
        }
    }

    /// <summary>Parses a script and reports its syntax and early errors without running it.</summary>
    /// <param name="source">The script text.</param>
    /// <param name="options">Parse options; <see langword="null"/> means a sloppy classic script named <c>&lt;anonymous&gt;</c>.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>Every error found; an empty list when the source is valid.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public static IReadOnlyList<Diagnostic> Check(string source, ParseOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return JsParser.Parse(source, options, cancellationToken).Diagnostics;
    }

    /// <summary>
    /// Like <see cref="Check"/>, but when the source does not parse it tries the syntax-only repairs of the repair table. Off unless
    /// called; see <see cref="SyntaxRepair.Repair(string, ParseOptions?, RepairOptions?, CancellationToken)"/> for the rules.
    /// </summary>
    /// <param name="source">The script text.</param>
    /// <param name="options">Parse options; <see langword="null"/> means a sloppy classic script named <c>&lt;anonymous&gt;</c>.</param>
    /// <param name="repair">Repair limits; <see langword="null"/> means the defaults.</param>
    /// <param name="cancellationToken">Cancels the repair.</param>
    /// <returns>The repaired source, the repairs applied first-to-last and the diagnostics that remain.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public static RepairResult CheckAndRepair(string source, ParseOptions? options = null, RepairOptions? repair = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return SyntaxRepair.Repair(source, options, repair, cancellationToken);
    }
}
