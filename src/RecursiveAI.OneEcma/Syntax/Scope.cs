using RecursiveAI.OneEcma.Text;

namespace RecursiveAI.OneEcma.Syntax;

/// <summary>The kind of a <see cref="Scope"/>.</summary>
public enum ScopeKind
{
    /// <summary>Script top level (ECMA-262 16.1.7 GlobalDeclarationInstantiation).</summary>
    Global,

    /// <summary>Module top level.</summary>
    Module,

    /// <summary>Eval code top level.</summary>
    Eval,

    /// <summary>A function: parameters and the top-level declarations of the body.</summary>
    Function,

    /// <summary>A block, or the case block of a switch.</summary>
    Block,

    /// <summary>A <c>catch</c> clause: the parameter and the declarations of its block.</summary>
    Catch,

    /// <summary>The loop head of a <c>for</c> statement with a lexical declaration.</summary>
    For,

    /// <summary>The case block of a <c>switch</c>.</summary>
    Switch,

    /// <summary>A class: the inner name binding and its private names.</summary>
    Class,

    /// <summary>A class static initialisation block (a var scope like a function body).</summary>
    ClassStaticBlock,

    /// <summary>The initializer of a class field (behaves as a method body).</summary>
    ClassFieldInitializer,
}

/// <summary>What introduced a <see cref="Binding"/>.</summary>
public enum BindingKind
{
    /// <summary><c>var</c>, or a hoisted function-level name.</summary>
    Var,

    /// <summary><c>let</c>.</summary>
    Let,

    /// <summary><c>const</c>.</summary>
    Const,

    /// <summary>A class declaration or the inner name binding of a class.</summary>
    Class,

    /// <summary>A function declaration at the top level of a function, script or eval (var-like).</summary>
    Function,

    /// <summary>A function declaration in a block or at module top level (let-like).</summary>
    LexicalFunction,

    /// <summary>A formal parameter.</summary>
    Parameter,

    /// <summary>A catch parameter.</summary>
    CatchParameter,

    /// <summary>An import binding.</summary>
    Import,

    /// <summary>The var binding that B.3.2 adds for a function declared in a sloppy-mode block.</summary>
    AnnexBFunctionVar,
}

/// <summary>A name declared in a scope.</summary>
public sealed class Binding
{
    internal Binding(string name, BindingKind kind, Span span, Scope scope)
    {
        Name = name;
        Kind = kind;
        Span = span;
        Scope = scope;
    }

    /// <summary>The declared name.</summary>
    public string Name { get; }

    /// <summary>What declared it.</summary>
    public BindingKind Kind { get; internal set; }

    /// <summary>The span of the first declaring identifier.</summary>
    public Span Span { get; }

    /// <summary>The scope holding the binding.</summary>
    public Scope Scope { get; }

    /// <summary>For <see cref="BindingKind.Function"/> and <see cref="BindingKind.LexicalFunction"/>: the declaration to instantiate (the last one wins for duplicates).</summary>
    public FunctionDeclaration? FunctionDeclaration { get; internal set; }

    /// <summary>True for <c>let</c>, <c>const</c>, class, lexical function and import bindings (those with a temporal dead zone or block scope).</summary>
    public bool IsLexical => Kind is BindingKind.Let or BindingKind.Const or BindingKind.Class or BindingKind.LexicalFunction or BindingKind.Import;
}

/// <summary>
/// Static scope information recorded during parsing: the names each scope declares, in source order,
/// and flags the executor needs to decide how to represent the scope. Resolution of references is left
/// to the consumer, which can walk <see cref="Parent"/>.
/// </summary>
public sealed class Scope
{
    private readonly List<Binding> _bindings = [];
    private Dictionary<string, Binding>? _map;

    internal Scope(ScopeKind kind, Scope? parent, int start)
    {
        Kind = kind;
        Parent = parent;
        Start = start;
    }

    /// <summary>The kind.</summary>
    public ScopeKind Kind { get; }

    /// <summary>The enclosing scope, or null at the top.</summary>
    public Scope? Parent { get; internal set; }

    /// <summary>Scopes directly nested in this one, in source order.</summary>
    public List<Scope> Children { get; } = [];

    /// <summary>Offset where the scope's construct starts.</summary>
    public int Start { get; }

    /// <summary>True when the code in this scope is strict mode code.</summary>
    public bool IsStrict { get; internal set; }

    /// <summary>The names declared directly in this scope, in declaration order.</summary>
    public IReadOnlyList<Binding> Bindings => _bindings;

    /// <summary>True when this scope or a scope nested in it (not through a non-arrow function boundary... any nested scope) contains a direct <c>eval</c> call, so names may be added or observed dynamically.</summary>
    public bool ContainsDirectEval { get; internal set; }

    /// <summary>True when a <c>with</c> statement encloses code in this scope or lies in it.</summary>
    public bool ContainsWith { get; internal set; }

    /// <summary>Private names declared by a class scope (without <c>#</c>).</summary>
    public IReadOnlyCollection<string> PrivateNames => (IReadOnlyCollection<string>?)_privateNames?.Keys ?? [];

    /// <summary>True when the scope holds var declarations (a function, script, module, eval or static block scope).</summary>
    public bool IsVarScope => Kind is ScopeKind.Global or ScopeKind.Module or ScopeKind.Eval or ScopeKind.Function or ScopeKind.ClassStaticBlock or ScopeKind.ClassFieldInitializer;

    /// <summary>Looks up a name declared directly in this scope.</summary>
    public Binding? Lookup(string name)
    {
        if (_map is not null)
        {
            return _map.GetValueOrDefault(name);
        }

        foreach (Binding b in _bindings)
        {
            if (b.Name == name)
            {
                return b;
            }
        }

        return null;
    }

    // ---- parser-internal state -------------------------------------------------------------

    internal Dictionary<string, PrivateKind>? _privateNames;

    /// <summary>Names var-declared in a nested block that passed through this (non-var) scope.</summary>
    internal HashSet<string>? VarThrough;

    /// <summary>Sloppy-mode block-level function declarations to consider for B.3.2 hoisting when the var scope closes.</summary>
    internal List<(FunctionDeclaration Decl, Scope Block, string Name, Span Span)>? HoistCandidates;

    /// <summary>For Catch scopes: true when the parameter is a plain identifier (B.3.4 allows <c>var</c> with the same name).</summary>
    internal bool SimpleCatchParameter;

    /// <summary>For function scopes: names of parameters, for the B.3.2 exclusion.</summary>
    internal bool HasParameterExpressions;

    /// <summary>The scope the unit of Annex B check should stop at.</summary>
    internal bool IsFunctionBodyTop => Kind == ScopeKind.Function;

    internal Binding Add(string name, BindingKind kind, Span span)
    {
        var b = new Binding(name, kind, span, this);
        _bindings.Add(b);
        if (_map is not null)
        {
            _map[name] = b;
        }
        else if (_bindings.Count > 8)
        {
            _map = new Dictionary<string, Binding>(StringComparer.Ordinal);
            foreach (Binding x in _bindings)
            {
                _map[x.Name] = x;
            }
        }

        return b;
    }
}

internal enum PrivateKind : byte
{
    Field,
    Method,
    Getter,
    Setter,
    StaticField,
    StaticMethod,
    StaticGetter,
    StaticSetter,
}
