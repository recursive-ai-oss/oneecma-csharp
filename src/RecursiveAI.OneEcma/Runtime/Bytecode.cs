using RecursiveAI.OneEcma.Text;

namespace RecursiveAI.OneEcma;

/// <summary>Bytecode operations. Operands are <c>A</c> and <c>B</c>; the operand stack is per frame.</summary>
internal enum Op : byte
{
    Nop,

    // constants and stack
    PushUndef, PushNull, PushTrue, PushFalse, PushInt, PushConst, Pop, Dup, Dup2, Swap, Rot3, Rot4, DupUnder,

    // temporaries kept in the frame below the operand stack
    LoadLocal, StoreLocal, SetLocal,

    // bindings
    LoadSlot, LoadSlotChecked, StoreSlot, StoreSlotChecked, InitSlot, ThrowConstAssign, LoadImport,
    LoadGlobal, StoreGlobal, TypeofGlobal, DeclareGlobals, InitGlobalLexical, DefineGlobalFunction,
    LoadDyn, StoreDyn, StoreVarDyn, ResolveDynRef, GetDynRef, PutDynRef, TypeofDyn, DeleteDyn, LoadDynCall, InitDyn, DeclareEvalVar,
    PushScope, PopScope, CopyScope, PushWith, LoadArg, CreateArguments, CollectRest,
    LoadThis, LoadThisChecked, LoadNewTarget, LoadFunction,

    // properties
    GetProp, SetProp, GetElem, SetElem, GetMethod, GetElemMethod, DeleteProp, DeleteElem,
    GetSuper, GetSuperElem, SetSuper, SetSuperElem, ToPropKey,
    PrivateGet, PrivateSet, PrivateIn,
    In, InstanceOf,

    // literals and classes
    NewObject, NewArray, ArrayPush, ArrayPushHole, ArraySpread, DefineField, DefineFieldComputed, DefineMethodOp,
    CopyDataProps, SetProtoLiteral, NewRegExp, TemplateObject, SetFnNameKey, ObjectRest,
    NewClass, DefineClassField, AddPrivateMethod, RunStaticBlock, NewPrivateName,

    // operators
    Add, Sub, Mul, Div, Mod, Exp, BitAnd, BitOr, BitXor, Shl, Shr, UShr,
    Eq, Ne, StrictEq, StrictNe, Lt, Le, Gt, Ge,
    Neg, Plus, Not, BitNot, Typeof, ToNumeric, Inc, Dec, ToStringOp, RequireObjectCoercible, TemplateConcat,

    // control flow
    Jmp, JmpIfFalse, JmpIfTrue, JmpIfFalseKeep, JmpIfTrueKeep, JmpIfNotNullishKeep, JmpIfNotUndefKeep,
    JmpNullishUndef, JmpNullishUndef2, JmpIfStrictEq, JmpBack,
    TryEnter, TryLeave, Throw, ThrowError, Return, ReturnUndef,

    // calls
    Call, TailCall, TailCallEval, CallSpread, New, NewSpread, SuperCall, SuperCallSpread, CallEval, CallEvalSpread, ImportCallOp, ImportMeta, MakeClosure,

    // iteration
    GetIterator, IterNext, IterClose, IterCloseIfNotDone, IterCloseSilent, IterStep, IterRest,
    ForInStart, ForInNext,
    AsyncIterNextStart, AsyncIterResult, AsyncIterCloseStart,

    // generators and async
    GenStart, Yield, YieldStar, Await,

    // misc
    Debugger, ThrowIfNotObject, CheckObjectCoercibleDestructure, GetAsyncIterator,
}

internal struct Instr
{
    internal Op Op;
    internal int A;
    internal int B;

    public override readonly string ToString() => Op + " " + A + " " + B;
}

/// <summary>One live binding table: names and kinds of a runtime scope, shared by all its instances.</summary>
internal sealed class ScopeInfo
{
    internal string[] Names = [];
    internal byte[] Kinds = [];
    internal JsValue[] Template = [];
    internal bool IsFunctionVarScope;

    /// <summary>The parameter environment of a function with parameter expressions; the body's vars live in a separate scope.</summary>
    internal bool IsParamScope;
    internal bool IsStrict;
    private Dictionary<string, int>? _index;

    internal const byte KVar = 0;
    internal const byte KLet = 1;
    internal const byte KConst = 2;
    internal const byte KFuncName = 3;
    internal const byte KImport = 4;

    internal int IndexOf(string name)
    {
        if (_index is null)
        {
            _index = new Dictionary<string, int>(Names.Length, StringComparer.Ordinal);
            for (int i = 0; i < Names.Length; i++)
            {
                _index[Names[i]] = i;
            }
        }

        return _index.TryGetValue(name, out int v) ? v : -1;
    }
}

/// <summary>A shared mutable binding: a global lexical declaration or a variable introduced by sloppy direct eval.</summary>
internal sealed class Cell
{
    internal JsValue Value;
    internal bool IsConst;
    internal bool Deletable;
}

