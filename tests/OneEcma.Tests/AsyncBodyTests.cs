using RecursiveAI.OneEcma.Diagnostics;
using RecursiveAI.OneEcma.SourceMaps;
using RecursiveAI.OneEcma.Tests.SourceMaps;
using RecursiveAI.OneEcma.Tests.Fixtures;

namespace RecursiveAI.OneEcma.Tests.Fixtures
{
    [JsExport]
    public record Basket(int Id, List<int> LineItems, int Total);

    /// <summary>Host methods that hand tasks to script in every state a task can be in when it arrives.</summary>
    [JsExport]
    public static class Depot
    {
        public static Task<Basket> Ready(int id) => Task.FromResult(new Basket(id, [4, 5, 6], 15));

        public static async Task<Basket> Later(int id)
        {
            await Task.Delay(5);
            return new Basket(id, [4, 5, 6], 15);
        }

        public static Task<Basket> Faulted() => Task.FromException<Basket>(new InvalidOperationException("depot is closed"));

        public static Task<Basket> Cancelled() => Task.FromCanceled<Basket>(new CancellationToken(true));

        public static Task Done() => Task.CompletedTask;

        public static Task FaultedPlain() => Task.FromException(new InvalidOperationException("plain failure"));

        public static Task<Basket> ThrowsAtOnce() => throw new InvalidOperationException("threw before returning a task");
    }
}

namespace RecursiveAI.OneEcma.Tests
{
    public sealed class AsyncBodyTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static void World(ScriptEngine e)
        {
            JsExports.InstallAll(e.MainRealm);
            JsRealm r = e.MainRealm;
            JsValue Obj(JsRealm rr, int id)
            {
                JsObject o = rr.NewObject();
                rr.DefineData(o, "id", JsValue.FromNumber(id));
                rr.DefineData(o, "lineItems", JsValue.FromObject(rr.NewArray([JsValue.FromNumber(4), JsValue.FromNumber(5)])));
                return JsValue.FromObject(o);
            }
            r.DefineData(r.Global, "readyFn", JsValue.FromObject(r.NewFunction("readyFn", 1, (_, a) =>
            {
                int id = (int)r.ToNumber(a[0]);
                return JsMarshal.FromTask(r, "readyFn", () => Task.FromResult(id), Obj);
            })), JsAttributes.Method);
            r.DefineData(r.Global, "faultedFn", JsValue.FromObject(r.NewFunction("faultedFn", 0, (_, _) => JsMarshal.FromTask(r, "faultedFn", () => Task.FromException<int>(new InvalidOperationException("fn faulted")), Obj))), JsAttributes.Method);
            r.DefineData(r.Global, "cancelledFn", JsValue.FromObject(r.NewFunction("cancelledFn", 0, (_, _) => JsMarshal.FromTask(r, "cancelledFn", () => Task.FromCanceled<int>(new CancellationToken(true)), Obj))), JsAttributes.Method);
            r.DefineData(r.Global, "doneFn", JsValue.FromObject(r.NewFunction("doneFn", 0, (_, _) => JsMarshal.FromTask(r, "doneFn", () => Task.CompletedTask))), JsAttributes.Method);
        }

        private static async Task<(ScriptEngine Engine, JsValue Value, JsException? Error)> RunAsync(string body, string file = "body.js")
        {
            var engine = new ScriptEngine(new ScriptEngineOptions { Configure = World });
            try
            {
                JsValue p = engine.EvaluateAsyncBody(body, file, Ct);
                return (engine, await engine.AwaitAsync(p, Ct), null);
            }
            catch (JsException e)
            {
                return (engine, default, e);
            }
        }

        private static string Show(ScriptEngine e, JsValue v) => e.ToDisplayString(v);

        // ---- EvaluateAsyncBody ---------------------------------------------------------------------------------

        [Fact]
        public async Task TopLevelAwaitAndReturnWork()
        {
            var (engine, value, error) = await RunAsync("const a = await Promise.resolve(20);\nconst b = await readyFn(1);\nreturn a + b.id + b.lineItems.length;");
            Assert.Null(error);
            Assert.Equal("23", Show(engine, value));
        }

