using RecursiveAI.OneEcma.Syntax;
using RecursiveAI.OneEcma.Text;

namespace RecursiveAI.OneEcma.Tests;

// test262 is plain JavaScript, so stripping must hand every valid file back untouched. The check runs when ONEECMA_TEST262_DIR
// names a test262 checkout and is a no-op otherwise.
public class TypeStripTest262SampleTests
{
    [Fact]
    public void FirstTwoThousandLanguageFilesComeBackUnchanged()
    {
        string? root = Environment.GetEnvironmentVariable("ONEECMA_TEST262_DIR");
        if (root is null || !Directory.Exists(Path.Combine(root, "test", "language")))
        {
            return;
        }

        // ONEECMA_TEST262_SAMPLE widens the sample (the whole directory is about 28,000 files).
        int take = int.TryParse(Environment.GetEnvironmentVariable("ONEECMA_TEST262_SAMPLE"), out int n) ? n : 2000;
        var files = Directory.EnumerateFiles(Path.Combine(root, "test", "language"), "*.js", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("_FIXTURE.js", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal)
            .Take(take)
            .ToList();
        int checkedFiles = 0;
        var problems = new List<string>();
        foreach (string file in files)
        {
            string src = File.ReadAllText(file);
            int meta = src.IndexOf("/*---", StringComparison.Ordinal);
            string header = meta >= 0 ? src[meta..Math.Min(src.Length, src.IndexOf("---*/", meta, StringComparison.Ordinal) + 5)] : "";
            if (header.Contains("negative:", StringComparison.Ordinal))
            {
                continue;
            }

            bool module = header.Contains("module", StringComparison.Ordinal) && System.Text.RegularExpressions.Regex.IsMatch(header, @"flags:.*\bmodule\b");
            checkedFiles++;
            TypeStripResult r = ScriptEngine.StripTypes(src, new TypeStripOptions { FileName = file, Module = module }, TestContext.Current.CancellationToken);
            string rel = file[root.Length..];
            if (!r.Success)
            {
                // Source the engine's own parser rejects (syntax it does not have yet) is not the stripper's concern.
                if (JsParser.Parse(src, new ParseOptions { Goal = module ? ParseGoal.Module : ParseGoal.Script }, TestContext.Current.CancellationToken).HasErrors)
                {
                    continue;
                }

                problems.Add("rejected " + rel + ": " + r.Diagnostics[0].Headline);
            }
            else if (!ReferenceEquals(r.Code, src))
            {
                problems.Add("changed " + rel);
            }
            else
            {
                // StripTypes also passes through source the reader stumbled on; the reader itself should understand plain JavaScript.
                var s = new TypeStripper(new SourceText(src, file), TestContext.Current.CancellationToken);
                s.Run();
                if (s.Diagnostics.Count > 0 || s.HasEdits)
                {
                    problems.Add("reader " + (s.HasEdits ? "edited " : "stumbled on ") + rel + (s.Diagnostics.Count > 0 ? ": " + s.Diagnostics[0].Headline : ""));
                }
            }
        }

        Assert.True(checkedFiles > files.Count * 3 / 4, "files checked: " + checkedFiles);
        Assert.True(problems.Count == 0, problems.Count + " of " + checkedFiles + " files:\n" + string.Join("\n", problems.Take(40)));
    }
}
