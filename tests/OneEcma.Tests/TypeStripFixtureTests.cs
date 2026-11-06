using RecursiveAI.OneEcma.Diagnostics;
using RecursiveAI.OneEcma.Syntax;

namespace RecursiveAI.OneEcma.Tests;

public class TypeStripFixtureTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FileHost(string name, string source) : DefaultHost
    {
        public override HostModule? ResolveModule(string specifier, string? referrerId, IReadOnlyDictionary<string, string> attributes)
            => specifier == name ? new HostModule(name, source) : null;
    }

    // An order-review module written the way application code is: shared types from another file (type-only import, so it
    // leaves no import behind), interfaces, aliases, generics, `as const`, `satisfies`, optional parameters, a class with typed
    // fields and access modifiers, and an async function with a typed return.
    internal const string Decider = """
        import type { Order, Rule } from './types';

        type Verdict = 'approve' | 'review' | 'reject';

        interface Decision<T extends Order = Order> {
          readonly order: T;
          verdict: Verdict;
          reasons: readonly string[];
          score?: number;
        }

        declare const FAIL: boolean | undefined;

        const THRESHOLDS = { approve: 80, review: 40 } as const;

        const RULES = [
          { name: 'big', weight: -30, test: (o: Order): boolean => o.total > 1000 },
          { name: 'many', weight: -25, test: (o: Order): boolean => o.items.length > 3 },
          { name: 'known', weight: 15, test: (o: Order): boolean => o.customer?.name !== undefined },
        ] satisfies Rule[];

        function score<T extends Order>(order: T, base: number = 70): number {
          let s: number = base;
          for (const rule of RULES) {
            if (rule.test(order)) s += rule.weight;
          }
          return s;
        }

        function explode(order: Order): number {
          return order.customer!.name.length;
        }

        class Decider {
          private readonly seen: string[] = [];
          protected count = 0;
          public label: string;

          constructor(label: string) {
            this.label = label;
          }

          decide<T extends Order>(order: T, base?: number): Decision<T> {
            const s = score<T>(order, base);
            const verdict: Verdict = s >= THRESHOLDS.approve ? 'approve' : s >= THRESHOLDS.review ? 'review' : 'reject';
            this.count++;
            this.seen.push(order.id);
            const reasons: string[] = RULES.filter((r): boolean => r.test(order)).map((r) => r.name);
            return { order, verdict, reasons, score: s };
          }

          get total(): number {
            return this.count;
          }
        }

        export async function reviewAll(orders: Order[]): Promise<string[]> {
          const decider = new Decider('main');
          const out: string[] = [];
          for (const o of orders) {
            const d = await Promise.resolve(decider.decide(o));
            out.push(`${d.order.id}:${d.verdict}:${d.score}:${d.reasons.join('+')}`);
          }
          out.push('total=' + decider.total);
          return out;
        }

        const orders: Order[] = [
          { id: 'a', total: 20, items: [1], customer: { name: 'Ann' } },
          { id: 'b', total: 5000, items: [1, 2, 3, 4], customer: { name: 'Bo' } },
          { id: 'c', total: 1500, items: [1], customer: undefined },
        ] as unknown as Order[];

        export let output: string = '';
        reviewAll(orders).then((lines: string[]) => { output = lines.join('|'); });
        /*FAIL*/
        """;

    private const string Expected = "a:approve:85:known|b:reject:30:big+many+known|c:review:40:big|total=3";

    private static string StripOrFail(string source)
    {
        TypeStripResult r = ScriptEngine.StripTypes(source, new TypeStripOptions { FileName = "decider.ts" }, Ct);
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => DiagnosticRenderer.Render(d))));
        return r.Code!;
    }

    [Fact]
    public void StripsAndRunsAsAModuleWithNoImportLeft()
    {
        string code = StripOrFail(Decider);
        Assert.DoesNotContain("import", code, StringComparison.Ordinal);
        Assert.Equal(Decider.Split('\n').Length, code.Split('\n').Length);
        var engine = new ScriptEngine(new ScriptEngineOptions { Host = new FileHost("decider.ts", code) });
        JsValue ns = engine.EvaluateModule("decider.ts", Ct);
        engine.RunJobs(Ct);
        Assert.Equal(Expected, engine.GetProperty(ns.AsObject!, "output").ToString());
    }

    [Fact]
    public void RuntimeErrorsReportTheTypeScriptLineAndColumn()
    {
        string source = Decider.Replace("/*FAIL*/", "explode(orders[2]);", StringComparison.Ordinal);
        string code = StripOrFail(source);
        var engine = new ScriptEngine(new ScriptEngineOptions { Host = new FileHost("decider.ts", code) });
        JsException e = Assert.Throws<JsException>(() => engine.EvaluateModule("decider.ts", Ct));
        Assert.Contains("Cannot read properties of undefined (reading 'name')", e.Report!.Headline, StringComparison.Ordinal);
        string[] lines = source.Split('\n');
        int line = e.Report.Position.Line;
        Assert.StartsWith("  return order.customer!.name.length;", lines[line - 1], StringComparison.Ordinal);
        // The column indexes the same character in the original: it is the start of the failing read.
        Assert.StartsWith("customer!.name", lines[line - 1][(e.Report.Position.Column - 1)..], StringComparison.Ordinal);
        Assert.Equal("decider.ts", e.Report.Source.Name);
        Assert.Contains("decider.ts:" + line + ":", DiagnosticRenderer.Render(e.Report), StringComparison.Ordinal);
    }

    [Fact]
    public void TheOnlyImportBeingTypeOnlyStillRunsAsAModule()
    {
        const string src = "import type { X } from './y';\nexport const v: unknown = 1 as unknown as number;\nexport interface Opt { readonly a?: string }\n";
        string code = StripOrFail(src);
        var engine = new ScriptEngine(new ScriptEngineOptions { Host = new FileHost("m.ts", code) });
        JsValue ns = engine.EvaluateModule("m.ts", Ct);
        Assert.Equal(1, engine.GetProperty(ns.AsObject!, "v").AsNumber);
    }
}