        [Fact]
        public void BodyWithoutReturnFulfilsWithUndefinedAndReturnedPromiseIsTheCompletion()
        {
            var engine = new ScriptEngine();
            JsValue p = engine.EvaluateAsyncBody("await 1; const x = 2;", "a.js", Ct);
            Assert.IsType<PromiseObject>(p.Ref);
            Assert.Equal("undefined", Show(engine, engine.Await(p, Ct)));
        }

        [Fact]
        public void DeclarationsAreLocalToTheCall()
        {
            var engine = new ScriptEngine();
            const string body = "const x = 1; let y = 2; var z = 3; function f() { return x + y + z; } class K {} return f();";
            Assert.Equal("6", Show(engine, engine.Await(engine.EvaluateAsyncBody(body, "one.js", Ct), Ct)));
            Assert.Equal("6", Show(engine, engine.Await(engine.EvaluateAsyncBody(body, "two.js", Ct), Ct)));
            Assert.Equal("undefined|undefined|undefined|undefined|undefined", Show(engine, engine.Evaluate("[typeof x, typeof y, typeof z, typeof f, typeof K].join('|')", cancellationToken: Ct)));
            Assert.False(engine.Global.HasOwnProperty("z"));
        }

        [Fact]
        public void FreeNamesAndThisAreTheGlobalScopeAndStrictnessIsOptIn()
        {
            var engine = new ScriptEngine();
            engine.Evaluate("var shared = 5;", cancellationToken: Ct);
            Assert.Equal("5|true", Show(engine, engine.Await(engine.EvaluateAsyncBody("return shared + '|' + (this === globalThis);", cancellationToken: Ct), Ct)));
            // Sloppy by default: an undeclared assignment creates a global; "use strict" makes it an error.
            engine.Await(engine.EvaluateAsyncBody("leaked = 1;", cancellationToken: Ct), Ct);
            Assert.Equal("1", Show(engine, engine.Evaluate("leaked", cancellationToken: Ct)));
            JsException e = Assert.Throws<JsException>(() => engine.EvaluateAsyncBody("'use strict';\nundeclared = 1;", "s.js", Ct));
            Assert.Contains("undeclared is not defined", e.Report!.Headline);
        }

        [Fact]
        public void PositionsAreThoseOfTheSourceWithNoWrapper()
        {
            var engine = new ScriptEngine();
            JsException e = Assert.Throws<JsException>(() => engine.EvaluateAsyncBody("\n\n  await 0;\n  return null.x;", "pos.js", Ct));
            Assert.Equal((4, 10), (e.Report!.Position.Line, e.Report.Position.Column));
            Assert.Equal("pos.js", e.Report.Source.Name);
            Assert.Equal((4, 15), (e.Report.Stack[0].Position.Line, e.Report.Stack[0].Position.Column));
        }

        [Fact]
        public void ErrStackIsV8Format()
        {
            var engine = new ScriptEngine();
            JsValue p = engine.EvaluateAsyncBody("await 0;\ntry { null.x } catch (e) { return e.stack }", "s.js", Ct);
            Assert.Equal("TypeError: Cannot read properties of null (reading 'x')\n    at s.js:2:12", Show(engine, engine.Await(p, Ct)));
        }

        [Fact]
        public void CheckReportsEverySyntaxErrorWithSourcePositions()
        {
            IReadOnlyList<Diagnostic> ds = ScriptEngine.CheckAsyncBody("await foo(;\nfunction f() { await 1 }\n", "c.js", Ct);
            Assert.Equal(2, ds.Count);
            Assert.Equal((1, 11), (ds[0].Position.Line, ds[0].Position.Column));
            Assert.Equal((2, 16), (ds[1].Position.Line, ds[1].Position.Column));
            Assert.Empty(ScriptEngine.CheckAsyncBody("await x; return 1;", cancellationToken: Ct));
            Assert.Single(ScriptEngine.CheckAsyncBody("let a; let a;", cancellationToken: Ct));
        }

        [Fact]
        public void ASyntaxErrorThrowsAndRepairWorksWithTheBodyGoal()
        {
            var engine = new ScriptEngine();
            JsException e = Assert.Throws<JsException>(() => engine.EvaluateAsyncBody("const x = await foo(;", "bad.js", Ct));
            Assert.Equal("SyntaxError", e.Report!.Kind);

            RepairResult r = ScriptEngine.CheckAndRepairAsyncBody("const x = await Promise.resolve(\u201chi\u201d);\nreturn x;", "r.js", cancellationToken: Ct);
            Assert.True(r.Changed);
            Assert.True(r.Parses);
            Assert.Equal("hi", Show(engine, engine.Await(engine.EvaluateAsyncBody(r.RepairedSource, "r.js", Ct), Ct)));
            RepairResult same = ScriptEngine.CheckAndRepairAsyncBody("await 1; return 2;", cancellationToken: Ct);
            Assert.False(same.Changed);
        }

