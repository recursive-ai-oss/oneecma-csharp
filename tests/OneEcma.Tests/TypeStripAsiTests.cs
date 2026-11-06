using RecursiveAI.OneEcma.Syntax;

namespace RecursiveAI.OneEcma.Tests;

// ECMA-262 12.10: a declaration that TypeScript reads as its own statement is blanked in the output, so the lines around it must
// still be two statements. Each test runs the stripped program and compares it with the TypeScript meaning, which is the same
// program with the declaration replaced by an explicit ";".
public class TypeStripAsiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Gone(string erased) => new('\u00a4', erased.Length);

    private static string Strip(string source)
    {
        TypeStripResult r = ScriptEngine.StripTypes(source, new TypeStripOptions { FileName = "t.ts", Module = false }, Ct);
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.Headline)));
        Assert.Equal(source.Length, r.Code!.Length);
        Assert.Equal(source.Split('\n').Select(l => l.Length), r.Code.Split('\n').Select(l => l.Length));
        return r.Code;
    }

    private static void Same(string expectedWithMarkers, string actual) => Assert.Equal(expectedWithMarkers.Replace(' ', '\u00a4'), actual.Replace(' ', '\u00a4'));

    private static string Run(string js)
    {
        try
        {
            var engine = new ScriptEngine();
            return engine.ToDisplayString(engine.Evaluate(js, "t.js", Ct));
        }
        catch (JsException e)
        {
            return "throws " + e.Report?.Headline;
        }
    }

    private static void ExactAndMeaning(string ts, string expectedWithMarkers, string reference, string expectedValue)
    {
        string code = Strip(ts);
        Assert.Equal(expectedWithMarkers.Replace(' ', '\u00a4'), code.Replace(' ', '\u00a4'));
        Assert.Equal(expectedValue, Run(reference));
        Assert.Equal(expectedValue, Run(code));
    }

    private const string Prelude = "var calls = 0; var g = function () { calls++; return 0; }; var f = function () { calls += 10; return 0; }; var tag = function () { calls += 100; return 0; };\n";

    [Fact]
    public void TypeAliasBetweenAnIdentifierAndAParenthesis() => ExactAndMeaning(
        Prelude + "let x = f\ntype T = number\n(g)()\ntypeof x",
        Prelude + "let x = f\n;" + Gone("ype T = number") + "\n(g)()\ntypeof x",
        Prelude + "let x = f\n;\n(g)()\ntypeof x",
        "function");

    [Fact]
    public void TypeAliasBetweenAnIdentifierAndATemplate() => ExactAndMeaning(
        Prelude + "let r = tag\ntype T = 1\n`x`\ntypeof r",
        Prelude + "let r = tag\n;" + Gone("ype T = 1") + "\n`x`\ntypeof r",
        Prelude + "let r = tag\n;\n`x`\ntypeof r",
        "function");

    [Fact]
    public void LeadingSemicolonGuardIsKept() => ExactAndMeaning(
        Prelude + "let x = g\ninterface I { a: number }\n;[1,2].forEach(() => {})\ntypeof x",
        Prelude + "let x = g\n" + Gone("interface I { a: number }") + "\n;[1,2].forEach(() => {})\ntypeof x",
        Prelude + "let x = g\n\n;[1,2].forEach(() => {})\ntypeof x",
        "function");

    [Fact]
    public void DeclareConstBeforeABracket() => ExactAndMeaning(
        Prelude + "let r = 'A'\nlet a = [1]\nlet z = r\ndeclare const q: number\n[0]\nr + a.length",
        Prelude + "let r = 'A'\nlet a = [1]\nlet z = r\n;" + Gone("eclare const q: number") + "\n[0]\nr + a.length",
        Prelude + "let r = 'A'\nlet a = [1]\nlet z = r\n;\n[0]\nr + a.length",
        "A1");

    [Fact]
    public void ASemicolonThatEndsTheDeclarationStaysInTheOutput() => ExactAndMeaning(
        Prelude + "let x = f\ntype T = number;\n(g)()\ntypeof x",
        Prelude + "let x = f\n" + Gone("type T = number") + ";\n(g)()\ntypeof x",
        Prelude + "let x = f\n;\n(g)()\ntypeof x",
        "function");

    [Fact]
    public void ASemicolonOnTheNextLineIsNeverErased()
    {
        string code = Strip("let a = 1\ninterface I {}\n;\n(a)");
        Same("let a = 1\n" + Gone("interface I {}") + "\n;\n(a)", code);
    }

    [Fact]
    public void NoSemicolonIsWrittenWhereNoneIsNeeded()
    {
        Same("let a = 1;\n" + Gone("interface I {}") + "\nlet b = 2", Strip("let a = 1;\ninterface I {}\nlet b = 2"));
        Same("let a = 1\n" + Gone("interface I {}") + "\nfoo()", Strip("let a = 1\ninterface I {}\nfoo()"));
        Same("function f() {\n  " + Gone("type T = 1") + "\n}", Strip("function f() {\n  type T = 1\n}"));
    }

    private static readonly string[] Declarations =
    [
        "type T = number",
        "interface I { a: number }",
        "declare const z: number",
        "declare function dz(a: string): void",
        "declare class DC { m(): void }",
        "declare module 'dm' { export const y: number }",
        "declare global { interface W {} }",
        "declare enum DE { A }",
        "namespace NS { export type A = 1 }",
        "declare namespace DN { const v: number }",
        "import type { X } from './x'",
        "export type ET = 1",
        "export interface EI {}",
        "export declare const ec: number",
        "export default interface DI {}",
    ];

    // The statement before the declaration, the statement after it, and the result of the TypeScript program.
    private static readonly (string Before, string After, string Expected)[] Contexts =
    [
        ("var a = f", "(g)()", "function:1"),          // identifier, then a call
        ("var a = f()", "(g)()", "number:11"),         // ")" then a call
        ("var a = [1, 2]", "[0]", "object:0"),         // "]" then an index
        ("var a = `t`", "(g)()", "string:1"),          // template then a call
        ("var a = 1", "[0]", "number:0"),              // literal then an index
        ("var a = 1; a++", "(g)()", "number:1"),       // postfix ++ then a call
        ("var a = 1; a--", "(g)()", "number:1"),       // postfix -- then a call
        ("var a = tag", "`x`", "function:0"),          // identifier then a template
        ("var a = 1", "+ 1", "number:0"),              // literal then a unary-looking binary operator
    ];

    [Fact]
    public void EveryDeclarationKindAfterEveryKindOfStatementEnd()
    {
        var failures = new List<string>();
        foreach (string decl in Declarations)
        {
            foreach ((string before, string after, string expected) in Contexts)
            {
                string tail = "\ntypeof a + ':' + calls";
                string ts = Prelude + before + "\n" + decl + "\n" + after + tail;
                string reference = Prelude + before + "\n;\n" + after + tail;
                string refValue = Run(reference);
                if (refValue != expected)
                {
                    failures.Add("reference " + before + " / " + after + " gave " + refValue);
                    continue;
                }

                TypeStripResult r = ScriptEngine.StripTypes(ts, new TypeStripOptions { Module = false }, Ct);
                if (!r.Success)
                {
                    failures.Add("refused: " + decl + " | " + before + " | " + after + " => " + r.Diagnostics[0].Headline);
                    continue;
                }

                string got = Run(r.Code!);
                if (got != expected)
                {
                    failures.Add(decl + " | " + before + " | " + after + " => " + got);
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void OverloadSignatureAfterAnExpressionStatement()
    {
        // The next token is "function", which cannot continue an expression, so nothing is written.
        string code = Strip("let a = 1\nfunction f(x: string): void\nfunction f(x) {}");
        Same("let a = 1\n" + Gone("function f(x: string): void") + "\nfunction f(x) {}", code);
    }

    [Fact]
    public void OverloadSignatureBeforeAParenthesisGetsTheGuard()
    {
        string code = Strip("let a = 1\nexport function f(x: string): void\n(a)");
        Same("let a = 1\n;" + Gone("xport function f(x: string): void") + "\n(a)", code);
    }

    [Fact]
    public void TypeArgumentsAtTheEndOfALineBeforeABracket() => ExactAndMeaning(
        Prelude + "var a = f<number>\n[0]\ntypeof a + ':' + calls",
        Prelude + "var a = f" + Gone("<number") + ";\n[0]\ntypeof a + ':' + calls",
        Prelude + "var a = f;\n[0]\ntypeof a + ':' + calls",
        "function:0");

    [Fact]
    public void NonNullAssertionAtTheEndOfALineContinuesLikeTypeScript()
    {
        // TypeScript reads "a![0]" and "f!(g)", and so does the blanked text; no semicolon is added.
        string code = Strip("var v = [5]!\n[0]");
        Same("var v = [5]" + Gone("!") + "\n[0]", code);
        Assert.Equal("number", Run(code + "\ntypeof v"));
    }

    private static void ClassMeaning(string ts, string expectedKeys)
    {
        string code = Strip(ts);
        Assert.Equal(expectedKeys, Run(code));
    }

    [Fact]
    public void DeclareFieldBetweenAnInitializerAndAComputedMember() => ClassMeaning(
        "var k = 'kk'; class A {\n  a = 1\n  declare y: number\n  [k] = 2\n}\nObject.keys(new A()).join()",
        "a,kk");

    [Fact]
    public void IndexSignatureBeforeAGenerator() => ClassMeaning(
        "class A {\n  a = 1;\n  m() {}\n  [key: string]: unknown\n  *gen() { yield 1 }\n}\ntypeof new A().gen + ':' + Object.keys(new A()).join()",
        "function:a");

    [Fact]
    public void ErasedModifierAtTheStartOfAMember() => ClassMeaning(
        "var k = 'kk'; class A {\n  a = 1\n  private [k] = 2\n  public *gen() { yield 1 }\n}\ntypeof new A().gen + ':' + Object.keys(new A()).join()",
        "function:a,kk");

    [Fact]
    public void AbstractMemberBetweenAnInitializerAndAComputedMember() => ClassMeaning(
        "var k = 'kk'; abstract class B {\n  a = 1\n  abstract m(): void\n  [k] = 3\n}\nclass C extends B { m() {} }\nObject.keys(new C()).join()",
        "a,kk");

    [Fact]
    public void OverloadedMethodInAClassNeedsNoGuard()
    {
        string code = Strip("class A {\n  a = 1\n  m(x: string): void\n  m(x) {}\n}");
        Same("class A {\n  a = 1\n  " + Gone("m(x: string): void") + "\n  m(x) {}\n}", code);
    }

    [Fact]
    public void ClassMemberGuardIsWrittenInTheFirstErasedColumn()
    {
        string code = Strip("class A {\n  a = 1\n  declare y: number\n  [k] = 2\n}");
        Same("class A {\n  a = 1\n  ;" + Gone("eclare y: number") + "\n  [k] = 2\n}", code);
    }

    [Fact]
    public void StrippedTextIsStableUnderCrLf()
    {
        string code = Strip("let x = f\r\ntype T = number\r\n(g)()");
        Same("let x = f\r\n;" + Gone("ype T = number") + "\r\n(g)()", code);
    }
}
