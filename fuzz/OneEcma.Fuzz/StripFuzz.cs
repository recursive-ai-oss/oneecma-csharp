using System.Diagnostics;
using System.Globalization;
using RecursiveAI.OneEcma.Syntax;
using RecursiveAI.OneEcma.Text;

namespace RecursiveAI.OneEcma.Fuzz;

/// <summary>
/// Type-stripper fuzzing. Seeds are TypeScript snippets plus any .js/.ts files under the given directories; every mutant goes
/// through <see cref="ScriptEngine.StripTypes"/> as a module and as a script. Checked: only cancellation may escape; a result
/// is either code or at least one diagnostic; code has the length and line breaks of the input and differs from it only by
/// blanks and the few tokens the stripper writes; code parses; a second pass is safe; writing a ";" at an erased statement boundary does not change the parse; one call is fast.
/// usage: strip &lt;seconds&gt; &lt;outdir&gt; [seed] [seeddir...]; strip-file &lt;path&gt; checks one input.
/// </summary>
internal static class StripFuzz
{
    private static readonly string[] Snippets =
    [
        "let x: number = 1;", "function f<T extends object = {}>(a: T, b?: string, ...rest: number[]): asserts a is T {}",
        "const g = <T,>(x: T): T => x;", "const h = async <U>(y: U): Promise<U> => y;", "const m = new Map<string, Array<number>>();",
        "f<A, B>(x); g<T>`t`; a < b > c; a<b>>c; a >>= 2;", "const a = x as const; const b = y satisfies Foo<Bar>; const c = z!.q![0]!;",
        "interface A<T> extends B<T>, C { readonly x?: number; m<U>(a: U): void; [k: string]: unknown; new (a: T): A<T>; }",
        "type U = 'a' | 'b' | `x${string}`; type F = (a: number, ...r: string[]) => void; type C<T> = T extends (infer R)[] ? R : never;",
        "type M = { readonly [K in keyof T]?: T[K] }; type N = { -readonly [K in keyof T]-?: T[K] }; type Tup = [a: string, b?: number, ...c: boolean[]];",
        "declare const d: number; declare function df(a: string): void; declare module 'm' { export const q: number; } declare global { interface W {} }",
        "export type { A } from 'm'; export type * as ns from 'm'; import type D, { E } from 'm'; import { type F, G, type H as I } from 'm';",
        "class C<T> extends B<T> implements I, J<T> { private readonly p: number = 1; static s?: string; y!: number; declare z: string; "
            + "protected override m(a: T): T { return a; } abstract q(): void; foo(a: T): T; foo(a) { return a; } [k: string]: any; get g(): number { return 1; } static { } }",
        "abstract class Q { abstract get v(): number; abstract m(): void; }", "function o(a: string): string;\nfunction o(a: number): number;\nfunction o(a) { return a; }",
        "enum E { A, B }", "const enum F { A }", "namespace N { export const v = 1; }", "namespace T { export interface I {} }", "class K { constructor(private x: number, public readonly y = 1) {} }",
        "import fs = require('fs'); export = fs;", "const c = <string>x;", "class P { m(@dec x) {} }",
        "const r = a ? (b) : c => d; const s = a ? (b): c => d : e;", "const f2 = (a: number):\n  number => a;", "const f3 = (a: number): {\n a: number } => ({ a });",
        "const t = b as\n  Foo\n(c).d();", "for (const k in o as any) {} for (let i: number = 0; i < n; i++) {} for (const v of xs as string[]) {}",
        "try { f(); } catch (e: unknown) { }", "label: for (;;) { break label; }", "const re = /ab+c/g, q = a / b / c, t = `x${y}${z}`;",
        "export default interface I {}", "export default abstract class A {}", "export abstract class B {}", "export declare const dc: number;", "export as namespace X;",
        "function th(this: Window, a: number) {}", "let u: unique symbol; let k: keyof typeof o; let i: typeof import('m'); let p: import('m').Foo<string>;",
        "let fn: new () => Foo; let fa: abstract new () => Foo; let g2: <T>(x: T) => T; let o2: { (a: number): string; new (): X; readonly [i: number]: string };",
        "const o3 = { m<T>(a: T): T { return a; }, get g(): number { return 1; }, async *ag() {}, ['c' + 1]: 2, ...s };", "x = a ? b : c ? d : e;",
    ];