        [Fact]
        public async Task BodyRecordsAndReplaysLikeAnyEntry()
        {
            var (engine, value, error) = await RunAsync("const b = await Depot.later(7); return b.id * 2 + (await readyFn(3)).id;");
            Assert.Null(error);
            Assert.True(engine.RecordLog.IsReplayable);
            ReplayResult r = ScriptEngine.Replay(engine.RecordLog, new ReplayOptions { Configure = World }, Ct);
            Assert.Null(r.Divergence);
            Assert.Equal(Show(engine, value), Show(r.Engine, r.Engine.EntryPromise(0)!.Result));
            Assert.Equal("17", Show(engine, value));
        }

        // ---- the explained report ------------------------------------------------------------------------------

        [Fact]
        public async Task AwaitingAHostFunctionThenFailingGivesTheFullReport()
        {
            var (_, _, e) = await RunAsync("const b = await Depot.later(1);\nreturn b.items.length;", "checkout.js");
            Assert.NotNull(e);
            Assert.Equal(
                "error[TypeError]: Cannot read properties of undefined (reading 'length')\n"
                + " --> checkout.js:2:10\n"
                + "  |\n"
                + "1 | const b = await Depot.later(1);\n"
                + "  |           -------------------- `b` comes from here: { id, lineItems, total }\n"
                + "2 | return b.items.length;\n"
                + "  |          ^^^^^ `b.items` is undefined\n"
                + "  |\n"
                + "  = help: did you mean `b.lineItems`? (Array, 3 items)\n"
                + "  = stack: <top-level> (checkout.js:2)\n",
                DiagnosticRenderer.Render(e.Report!));
        }

        [Fact]
        public async Task TheAsyncStackIsStitchedThroughAnAwaitedCall()
        {
            var (_, _, e) = await RunAsync("async function total(id) {\n  const b = await Depot.later(id);\n  return b.items.length;\n}\nreturn await total(3);");
            string text = DiagnosticRenderer.Render(e!.Report!);
            Assert.Contains("`b` comes from here: { id, lineItems, total }", text);
            Assert.Contains("= help: did you mean `b.lineItems`? (Array, 3 items)", text);
            Assert.Contains("= stack: total (body.js:3) \u2190 async <top-level> (body.js:5)", text);
        }

        // ---- tasks that are already done (and failed, and cancelled) when they reach the engine ----------------

        public static TheoryData<string, string> TaskCases => new()
        {
            { "return (await Depot.ready(2)).id;", "2" },
            { "return (await Depot.later(3)).id;", "3" },
            { "return String(await Depot.done());", "undefined" },
            { "return (await readyFn(4)).id;", "4" },
            { "return String(await doneFn());", "undefined" },
            { "try { await Depot.faulted(); } catch (e) { return e.message; }", "InvalidOperationException: depot is closed" },
            { "try { await Depot.faultedPlain(); } catch (e) { return e.message; }", "InvalidOperationException: plain failure" },
            { "try { await Depot.cancelled(); } catch (e) { return e.message; }", "TaskCanceledException: A task was canceled." },
            { "try { await Depot.throwsAtOnce(); } catch (e) { return e.message; }", "InvalidOperationException: threw before returning a task" },
            { "try { await faultedFn(); } catch (e) { return e.message; }", "InvalidOperationException: fn faulted" },
            { "try { await cancelledFn(); } catch (e) { return e.message; }", "TaskCanceledException: A task was canceled." },
            { "const ps = [Depot.ready(1), Depot.faulted(), Depot.later(2), Depot.cancelled(), Depot.ready(3)]; const r = await Promise.allSettled(ps); return r.map(x => x.status).join();", "fulfilled,rejected,fulfilled,rejected,fulfilled" },
        };

