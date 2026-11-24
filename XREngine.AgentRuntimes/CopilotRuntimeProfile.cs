namespace XREngine.AgentRuntimes;

/// <summary>
/// Holds the account lease while a Copilot CLI process uses its own profile.
/// Sign-in takes an exclusive lease so it cannot change an account during a run.
/// </summary>
internal sealed class CopilotRuntimeProfile : IAsyncDisposable
{
    private readonly FileStream _accountLease;

    private CopilotRuntimeProfile(FileStream accountLease, string workDirectory)
    {
        _accountLease = accountLease;
        WorkDirectory = workDirectory;
    }

    public static string HomeDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "XREngine", "AgentRuntimes", "Copilot", "profile");

    public string WorkDirectory { get; }

    public static Task<CopilotRuntimeProfile> OpenAsync(LocalCliRuntimeOptions options, bool signingIn, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string home = HomeDirectory;
        Directory.CreateDirectory(home);
        foreach (string name in new[]
        {
            "providers.json", "mcp-config.json", "copilot-instructions.md", "settings.json",
        })
        {
            if (File.Exists(Path.Combine(home, name)))
                throw new InvalidOperationException("The Copilot product profile contains unsupported custom configuration. Remove it before a subscription run.");
        }
        foreach (string name in new[] { "hooks", "installed-plugins", "extensions", "agents", "skills" })
        {
            string path = Path.Combine(home, name);
            if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
                throw new InvalidOperationException("The Copilot product profile contains unsupported extensions or hooks. Remove them before a subscription run.");
        }

        string lockPath = Path.Combine(home, "account-session.lock");
        if (!File.Exists(lockPath))
        {
            try
            {
                using var created = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            }
            catch (IOException) when (File.Exists(lockPath))
            {
                // Another editor created the account lease file first.
            }
        }
        FileStream lease = new(lockPath, FileMode.Open,
            signingIn ? FileAccess.ReadWrite : FileAccess.Read,
            signingIn ? FileShare.None : FileShare.Read);
        try
        {
            string sessions = options.SessionDirectory ?? Path.Combine(Path.GetDirectoryName(home)!, "sessions");
            string work = Path.Combine(Path.GetFullPath(sessions), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            return Task.FromResult(new CopilotRuntimeProfile(lease, work));
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        _accountLease.Dispose();
        try
        {
            Directory.Delete(WorkDirectory);
        }
        catch (IOException)
        {
            // Copilot can leave session files in its isolated work folder.
        }
        return ValueTask.CompletedTask;
    }
}