/// <summary>A runtime environment record: declarative slots, or an object environment for <c>with</c>.</summary>
internal sealed class Env
{
    internal readonly Env? Parent;
    internal readonly JsValue[] Slots;
    internal readonly ScopeInfo? Info;
    internal readonly JsObject? WithObject;
    internal Dictionary<string, Cell>? Extra;

    internal Env(Env? parent, ScopeInfo info)
    {
        Parent = parent;
        Info = info;
        Slots = info.Template.Length == 0 ? [] : (JsValue[])info.Template.Clone();
    }

    internal Env(Env? parent, JsObject withObject)
    {
        Parent = parent;
        WithObject = withObject;
        Slots = [];
    }

    private Env(Env parent, ScopeInfo info, JsValue[] slots)
    {
        Parent = parent.Parent;
        Info = info;
        Slots = slots;
    }

    /// <summary>CreatePerIterationEnvironment (14.7.4.4): a copy with the same parent.</summary>
    internal Env Copy() => new(this, Info!, (JsValue[])Slots.Clone());
}

/// <summary>A binding imported from another module: reads go through to the exporting module's environment.</summary>
internal sealed class ImportRef
{
    internal Env? Env;
    internal int Slot;
    internal ModuleRecord? NsModule;
    internal string Name = "";
}

internal struct Handler
{
    internal int Pc;
    internal int Sp;
    internal Env? Env;
}

internal enum FunctionKind : byte
{
    Normal,
    Arrow,
    Method,
    Getter,
    Setter,
    ClassConstructor,
    DerivedConstructor,
    Script,
    Eval,
    Module,
    FieldInit,
    StaticBlock,
}

/// <summary>Compiled code of one function, script, module or eval body.</summary>
internal sealed class FunctionCode
{
    internal Instr[] Ops = [];
    internal int[] SpanStart = [];
    internal int[] SpanEnd = [];
    internal JsValue[] Consts = [];
    internal string[] Names = [];
    internal FunctionCode[] Functions = [];
    internal ScopeInfo[] Scopes = [];
    internal object[] Tables = [];
    internal PropCache[] Caches = [];
    internal string Name = "";
    internal int Length;
    internal bool Strict;
    internal bool IsAsync;
    internal bool IsGenerator;
    internal FunctionKind Kind;
    internal int NumLocals;
    internal int StackSize;
    internal SourceText Source = null!;
    internal int SrcStart;
    internal int SrcEnd;
    internal ScopeInfo? FuncScope;
    internal bool HasParamExpressions;
    internal bool IsClassMember;
    internal string ModuleId = "";
    internal bool AllowsNewTarget;
    internal bool AllowsSuperProperty;
    internal bool AllowsSuperCall;
    internal bool InFieldInit;

    internal bool IsArrow => Kind == FunctionKind.Arrow;

    internal string SourceText => Source.Text.Substring(SrcStart, SrcEnd - SrcStart);
}

/// <summary>An inline cache for a named property site.</summary>
internal sealed class PropCache
{
    internal Shape? Shape;
    internal int Slot;
    internal Shape? HolderShape;
    internal JsObject? Holder;
    internal Cell? Cell;
}

internal enum ResumeMode : byte
{
    Next,
    Throw,
    Return,
}

internal enum FrameState : byte
{
    Running,
    SuspendedStart,
    SuspendedYield,
    Awaiting,
    Completed,
}

/// <summary>An activation: operand stack, locals, environment and the bookkeeping the interpreter needs to suspend it.</summary>
internal sealed class Frame
{
    internal FunctionCode Code;
    internal ScriptFunction? Func;
    internal Frame? Caller;
    internal Realm Realm;
    internal JsValue[] Stack;
    internal int Sp;
    internal int Pc;
    internal Env? Env;
    internal JsValue This;
    internal JsObject? NewTarget;
    internal Frame ThisFrame;
    internal JsValue[]? ArgArr;
    internal int ArgOff;
    internal int ArgCount;
    internal List<Handler>? Handlers;
    internal bool IsEntry;
    internal bool IsConstruct;
    internal FrameState State;
    internal ResumeMode Mode;
    internal JsValue Sent;
    internal object? Owner;
    internal JsValue Completion;
    internal int Base;
    internal bool AwaitedInStar;
    internal ResumeMode StarMode;
    internal bool RawYield;

    internal Frame(FunctionCode code, Realm realm)
    {
        Code = code;
        Realm = realm;
        Stack = new JsValue[code.NumLocals + code.StackSize + 2];
        Base = code.NumLocals;
        Sp = Base;
        ThisFrame = this;
    }

    internal StackFrameInfo ToInfo()
    {
        int pc = Math.Clamp(Pc, 0, Math.Max(0, Code.SpanStart.Length - 1));
        var span = Code.SpanStart.Length == 0 ? new Span(0, 0) : new Span(Code.SpanStart[pc], Code.SpanEnd[pc]);
        return new StackFrameInfo(Code.Kind is FunctionKind.Script or FunctionKind.Module or FunctionKind.Eval ? "" : Code.Name, Code.Source, span, Code.IsAsync, IsConstruct);
    }
}
