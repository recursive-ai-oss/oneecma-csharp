namespace RecursiveAI.OneEcma.Cli;

/// <summary>The command-line host: modules are files, resolved relative to the importing module.</summary>
internal sealed class FileHost : DefaultHost
{
    public override HostModule? ResolveModule(string specifier, string? referrerId, IReadOnlyDictionary<string, string> attributes)
    {
        string baseDir = referrerId is not null && Path.IsPathRooted(referrerId) ? Path.GetDirectoryName(referrerId)! : Directory.GetCurrentDirectory();
        string full = Path.GetFullPath(specifier, baseDir);
        return File.Exists(full) ? new HostModule(full, File.ReadAllText(full)) : null;
    }
}
