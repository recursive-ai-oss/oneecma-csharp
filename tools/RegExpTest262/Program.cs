using RecursiveAI.OneEcma.RegExpTest262;

// Usage: oneecma-regexp-test262 <test262-root> [--list-failures]
// Runs the translators over the directories listed below and prints per-directory pass counts.
string root = args.Length > 0 ? args[0] : throw new ArgumentException("usage: oneecma-regexp-test262 <test262-root> [--list-failures]");
bool list = args.Contains("--list-failures");
string? only = args.SkipWhile(a => a != "--dir").Skip(1).FirstOrDefault();
string? filter = args.SkipWhile(a => a != "--filter").Skip(1).FirstOrDefault();
bool slow = args.Contains("--slow");
string[] generated =
[
    "built-ins/RegExp/property-escapes/generated",
    "built-ins/RegExp/unicodeSets/generated",
    "built-ins/RegExp/CharacterClassEscapes",
];
string[] literals =
[
    "language/literals/regexp",
    "built-ins/RegExp/property-escapes",
    "built-ins/RegExp/named-groups",
    "built-ins/RegExp/lookBehind",
    "built-ins/RegExp/unicodeSets",
    "built-ins/RegExp/match-indices",
    "annexB/built-ins/RegExp",
];
int failures = 0;
foreach (string dir in generated.Concat(literals))
{
    bool isGenerated = generated.Contains(dir);
    if ((only is not null && dir != only) || !Directory.Exists(Path.Combine(root, "test", dir)))
    {
        continue;
    }

    var results = new List<FileResult>();
    var sw = System.Diagnostics.Stopwatch.StartNew();
    foreach (var r in isGenerated ? Harness.RunGenerated(root, dir, filter) : Harness.RunLiterals(root, dir, filter))
    {
        if (slow && sw.ElapsedMilliseconds > 1500)
        {
            Console.WriteLine($"  slow {r.Path}: {sw.ElapsedMilliseconds} ms");
        }

        sw.Restart();
        results.Add(r);
    }

    int pass = results.Count(r => r.Passed);
    Console.WriteLine($"{(isGenerated ? "data" : "literals"),-8} {dir,-48} {pass}/{results.Count}");
    foreach (var r in results.Where(r => !r.Passed))
    {
        failures++;
        if (list)
        {
            Console.WriteLine($"  FAIL {r.Path}: {r.Detail}");
        }
    }
}

return failures == 0 ? 0 : 1;
