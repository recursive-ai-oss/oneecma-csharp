using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace RecursiveAI.OneEcma.Test262;

/// <summary>
/// Runs each test in its own Node process through a small host shim, as a black box. It
/// validates the harness itself (Node should pass nearly everything) and later serves as a
/// differential reference.
/// </summary>
public sealed class NodeBackend : ITestBackend, IDisposable
{
    private readonly string _nodePath;
    private readonly string _hostDir;
    private readonly string _hostScript;

    public NodeBackend(string nodePath)
    {
        _nodePath = nodePath;
        _hostDir = Path.Combine(Path.GetTempPath(), "oneecma-test262-" + Environment.ProcessId);
        Directory.CreateDirectory(_hostDir);
        _hostScript = Path.Combine(_hostDir, "node-host.js");
        using Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("node-host.js")
            ?? throw new InvalidOperationException("node-host.js is not embedded");
        using FileStream f = File.Create(_hostScript);
        s.CopyTo(f);
    }

    public string Name => "node";

    public bool SupportsParseOnly => true;

    // Node's main thread may block in Atomics.wait.
    public bool CanBlock => true;

    // IsHTMLDDA needs an engine-level object that scripts cannot construct.
    public IReadOnlySet<string> UnsupportedFeatures { get; } = new HashSet<string>(StringComparer.Ordinal) { "IsHTMLDDA" };

    public void Dispose()
    {
        try
        {
            Directory.Delete(_hostDir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a leftover temp directory is harmless.
        }
    }

    public async Task<BackendResult> RunAsync(PreparedTest test, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(test);
        var psi = new ProcessStartInfo(_nodePath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("--expose-gc");
        psi.ArgumentList.Add("--experimental-vm-modules");
        psi.ArgumentList.Add("--no-warnings");
        psi.ArgumentList.Add(_hostScript);

        using Process p = Process.Start(psi) ?? throw new InvalidOperationException("cannot start node");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        Task<string> stdout = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> stderr = p.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await p.StandardInput.WriteAsync(BuildRequest(test)).ConfigureAwait(false);
            p.StandardInput.Close();
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            p.Kill(entireProcessTree: true);
            await p.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new BackendResult(true, TimedOut: true);
        }
        catch (IOException)
        {
            // Node exited before reading all of stdin; its output tells why.
        }

        string output = await stdout.ConfigureAwait(false);
        string line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        if (line.Length == 0)
        {
            string err = await stderr.ConfigureAwait(false);
            return new BackendResult(true, "runtime", "HostError", $"node exited {p.ExitCode} without a result: {err}");
        }

        using JsonDocument doc = JsonDocument.Parse(line);
        JsonElement root = doc.RootElement;
        if (root.GetProperty("ok").GetBoolean())
        {
            return BackendResult.Passed;
        }

        return new BackendResult(
            true,
            root.GetProperty("phase").GetString() ?? "",
            root.GetProperty("name").GetString() ?? "",
            root.GetProperty("message").GetString() ?? "");
    }

    private static string BuildRequest(PreparedTest test)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("file", test.FullPath);
            w.WriteBoolean("module", test.Mode == SourceMode.Module);
            w.WriteBoolean("parseOnly", test.ParseOnly);
            w.WriteBoolean("async", test.IsAsync);
            w.WriteString("source", test.EffectiveSource);
            w.WriteStartArray("scripts");
            foreach (HarnessScript h in test.Harness)
            {
                w.WriteStartObject();
                w.WriteString("name", h.Name);
                w.WriteString("source", h.Source);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