        [Theory]
        [MemberData(nameof(TaskCases))]
        public async Task ATaskInAnyStateRecordsAndReplaysWithoutDivergence(string body, string expected)
        {
            var (engine, value, error) = await RunAsync(body);
            Assert.Null(error);
            Assert.Equal(expected, Show(engine, value));
            Assert.True(engine.RecordLog.IsReplayable);
            foreach (bool trace in new[] { false, true })
            {
                ReplayResult r = ScriptEngine.Replay(engine.RecordLog, new ReplayOptions { Configure = World, Trace = trace }, Ct);
                Assert.Null(r.Divergence);
                Assert.Equal(expected, Show(r.Engine, r.Engine.EntryPromise(0)!.Result));
            }
        }

        [Theory]
        [InlineData("await Depot.faulted(); return 1;", "InvalidOperationException: depot is closed")]
        [InlineData("await Depot.cancelled(); return 1;", "TaskCanceledException: A task was canceled.")]
        [InlineData("await Depot.throwsAtOnce(); return 1;", "InvalidOperationException: threw before returning a task")]
        [InlineData("await faultedFn(); return 1;", "InvalidOperationException: fn faulted")]
        [InlineData("await cancelledFn(); return 1;", "TaskCanceledException: A task was canceled.")]
        public async Task AnUncaughtTaskFailureIsExplainedAndSaysNothingWasLost(string body, string headline)
        {
            var (_, _, e) = await RunAsync(body);
            Assert.NotNull(e);
            string text = DiagnosticRenderer.Render(e.Report!);
            Assert.Equal(headline, e.Report!.Headline);
            Assert.DoesNotContain("explanation unavailable", text);
            Assert.Contains("= stack:", text);
            Assert.Contains("body.js:1", text);
        }

        [Fact]
        public async Task AReportFromAnAlreadyCompleteTaskIsTheSameAsFromALateOne()
        {
            string Body(string call) => "const b = await " + call + ";\nreturn b.items.length;";
            var (_, _, ready) = await RunAsync(Body("Depot.ready(1)"));
            var (_, _, later) = await RunAsync(Body("Depot.later(1)"));
            Assert.Equal(DiagnosticRenderer.Render(later!.Report!).Replace("Depot.later(1)", "Depot.ready(1)"), DiagnosticRenderer.Render(ready!.Report!));
        }

        [Fact]
        public async Task ReplayingTwiceGivesTheSameReportBytes()
        {
            var (engine, _, e) = await RunAsync("const b = await Depot.later(1);\nreturn b.items.length;");
            string first = DiagnosticRenderer.Render(e!.Report!);
            string again = DiagnosticRenderer.Render(Assert.IsType<JsException>(await ReplayRejection(engine)).Report!);
            Assert.Equal(first, again);
        }

        private static async Task<JsException> ReplayRejection(ScriptEngine engine)
        {
            ReplayResult r = ScriptEngine.Replay(engine.RecordLog, new ReplayOptions { Configure = World }, Ct);
            Assert.Null(r.Divergence);
            PromiseObject p = r.Engine.EntryPromise(0)!;
            Assert.Equal(2, p.State);
            try
            {
                await r.Engine.AwaitAsync(JsValue.FromObject(p), Ct);
            }
            catch (JsException e)
            {
                return e;
            }

            throw new InvalidOperationException("the replay did not fail");
        }

        // ---- a lost explanation says so ------------------------------------------------------------------------

        [Fact]
        public void AnUnreplayableRunNamesTheReason()
        {
            var engine = new ScriptEngine();
            engine.SetProperty(engine.Global, "z", JsValue.FromNumber(1));
            JsException e = Assert.Throws<JsException>(() => engine.Evaluate("null.x", "n.js", Ct));
            Assert.Contains("= note: explanation unavailable: the run cannot be replayed: ScriptEngine.SetProperty was used", DiagnosticRenderer.Render(e.Report!));
        }

        [Fact]
        public void AReplayThatBehavesDifferentlySaysSo()
        {
            int worlds = 0;
            var engine = new ScriptEngine(new ScriptEngineOptions { Configure = e => e.MainRealm.DefineData(e.Global, "n", JsValue.FromNumber(++worlds)) });
            JsException ex = Assert.Throws<JsException>(() => engine.Evaluate("if (n === 1) null.x;", "d.js", Ct));
            Assert.Contains("= note: explanation unavailable: the replay diverged from the recorded run: the recorded call failed with", DiagnosticRenderer.Render(ex.Report!));
        }