    private static readonly string[] Pieces =
    [
        ":", "?", "!", "<", ">", ">>", ">=", "<T>", "<T,>", "<T extends U>", "as", "as const", "satisfies", "type ", "interface ", "declare ", "abstract ", "readonly ", "private ",
        "public ", "protected ", "override ", "implements ", "enum ", "namespace ", "module ", "export ", "import type ", "import ", "from ", "=>", "=", "|", "&", "keyof ", "typeof ",
        "infer ", "extends ", "is ", "asserts ", "unique ", "new ", "this", "...", "[", "]", "{", "}", "(", ")", ",", ";", "\n", "`", "${", "'", "\"", "/", "//", "/*", "*/",
        "@", "#x", "class ", "function ", "async ", "await ", "yield ", "get ", "set ", "static ", "const ", "let ", "var ", "?.", "??", "**", "x", "T", "number", "string", "unknown",
    ];

    internal static int RunRandom(int seconds, string outDir, int seed, IEnumerable<string> seedDirs)
    {
        var rng = new Random(seed);
        var corpus = new List<string>(Snippets);
        foreach (string dir in seedDirs)
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Where(p => p.EndsWith(".js", StringComparison.Ordinal) || p.EndsWith(".ts", StringComparison.Ordinal)).Take(4000))
            {
                FileInfo info = new(f);
                if (info.Length < 20_000)
                {
                    corpus.Add(File.ReadAllText(f));
                }
            }
        }

        long executions = 0;
        int findings = 0;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            string text = Mutate(corpus[rng.Next(corpus.Count)], rng);
            if (rng.Next(4) == 0)
            {
                text += "\n" + Mutate(corpus[rng.Next(corpus.Count)], rng);
            }

            string? failure = Check(text, 5000);
            executions++;
            if (failure is not null)
            {
                findings++;
                Console.Error.WriteLine("finding " + findings + ": " + failure.Split('\n')[0]);
                Directory.CreateDirectory(outDir);
                File.WriteAllText(Path.Combine(outDir, "strip-" + findings.ToString(CultureInfo.InvariantCulture) + ".ts"), text);
            }
        }

        Console.WriteLine($"strip: executions={executions} findings={findings} corpus={corpus.Count} seconds={clock.Elapsed.TotalSeconds:F0}");
        return findings == 0 ? 0 : 1;
    }

    internal static string Mutate(string s, Random rng)
    {
        int rounds = 1 + rng.Next(4);
        for (int r = 0; r < rounds && s.Length > 0; r++)
        {
            int at = rng.Next(s.Length + 1);
            switch (rng.Next(6))
            {
                case 0:
                    s = s.Insert(at, Pieces[rng.Next(Pieces.Length)]);
                    break;
                case 1:
                    s = s.Remove(Math.Min(at, s.Length - 1), Math.Min(1 + rng.Next(8), s.Length - Math.Min(at, s.Length - 1)));
                    break;
                case 2:
                    {
                        int len = Math.Min(1 + rng.Next(40), s.Length - Math.Min(at, s.Length - 1));
                        string piece = s.Substring(Math.Min(at, s.Length - 1), len);
                        s = s.Insert(rng.Next(s.Length + 1), piece);
                        break;
                    }

                case 3:
                    s = s.Insert(at, Pieces[rng.Next(Pieces.Length)] + Pieces[rng.Next(Pieces.Length)]);
                    break;
                case 4:
                    {
                        int a = rng.Next(s.Length);
                        s = s[..a] + s[(a + 1)..];
                        break;
                    }

                default:
                    s = s.Replace(" ", rng.Next(2) == 0 ? "\n" : "  ", StringComparison.Ordinal);
                    break;
            }
        }

        return s.Length > 100_000 ? s[..100_000] : s;
    }

    // ECMA-262 12.10: where the stripper erased a whole declaration (or the modifiers at the start of a class member), a ';' written there
    // must not change the program. If it does, the stripper left two statements joined that TypeScript keeps apart. A boundary whose
    // previous token is the ")" of an if/for/while/with header (invalid TypeScript) is skipped.
    private static string? CheckBoundaries(string text, string code, bool module)
    {
        var stripper = new TypeStripper(new SourceText(text, "f.ts"), default);
        stripper.Run();
        if (stripper.Diagnostics.Count > 0 || !stripper.HasEdits || stripper.Apply() != code)
        {
            return null;
        }

        var options = new ParseOptions { Goal = module ? ParseGoal.Module : ParseGoal.Script };
        ParseResult baseline = JsParser.Parse(code, options);
        if (baseline.HasErrors)
        {
            return null;
        }

        string baseFp = AstFingerprint.Of(baseline.Program);
        int checkedBoundaries = 0;
        foreach (int b in stripper.Boundaries.Distinct())
        {
            if (b >= code.Length || code[b] != ' ' || checkedBoundaries++ >= 12)
            {
                continue;
            }

            if (!InScope(code, b))
            {
                continue;
            }

            string variant = string.Concat(code.AsSpan(0, b), ";", code.AsSpan(b + 1));
            ParseResult v = JsParser.Parse(variant, options);
            if (v.HasErrors)
            {
                continue;
            }

            if (AstFingerprint.Of(v.Program) != baseFp)
            {
                return "a statement boundary at " + b + " joins two statements: writing ';' there changes the parse";
            }
        }

        return null;
    }

    // Only a boundary that follows something that can end an expression is a place where a missing ';' could join two statements.
    // After a label, "else", "{", an operator or a control-statement header, TypeScript would not be valid, and a ';' there changes the
    // program on purpose. Before a string, a ';' changes whether the string is a directive.
    private static bool InScope(string code, int b)
    {
        int p = b - 1;
        while (p >= 0 && char.IsWhiteSpace(code[p]))
        {
            p--;
        }

        if (p < 0 || !(char.IsLetterOrDigit(code[p]) || code[p] is '_' or '$' or ')' or ']' or '}' or '`' or '\'' or '"'))
        {
            return false;
        }

        if (code[p] == ')' && ControlHeaderEndsAt(code, p))
        {
            return false;
        }

        int w = p;
        while (w >= 0 && (char.IsLetterOrDigit(code[w]) || code[w] is '_' or '$'))
        {
            w--;
        }

        if (w < p && Array.IndexOf(Keywords, code.Substring(w + 1, p - w)) >= 0)
        {
            return false;
        }

        int n = b;
        while (n < code.Length && (code[n] == ' ' || char.IsWhiteSpace(code[n])))
        {
            n++;
        }

        return !(n < code.Length && code[n] is '\'' or '"');
    }

    private static readonly string[] Keywords = ["else", "do", "return", "throw", "typeof", "void", "delete", "new", "case", "default", "extends", "in", "of", "instanceof", "import", "export", "await", "yield", "try", "finally", "static", "async", "get", "set"];

    // Crude: the text before the matching "(" ends with if/for/while/with.
    private static bool ControlHeaderEndsAt(string code, int close)
    {
        int depth = 0;
        for (int i = close; i >= 0; i--)
        {
            if (code[i] == ')')
            {
                depth++;
            }
            else if (code[i] == '(' && --depth == 0)
            {
                int j = i - 1;
                while (j >= 0 && char.IsWhiteSpace(code[j]))
                {
                    j--;
                }

                foreach (string kw in new[] { "if", "for", "while", "with", "await" })
                {
                    if (j + 1 >= kw.Length && string.CompareOrdinal(code, j + 1 - kw.Length, kw, 0, kw.Length) == 0)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        return false;
    }

    internal static string? Check(string text, int maxMs)
    {
        foreach (bool module in new[] { true, false })
        {
            var timer = Stopwatch.StartNew();
            TypeStripResult r;
            try
            {
                r = ScriptEngine.StripTypes(text, new TypeStripOptions { Module = module, FileName = "f.ts" });
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return "exception " + e.GetType().Name + ": " + e.Message + "\n" + e.StackTrace;
            }

            if (timer.ElapsedMilliseconds > maxMs)
            {
                return "slow: " + timer.ElapsedMilliseconds + " ms";
            }

            if (!r.Success)
            {
                if (r.Diagnostics.Count == 0)
                {
                    return "failed without a diagnostic";
                }

                foreach (var d in r.Diagnostics)
                {
                    if (d.PrimarySpan.Start < 0 || d.PrimarySpan.End > text.Length + 1 || d.PrimarySpan.Start > d.PrimarySpan.End)
                    {
                        return "diagnostic span outside the source: " + d.PrimarySpan + " in " + text.Length;
                    }

                    _ = d.ToString();
                }

                continue;
            }

            string code = r.Code!;
            if (code.Length != text.Length)
            {
                return "length changed";
            }

            for (int i = 0; i < text.Length; i++)
            {
                char a = text[i];
                char b = code[i];
                bool lt = a is '\n' or '\r' or '\u2028' or '\u2029';
                if (lt ? a != b : (a != b && b is not (' ' or ';' or '=' or '>')))
                {
                    return "unexpected change at " + i;
                }
            }

            if (ScriptEngine.Check(code, new ParseOptions { Goal = module ? ParseGoal.Module : ParseGoal.Script }).Count > 0)
            {
                return "output does not parse";
            }

            if (module)
            {
                string? asi = CheckBoundaries(text, code, module);
                if (asi is not null)
                {
                    return asi;
                }
            }

            // The output is JavaScript, where "a<b>(c)" is a comparison but TypeScript reads a call, so a second pass may legitimately
            // change it; it must still be safe and its result must still parse.
            TypeStripResult again = ScriptEngine.StripTypes(code, new TypeStripOptions { Module = module });
            if (again.Success && ScriptEngine.Check(again.Code!, new ParseOptions { Goal = module ? ParseGoal.Module : ParseGoal.Script }).Count > 0)
            {
                return "second pass output does not parse";
            }
        }

        return null;
    }
}
