using System.ComponentModel;
using System.Diagnostics;

namespace XREngine.AgentRuntimes;

/// <summary>Runs Claude Code with an isolated subscription profile and an empty work folder.</summary>
internal sealed class ClaudeRuntimeProfile : IAsyncDisposable
{
    private readonly FileStream _accountLease;

    private ClaudeRuntimeProfile(FileStream accountLease, string workDirectory)
    {
        _accountLease = accountLease;
        WorkDirectory = workDirectory;
    }

    public string WorkDirectory { get; }

    private static string ProfileDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "XREngine", "AgentRuntimes", "Claude", "profile");

    public static Task<ClaudeRuntimeProfile> OpenAsync(LocalCliRuntimeOptions options, bool signingIn, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string profile = ProfileDirectory;
        Directory.CreateDirectory(profile);
        string lockPath = Path.Combine(profile, "account-session.lock");
        if (!File.Exists(lockPath))
        {
            try
            {
                using var created = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            }
            catch (IOException) when (File.Exists(lockPath))
            {
                // Another editor created the profile lock file first.
            }
        }
        FileStream lease = new(lockPath, FileMode.Open,
            signingIn ? FileAccess.ReadWrite : FileAccess.Read,
            signingIn ? FileShare.None : FileShare.Read);
        try
        {
            string sessions = options.SessionDirectory ?? Path.Combine(Path.GetDirectoryName(profile)!, "sessions");
            string workDirectory = Path.Combine(Path.GetFullPath(sessions), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDirectory);
            return Task.FromResult(new ClaudeRuntimeProfile(lease, workDirectory));
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public ProcessStartInfo CreateStartInfo(LocalCliRuntimeOptions options, params string[] arguments)
    {
        string executable = ResolveExecutable(options.ExecutablePath);
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = WorkDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
            info.ArgumentList.Add(argument);
        // Keep only operating-system process variables. Do not inherit API keys,
        // alternate endpoints, hooks, plugins, or another Claude configuration.
        foreach (string name in info.Environment.Keys.ToArray())
        {
            if (name.Equals("SystemRoot", StringComparison.OrdinalIgnoreCase)
                || name.Equals("WINDIR", StringComparison.OrdinalIgnoreCase)
                || name.Equals("PATH", StringComparison.OrdinalIgnoreCase)
                || name.Equals("PATHEXT", StringComparison.OrdinalIgnoreCase)
                || name.Equals("TEMP", StringComparison.OrdinalIgnoreCase)
                || name.Equals("TMP", StringComparison.OrdinalIgnoreCase)
                || name.Equals("APPDATA", StringComparison.OrdinalIgnoreCase)
                || name.Equals("LOCALAPPDATA", StringComparison.OrdinalIgnoreCase)
                || name.Equals("USERPROFILE", StringComparison.OrdinalIgnoreCase)
                || name.Equals("HOME", StringComparison.OrdinalIgnoreCase))
                continue;
            info.Environment.Remove(name);
        }
        info.Environment["CLAUDE_CONFIG_DIR"] = ProfileDirectory;
        info.Environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
        return info;
    }

    private static string ResolveExecutable(string configured)
    {
        if (string.IsNullOrWhiteSpace(configured) || configured.IndexOfAny(['\0', '\r', '\n']) >= 0)
            throw new ArgumentException("Select an installed Claude Code executable.", nameof(configured));
        if (Path.IsPathFullyQualified(configured))
        {
            if (Path.GetExtension(configured).Equals(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(configured))
                return configured;
            throw new Win32Exception("Select the native Claude Code executable, not a command script.");
        }
        if (configured.Contains(Path.DirectorySeparatorChar) || configured.Contains(Path.AltDirectorySeparatorChar))
            throw new ArgumentException("Select an absolute Claude Code executable path.", nameof(configured));
        string file = configured.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? configured : configured + ".exe";
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory.Trim('"'), file);
            if (File.Exists(candidate))
                return candidate;
        }
        string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", file);
        if (File.Exists(local))
            return local;
        throw new Win32Exception("Claude Code is not installed. Install its native CLI and select claude.exe in Assistant settings.");
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
            // Claude Code can leave session files in the isolated work folder.
        }
        return ValueTask.CompletedTask;
    }
}