        [Fact]
        public async Task ARejectionNotFromAnEntryNamesWhyItIsNotExplained()
        {
            var engine = new ScriptEngine(new ScriptEngineOptions { ThrowOnUnhandledRejection = false });
            JsValue holder = engine.Evaluate("({ p: Promise.reject(new TypeError('late')) })", cancellationToken: Ct);
            JsValue p = engine.GetProperty(holder.AsObject!, "p");
            JsException e = await Assert.ThrowsAsync<JsException>(() => engine.AwaitAsync(p, Ct));
            Assert.Contains("= note: explanation unavailable: the rejected promise was not the result of an entry call", DiagnosticRenderer.Render(e.Report!));
        }

        // ---- source maps keep the whole report -----------------------------------------------------------------

        [Fact]
        public async Task RemapKeepsStackProvenanceSuggestionsAndLocalsAndMovesTheirPositions()
        {
            var (_, _, e) = await RunAsync("async function total(id) {\n  const b = await Depot.later(id);\n  return b.items.length;\n}\nreturn await total(3);", "gen.js");
            Diagnostic d = e!.Report!;
            Assert.NotEmpty(d.Stack);
            Assert.NotEmpty(d.Provenance);
            Assert.NotEmpty(d.Suggestions);
            Assert.Contains(d.Stack, f => f.Locals is { Count: > 0 });

            // Generated line g (0-based) is original line g + 10 of order.ts, same columns.
            string mappings = SourceMapTests.Encode((0, 0, 0, 10, 0, -1), (1, 0, 0, 11, 0, -1), (2, 0, 0, 12, 0, -1), (3, 0, 0, 13, 0, -1), (4, 0, 0, 14, 0, -1));
            string original = string.Concat(Enumerable.Repeat("// header\n", 10)) + "async function total(id) {\n  const b = await Depot.later(id);\n  return b.items.length;\n}\nreturn await total(3);\n";
            string json = "{\"version\":3,\"sources\":[\"order.ts\"],\"sourcesContent\":[" + System.Text.Json.JsonSerializer.Serialize(original) + "],\"names\":[],\"mappings\":\"" + mappings + "\"}";
            SourceMap map = SourceMap.Parse(json).Map!;
            Diagnostic m = SourceMapReports.Remap(d, map);

            Assert.Equal("order.ts", m.Source.Name);
            Assert.Equal((13, 12), (m.Position.Line, m.Position.Column));
            Assert.Equal(d.Stack.Count, m.Stack.Count);
            Assert.Equal(d.Provenance.Count, m.Provenance.Count);
            Assert.Equal(d.Suggestions, m.Suggestions);
            for (int i = 0; i < d.Stack.Count; i++)
            {
                Assert.Equal(d.Stack[i].Function, m.Stack[i].Function);
                Assert.Equal(d.Stack[i].IsAsync, m.Stack[i].IsAsync);
                Assert.Equal(d.Stack[i].Locals, m.Stack[i].Locals);
                Assert.Equal("order.ts", m.Stack[i].Source.Name);
                Assert.Equal(d.Stack[i].Position.Line + 10, m.Stack[i].Position.Line);
                Assert.Equal(d.Stack[i].Position.Column, m.Stack[i].Position.Column);
            }

            Assert.Equal(d.Provenance[0].Description, m.Provenance[0].Description);
            Assert.Equal(d.Provenance[0].Span.Length, m.Provenance[0].Span.Length);
            Assert.Equal(12, m.Source.GetPosition(m.Provenance[0].Span.Start).Line);
            string text = DiagnosticRenderer.Render(m);
            Assert.Contains("= stack: total (order.ts:13) \u2190 async <top-level> (order.ts:15)", text);
            Assert.Contains("= help: did you mean `b.lineItems`?", text);
            Assert.Contains("`b` comes from here", text);

            // Without the original text the report stays about the generated code and still carries every member.
            Diagnostic bare = SourceMapReports.Remap(d, SourceMap.Parse("{\"version\":3,\"sources\":[\"order.ts\"],\"names\":[],\"mappings\":\"" + mappings + "\"}").Map!);
            Assert.Equal(d.Stack.Count, bare.Stack.Count);
            Assert.Equal(d.Provenance.Count, bare.Provenance.Count);
            Assert.Equal(d.Suggestions, bare.Suggestions);
        }
    }
}
