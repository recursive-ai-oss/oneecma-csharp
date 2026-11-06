using RecursiveAI.OneEcma.Diagnostics;
using RecursiveAI.OneEcma.Syntax;

namespace RecursiveAI.OneEcma.Tests;

public class TypeStripTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Erased text is written as the same number of marker characters, so an expectation shows exactly what is blanked.
    private static string Gone(string erased) => new('\u00a4', erased.Length);

    private static string Strip(string source, bool module = true)
    {
        TypeStripResult r = ScriptEngine.StripTypes(source, new TypeStripOptions { FileName = "t.ts", Module = module }, Ct);
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.Headline)));
        Assert.Empty(r.Diagnostics);
        Assert.Equal(source.Length, r.Code!.Length);
        // Every line break stays where it was.
        Assert.Equal(Lines(source), Lines(r.Code));
        return r.Code.Replace(' ', '\u00a4');
    }

    private static string[] Lines(string s) => s.Split('\n').Select(l => l.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();

    private static void Erased(string source, string expectedWithMarkers)
    {
        // Spaces that were in the source stay spaces; markers stand for blanked characters.
        string actual = Strip(source);
        string expected = expectedWithMarkers.Replace(' ', '\u00a4');
        // A source space and a blanked character are both spaces in the output, so compare the output as plain spaces.
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void VariableAnnotation() => Erased("let x: number = 1;", "let x" + Gone(": number") + " = 1;");

    [Fact]
    public void ParametersOptionalAndReturnType() => Erased(
        "function f(a: string, b?: number): void {}",
        "function f(a" + Gone(": string") + ", b" + Gone("?: number") + ")" + Gone(": void") + " {}");

    [Fact]
    public void DestructuredParameterAndDefault() => Erased(
        "function f({ a, b }: { a: number; b: string }, [c]: number[] = []) {}",
        "function f({ a, b }" + Gone(": { a: number; b: string }") + ", [c]" + Gone(": number[]") + " = []) {}");

    [Fact]
    public void ThisParameterIsRemovedWithItsComma() => Erased(
        "function f(this: Window, a) {}",
        "function f(" + Gone("this: Window, ") + "a) {}");

    [Fact]
    public void TypeArgumentsOnCallsAndNew()
    {
        Erased("f<T>(x);", "f" + Gone("<T>") + "(x);");
        Erased("const m = new Map<string, number>();", "const m = new Map" + Gone("<string, number>") + "();");
        Erased("const n = new Set<Array<Array<number>>>();", "const n = new Set" + Gone("<Array<Array<number>>>") + "();");
    }

    [Fact]
    public void ComparisonsAreNotTypeArguments()
    {
        string[] same = ["a < b > c;", "if (a < b && c > (d)) {}", "x = a < b >= c;", "x = a >> b; y = a >>> b; z >>= 2;", "x = i < n; y = j > m;"];
        foreach (string s in same)
        {
            Assert.Equal(s.Replace(' ', '\u00a4'), Strip(s));
        }
    }

    [Fact]
    public void GenericDeclarations() => Erased(
        "function id<T extends object = {}>(x: T): T { return x; }\nconst h = <T,>(x: T): T => x;\nconst k = async <U>(y: U) => y;",
        "function id" + Gone("<T extends object = {}>") + "(x" + Gone(": T") + ")" + Gone(": T") + " { return x; }\nconst h = " + Gone("<T,>") + "(x" + Gone(": T") + ")" + Gone(": T") + " => x;\nconst k = async " + Gone("<U>") + "(y" + Gone(": U") + ") => y;");

    [Fact]
    public void AsConstAsAndSatisfies()
    {
        Erased("const a = [1, 2] as const;", "const a = [1, 2] " + Gone("as const") + ";");
        Erased("const b = x as unknown as string[];", "const b = x " + Gone("as unknown as string[]") + ";");
        Erased("const c = {} satisfies Foo;", "const c = {} " + Gone("satisfies Foo") + ";");
        Erased("const d = (x as any).y;", "const d = (x " + Gone("as any") + ").y;");
    }

    [Fact]
    public void NonNullAssertion() => Erased("const v = a!.b![0]!;", "const v = a" + Gone("!") + ".b" + Gone("!") + "[0]" + Gone("!") + ";");

    [Fact]
    public void InterfaceAndTypeAliasAreErasedWhole()
    {
        const string iface = "interface A<T> extends B {\n  x: number;\n  m(): void;\n}";
        Erased(iface + "\nlet y = 1;", Gone(iface.Split('\n')[0]) + "\n" + Gone("  x: number;") + "\n" + Gone("  m(): void;") + "\n" + Gone("}") + "\nlet y = 1;");
        Erased("type A = { a: string };\nlet z = 1;", Gone("type A = { a: string }") + ";" + "\nlet z = 1;");
        Erased("export interface P { a: 1 }\nexport type Q<T> = T extends string ? 'a' : 'b';\nexport const k = 1;", Gone("export interface P { a: 1 }") + "\n" + Gone("export type Q<T> = T extends string ? 'a' : 'b'") + ";" + "\nexport const k = 1;");
        Erased("type F = (a: number) => void;\nlet z = 1;", Gone("type F = (a: number) => void") + ";" + "\nlet z = 1;");
        Erased("type R = readonly string[] | Record<string, unknown>;", Gone("type R = readonly string[] | Record<string, unknown>") + ";");
    }

    [Fact]
    public void DeclareDeclarationsAreErased()
    {
        Erased("declare const x: number;\ndeclare function f(a: string): void;\ndeclare class C { m(): void; x: number }\ndeclare module 'm' { export const y: number; }\ndeclare global { interface W {} }\ndeclare enum E { A }\nlet z = 1;",
            Gone("declare const x: number") + ";" + "\n" + Gone("declare function f(a: string): void") + ";" + "\n" + Gone("declare class C { m(): void; x: number }") + "\n" + Gone("declare module 'm' { export const y: number; }") + "\n" + Gone("declare global { interface W {} }") + "\n" + Gone("declare enum E { A }") + "\nlet z = 1;");
    }

    [Fact]
    public void OverloadSignaturesAreErased() => Erased(
        "function f(a: string): string;\nfunction f(a: number): number;\nfunction f(a) { return a; }\nexport function g(): void;\nexport function g() {}",
        Gone("function f(a: string): string") + ";" + "\n" + Gone("function f(a: number): number") + ";" + "\nfunction f(a) { return a; }\n" + Gone("export function g(): void") + ";" + "\nexport function g() {}");

    [Fact]
    public void ImportTypeVanishesAndInlineTypeKeepsTheImport()
    {
        Erased("import type { X } from './y';\nexport const a = 1;", Gone("import type { X } from './y'") + ";" + "\nexport const a = 1;");
        Erased("import type D from './y';\nimport type * as N from './z';", Gone("import type D from './y'") + ";" + "\n" + Gone("import type * as N from './z'") + ";");
        Erased("import { type A, b } from 'm';", "import { " + Gone("type A, ") + "b } from 'm';");
        Erased("import { a, type B } from 'm';", "import { a" + Gone(", type B") + " } from 'm';");
        Erased("import { type A } from 'm';", "import { " + Gone("type A") + " } from 'm';");
        Erased("import d, { type A, b as c } from 'm';", "import d, { " + Gone("type A, ") + "b as c } from 'm';");
        Erased("export type { A } from 'm';\nexport type * from 'n';", Gone("export type { A } from 'm'") + ";" + "\n" + Gone("export type * from 'n'") + ";");
        Erased("const b = 1; export { type A, b };", "const b = 1; export { " + Gone("type A, ") + "b };");
        Erased("import { type } from 'm';", "import { type } from 'm';");
        Erased("import type from 'm';", "import type from 'm';");
    }

    [Fact]
    public void ClassMembers()
    {
        const string src = "class A<T> extends B<T> implements I, J {\n  private readonly x: number = 1;\n  public static s?: string;\n  y?: string;\n  z!: number;\n  declare w: number;\n  protected override m(a: T): T { return a; }\n  abstract q(): void;\n  foo(a: T): T;\n  foo(a) { return a; }\n  [key: string]: unknown;\n  get g(): number { return 1; }\n}";
        string[] lines = src.Split('\n');
        string expected = string.Join("\n",
            "class A" + Gone("<T>") + " extends B" + Gone("<T>") + " " + Gone("implements I, J ") + "{",
            "  " + Gone("private readonly ") + "x" + Gone(": number") + " = 1;",
            "  " + Gone("public ") + "static s" + Gone("?: string") + ";",
            "  y" + Gone("?: string") + ";",
            "  z" + Gone("!: number") + ";",
            "  " + Gone("declare w: number") + ";",
            "  " + Gone("protected override ") + "m(a" + Gone(": T") + ")" + Gone(": T") + " { return a; }",
            "  " + Gone("abstract q(): void") + ";",
            "  " + Gone("foo(a: T): T") + ";",
            "  foo(a) { return a; }",
            "  " + Gone("[key: string]: unknown") + ";",
            "  get g()" + Gone(": number") + " { return 1; }",
            "}");
        Assert.Equal(lines.Length, expected.Split('\n').Length);
        Erased(src, expected);
    }

    [Fact]
    public void AbstractClassAndExportDefault()
    {
        Erased("export abstract class A { abstract m(): void; n() {} }", "export " + Gone("abstract ") + "class A { " + Gone("abstract m(): void") + ";" + " n() {} }");
        Erased("export default interface I {}\nexport default class {}", Gone("export default interface I {}") + "\nexport default class {}");
    }

    [Fact]
    public void ConditionalExpressionIsNotAnArrowReturnType() => Erased(
        "const r = a ? (b) : c => d;",
        "const r = a ? (b) : c => d;");

    [Fact]
    public void ArrowWithReturnTypeInsideTernary() => Erased(
        "const r = a ? (x: number): number => x : null;",
        "const r = a ? (x" + Gone(": number") + ")" + Gone(": number") + " => x : null;");

    [Fact]
    public void TypeOperatorsTemplatesAndFunctionTypes() => Erased(
        "let a: keyof typeof o; let b: `x${string}-y`; let c: (new () => Foo) | { [K in keyof T]?: T[K] }; let d: readonly [a: 1, b?: 2]; let e: typeof import('m'); let f: <T>(x: T) => asserts x is T;",
        "let a" + Gone(": keyof typeof o") + "; let b" + Gone(": `x${string}-y`") + "; let c" + Gone(": (new () => Foo) | { [K in keyof T]?: T[K] }") + "; let d" + Gone(": readonly [a: 1, b?: 2]") + "; let e" + Gone(": typeof import('m')") + "; let f" + Gone(": <T>(x: T) => asserts x is T") + ";");

    [Fact]
    public void TypePredicateReturn() => Erased(
        "function isS(x: unknown): x is string { return typeof x === 'string'; }",
        "function isS(x" + Gone(": unknown") + ")" + Gone(": x is string") + " { return typeof x === 'string'; }");

    [Fact]
    public void CatchAnnotationAndLoops() => Erased(
        "try { f(); } catch (e: unknown) {}\nfor (const k of ks as string[]) {}\nfor (let i: number = 0; i < n; i++) {}",
        "try { f(); } catch (e" + Gone(": unknown") + ") {}\nfor (const k of ks " + Gone("as string[]") + ") {}\nfor (let i" + Gone(": number") + " = 0; i < n; i++) {}");

    [Fact]
    public void ObjectLiteralMethodsAndPatterns() => Erased(
        "const o = { m<T>(a: T): T { return a; }, get g(): number { return 1; }, a: x as number };\nconst { p, q: [r] }: Shape = o;",
        "const o = { m" + Gone("<T>") + "(a" + Gone(": T") + ")" + Gone(": T") + " { return a; }, get g()" + Gone(": number") + " { return 1; }, a: x " + Gone("as number") + " };\nconst { p, q: [r] }" + Gone(": Shape") + " = o;");

    // A newline inside an erased return type would put "=>" on a line after ")", which is a syntax error.
    [Fact]
    public void ArrowStaysOnTheLineOfItsParenthesis()
    {
        const string src = "const f = (a: number): {\n  v: number } => ({ v: a + 1 });\nf(1).v;";
        TypeStripResult r = ScriptEngine.StripTypes(src, new TypeStripOptions { FileName = "t.ts" }, Ct);
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.Headline)));
        Assert.Equal(Lines(src), Lines(r.Code!));
        Assert.Equal("const f = (a" + new string(' ', 8) + ")=> ", r.Code!.Split('\n')[0][..24]);
        Assert.Equal(2, new ScriptEngine().Evaluate(r.Code, "t.ts", Ct).AsNumber);
    }

    [Fact]
    public void ArrowReturnTypeThatBeginsOnTheNextLineIsRefused() =>
        Refused("const g = (a):\n  number => a;", "ts-arrow-return-type-layout", "The return type of this arrow function cannot be erased in place", ":\n  number");

    // After "as T" a line starting with ( would join the expression once the type is gone.
    [Fact]
    public void SemicolonKeepsTheNextLineApartAfterAs()
    {
        const string src = "const a = b as\n  Foo\n(c).d();";
        TypeStripResult r = ScriptEngine.StripTypes(src, new TypeStripOptions { FileName = "t.ts" }, Ct);
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.Headline)));
        Assert.Equal(Lines(src), Lines(r.Code!));
        Assert.Contains(";", r.Code!, StringComparison.Ordinal);
        var engine = new ScriptEngine();
        JsValue v = engine.Evaluate("var b = 1, c = { d() { return 7; } };" + r.Code, "t.js", Ct);
        Assert.Equal(7, v.AsNumber);
    }

    [Fact]
    public void PlainJavaScriptIsReturnedUnchanged()
    {
        string[] plain =
        [
            "", "var a = 1;", "let { a, b = 2 } = o; x = y ? z : w;", "label: for (;;) { break label; }\nclass A { #p = 1; static { } get x() { return this.#p; } }",
            "const f = async (a, b) => { await a; return b; }; const g = async function* () { yield* h(); };",
            "x = a / b / c; y = /re/g.test(s); z = `t${a}${b}`;", "import a, { b as c } from 'm'; export { a, c }; export * from 'n'; export default 1;",
            "if (a) b; else c; do x++; while (x < 3); switch (a) { case 1: break; default: }", "var type = 1, as = 2, of = 3, interface_ = 4; type = as;",
            "a = b\n(c)\n[d]\n`e`", "async function f() { for await (const x of y) { } }", "function f() { return new.target; } y = import.meta; z = import('m');",
        ];
        foreach (string s in plain)
        {
            TypeStripResult r = ScriptEngine.StripTypes(s, new TypeStripOptions { Module = true }, Ct);
            Assert.True(r.Success, s + " => " + string.Join(";", r.Diagnostics.Select(d => d.Headline)));
            Assert.Same(s, r.Code);
        }
    }

    private static void Refused(string source, string code, string headline, string spanText, params string[] more)
    {
        TypeStripResult r = ScriptEngine.StripTypes(source, new TypeStripOptions { FileName = "t.ts" }, Ct);
        Assert.False(r.Success);
        Assert.Null(r.Code);
        Diagnostic d = r.Diagnostics[0];
        Assert.Equal(code, d.Code);
        Assert.Equal(headline, d.Headline);
        Assert.Equal(spanText, source[d.PrimarySpan.Start..d.PrimarySpan.End]);
        Assert.NotEmpty(d.PrimaryLabel);
        Assert.NotEmpty(d.Help);
        Assert.Equal("t.ts", d.Source.Name);
        string text = DiagnosticRenderer.Render(d);
        Assert.Contains("error[SyntaxError]: " + headline, text, StringComparison.Ordinal);
        Assert.Contains("= help:", text, StringComparison.Ordinal);
        Assert.Contains("^", text, StringComparison.Ordinal);
        foreach (string m in more)
        {
            Assert.Contains(m, d.Help[0], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EnumIsRefused()
    {
        Refused("enum Color { Red, Green }\nlet x = 1;", "ts-enum", "TypeScript 'enum' is not erasable syntax", "enum Color", "const Color");
        Refused("export const enum E { A = 1 }", "ts-enum", "TypeScript 'const enum' is not erasable syntax", "const enum E");
    }

    [Fact]
    public void NamespaceWithValuesIsRefusedButTypeOnlyIsErased()
    {
        Refused("namespace N {\n  export const a = 1;\n}", "ts-namespace", "TypeScript 'namespace' blocks that contain values are not erasable syntax", "namespace N");
        Refused("export module M.Inner { function f() {} }", "ts-namespace", "TypeScript 'module' blocks that contain values are not erasable syntax", "module M.Inner");
        Erased("namespace T {\n  export interface A {}\n  export type B = number;\n}\nlet y = 1;", Gone("namespace T {") + "\n" + Gone("  export interface A {}") + "\n" + Gone("  export type B = number;") + "\n" + Gone("}") + "\nlet y = 1;");
    }

    [Fact]
    public void ParameterPropertiesAreRefused()
    {
        Refused("class A {\n  constructor(private x: number, public readonly y = 1) {}\n}", "ts-parameter-property", "TypeScript parameter properties are not erasable syntax", "private x", "constructor");
        TypeStripResult r = ScriptEngine.StripTypes("class A {\n  constructor(private x: number, public readonly y = 1) {}\n}", null, Ct);
        Assert.Equal(2, r.Diagnostics.Count);
    }

    [Fact]
    public void ImportEqualsAndExportAssignmentAreRefused()
    {
        Refused("import fs = require('fs');\nfs.x();", "ts-import-equals", "TypeScript 'import fs = require()' is not erasable syntax", "import fs = require('fs');", "import fs from");
        Refused("import A = B.C;", "ts-import-equals", "TypeScript 'import A = ...' is not erasable syntax", "import A = B.C;");
        Refused("const x = 1;\nexport = x;", "ts-export-assignment", "TypeScript 'export =' is not erasable syntax", "export =", "export default");
    }

    [Fact]
    public void LegacyAngleCastIsRefused()
    {
        Refused("const a = <string>x;", "ts-angle-cast", "TypeScript '<T>expr' type assertions are not erasable syntax", "<string>", "as T");
    }

    [Fact]
    public void ParameterDecoratorIsRefusedAndOtherDecoratorsPass()
    {
        Refused("class A { m(@Inject() x) {} }", "ts-parameter-decorator", "TypeScript parameter decorators are not erasable syntax", "@Inject()");
        // The engine has no decorator syntax yet, so class and member decorators come back as the parser's own error, on the original text.
        TypeStripResult r = ScriptEngine.StripTypes("@dec class A { m(x: number) {} }", null, Ct);
        Assert.False(r.Success);
        Assert.Equal("SyntaxError", r.Diagnostics[0].Kind);
        Assert.Equal((1, 1), (r.Diagnostics[0].Position.Line, r.Diagnostics[0].Position.Column));
    }

    [Fact]
    public void EveryRefusalInTheFileIsReportedInSourceOrder()
    {
        const string src = "enum A { X }\nnamespace N { export const v = 1; }\nclass C { constructor(readonly z) {} }\nimport q = require('q');\nlet ok: number = <number>1;";
        TypeStripResult r = ScriptEngine.StripTypes(src, new TypeStripOptions { FileName = "t.ts" }, Ct);
        Assert.False(r.Success);
        Assert.Equal(["ts-enum", "ts-namespace", "ts-parameter-property", "ts-import-equals", "ts-angle-cast"], r.Diagnostics.Select(d => d.Code).ToArray());
        Assert.Equal([1, 2, 3, 4, 5], r.Diagnostics.Select(d => d.Position.Line).ToArray());
    }

    [Fact]
    public void SyntaxErrorsAreReportedAgainstTheOriginalSpans()
    {
        const string src = "let a: number = 1;\nlet b: string = ;\n";
        TypeStripResult r = ScriptEngine.StripTypes(src, new TypeStripOptions { FileName = "t.ts" }, Ct);
        Assert.False(r.Success);
        Diagnostic d = r.Diagnostics[0];
        Assert.Equal(2, d.Position.Line);
        Assert.Equal(17, d.Position.Column);
        Assert.Equal("t.ts", d.Source.Name);
        Assert.Contains("let b: string = ;", DiagnosticRenderer.Render(d), StringComparison.Ordinal);

        // An error the reader does not see, but the parser does, is reported on the original text too.
        const string src2 = "let a: number = 1;\nlet a = 2;\n";
        TypeStripResult r2 = ScriptEngine.StripTypes(src2, new TypeStripOptions { FileName = "t.ts" }, Ct);
        Assert.False(r2.Success);
        Assert.Equal(2, r2.Diagnostics[0].Position.Line);
        Assert.Contains("let a: number = 1;", DiagnosticRenderer.Render(r2.Diagnostics[0]), StringComparison.Ordinal);
    }

    [Fact]
    public void ScriptGoalRejectsModuleSyntaxInTheResult()
    {
        TypeStripResult r = ScriptEngine.StripTypes("import { type A, b } from 'm';", new TypeStripOptions { Module = false }, Ct);
        Assert.False(r.Success);
    }

    [Fact]
    public void DeepNestingEndsInADiagnostic()
    {
        string src = "let x: " + new string('(', 100000) + "number" + new string(')', 100000) + ";";
        TypeStripResult ok = ScriptEngine.StripTypes(src, null, Ct);
        Assert.NotNull(ok);
        string expr = "x = " + new string('[', 50000) + new string(']', 50000) + ";";
        TypeStripResult deep = ScriptEngine.StripTypes(expr, null, Ct);
        Assert.False(deep.Success);
        Assert.Contains("nesting", deep.Diagnostics[0].Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void CancellationIsHonoured()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        string big = string.Concat(Enumerable.Repeat("let a: number = 1;\n", 5000));
        Assert.Throws<OperationCanceledException>(() => ScriptEngine.StripTypes(big, null, cts.Token));
    }
}
