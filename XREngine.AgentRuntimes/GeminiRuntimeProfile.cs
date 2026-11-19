using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XREngine.AgentRuntimes;

/// <summary>
/// Starts Gemini CLI in a product-owned Google login profile and an empty work folder.
/// The profile has no native tools or inherited API-key authentication.
/// </summary>
internal sealed class GeminiRuntimeProfile : IAsyncDisposable
{
    private const string ManagedSettings = """
        {
          "security": { "auth": { "selectedType": "oauth-personal" }, "folderTrust": { "enabled": false } },
          "tools": { "core": ["__xrengine_no_native_tools__"] },
          "mcp": { "allowed": ["xrengine-run"] },
          "advanced": { "ignoreLocalEnv": true },
          "experimental": { "enableAgents": false },
          "useWriteTodos": false
        }
        """;
    private readonly FileStream _settingsLease;
    private readonly FileStream _accountLease;
    private readonly string _workDirectory;

    private GeminiRuntimeProfile(FileStream settingsLease, FileStream accountLease, string workDirectory)
    {
        _settingsLease = settingsLease;
        _accountLease = accountLease;
        _workDirectory = workDirectory;
    }

    public static string HomeDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "XREngine", "AgentRuntimes", "Gemini", "profile");
    public string WorkDirectory => _workDirectory;

    public static async Task<GeminiRuntimeProfile> OpenAsync(LocalCliRuntimeOptions options, bool signingIn, CancellationToken token)
    {
        string home = HomeDirectory;
        string settingsDirectory = Path.Combine(home, ".gemini");
        Directory.CreateDirectory(settingsDirectory);
        string settingsPath = Path.Combine(settingsDirectory, "settings.json");
        if (!File.Exists(settingsPath))
        {
            try
            {
                using var file = new FileStream(settingsPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                file.Write(Encoding.UTF8.GetBytes(ManagedSettings));
            }
            catch (IOException) when (File.Exists(settingsPath))
            {
                // A second editor created the fixed settings file first.
            }
        }

        FileStream? settingsLease = null;
        FileStream? accountLease = null;
        try
        {
            settingsLease = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (settingsLease.Length > 16_384)
                throw new InvalidOperationException("The Gemini profile settings changed. Restore its managed settings before connecting.");
            using (var reader = new StreamReader(settingsLease, Encoding.UTF8, leaveOpen: true))
            {
                JsonNode? stored = JsonNode.Parse(await reader.ReadToEndAsync(token));
                if (!JsonNode.DeepEquals(stored, JsonNode.Parse(ManagedSettings)))
                    throw new InvalidOperationException("The Gemini profile settings changed. Restore its managed settings before connecting.");
            }

            string leasePath = Path.Combine(home, "account-session.lock");
            if (!File.Exists(leasePath))
            {
                try
                {
                    using var created = new FileStream(leasePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                }
                catch (IOException) when (File.Exists(leasePath))
                {
                    // A second editor created the lock file first.
                }
            }
            accountLease = new FileStream(leasePath, FileMode.Open,
                signingIn ? FileAccess.ReadWrite : FileAccess.Read,
                signingIn ? FileShare.None : FileShare.Read);
            string sessions = options.SessionDirectory ?? Path.Combine(Path.GetDirectoryName(home)!, "sessions");
            string workingDirectory = Path.Combine(Path.GetFullPath(sessions), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workingDirectory);
            return new GeminiRuntimeProfile(settingsLease, accountLease, workingDirectory);
        }
        catch
        {
            accountLease?.Dispose();
            settingsLease?.Dispose();
            throw;
        }
    }

    public async Task<string> ReadAccountFingerprintAsync(CancellationToken token)
    {
        string accountPath = Path.Combine(HomeDirectory, ".gemini", "google_accounts.json");
        var file = new FileInfo(accountPath);
        if (!file.Exists || file.Length is <= 0 or > 16_384)
            throw new InvalidOperationException("Sign in to Google in the Assistant connection settings before using Gemini subscription access.");
        JsonObject accounts = JsonNode.Parse(await File.ReadAllTextAsync(accountPath, token), documentOptions: new JsonDocumentOptions { MaxDepth = 8 }) as JsonObject
            ?? throw new InvalidDataException("Gemini returned invalid account metadata.");
        string account = accounts["active"]?.GetValue<string>() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(account) || account.Length > 320)
            throw new InvalidOperationException("Gemini has no active Google account. Sign in through the Assistant connection settings.");
        // The CLI stores its active account address separately from OAuth credentials.
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account.Trim().ToLowerInvariant())));
    }

    public async Task<string> ReadVersionAsync(LocalCliRuntimeOptions options, CancellationToken token)
    {
        using Process process = new() { StartInfo = CreateStartInfo(options, "--version") };
        if (!process.Start())
            throw new Win32Exception("Gemini CLI did not start.");
        Task stderrDrain = process.StandardError.BaseStream.CopyToAsync(Stream.Null, token);
        try
        {
            var version = new StringBuilder();
            char[] buffer = new char[1];
            while (version.Length < 64)
            {
                int count = await process.StandardOutput.ReadAsync(buffer, token);
                if (count == 0 || buffer[0] is '\n' or '\r')
                    break;
                version.Append(buffer[0]);
            }
            if (version.Length >= 64)
                throw new InvalidDataException("Gemini returned an invalid version string.");
            await process.WaitForExitAsync(token);
            await stderrDrain;
            string line = version.ToString().Trim();
            if (process.ExitCode != 0 || !line.StartsWith("0.67.", StringComparison.Ordinal))
                throw new InvalidOperationException("Gemini subscription access requires Gemini CLI 0.67.x with ACP model discovery.");
            return line;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    public ProcessStartInfo CreateStartInfo(LocalCliRuntimeOptions options, string argument)
    {
        string executable = options.ExecutablePath;
        if (string.IsNullOrWhiteSpace(executable))
            throw new ArgumentException("Select a Gemini CLI executable.", nameof(options));
        if (executable.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new ArgumentException("Select a Gemini CLI executable, not a shell command.", nameof(options));
        if (!Path.IsPathFullyQualified(executable))
        {
            string? found = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(directory => Path.Combine(directory.Trim('"'), executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ? executable : executable + ".cmd"))
                .FirstOrDefault(File.Exists);
            if (found is not null)
                executable = found;
        }
        if (executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            string fullShim = Path.GetFullPath(executable);
            string packageDirectory = Path.Combine(Path.GetDirectoryName(fullShim)!, "node_modules", "@google", "gemini-cli");
            string script = Path.Combine(packageDirectory, "bundle", "gemini.js");
            string packagePath = Path.Combine(packageDirectory, "package.json");
            JsonObject? package = File.Exists(packagePath) ? JsonNode.Parse(File.ReadAllText(packagePath)) as JsonObject : null;
            if (!File.Exists(script) || package?["name"]?.GetValue<string>() != "@google/gemini-cli"
                || package["bin"]?["gemini"]?.GetValue<string>() != "bundle/gemini.js")
                throw new InvalidOperationException("The selected Gemini CLI launcher has no adjacent installed Node entry point.");
            executable = Path.Combine(Path.GetDirectoryName(fullShim)!, "node.exe");
            if (!File.Exists(executable))
                executable = "node.exe";
            var nodeInfo = new ProcessStartInfo(executable) { WorkingDirectory = _workDirectory, UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            nodeInfo.ArgumentList.Add(script);
            nodeInfo.ArgumentList.Add(argument);
            ConfigureEnvironment(nodeInfo);
            return nodeInfo;
        }

        if (Path.GetExtension(executable) is ".bat" or ".ps1" or ".js")
            throw new ArgumentException("Select a Gemini CLI executable or its official npm launcher.", nameof(options));
        var start = new ProcessStartInfo(executable) { WorkingDirectory = _workDirectory, UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add(argument);
        ConfigureEnvironment(start);
        return start;
    }

    private static void ConfigureEnvironment(ProcessStartInfo start)
    {
        start.Environment.Clear();
        foreach (string name in new[] { "SystemRoot", "WINDIR", "PATH", "PATHEXT", "TEMP", "TMP", "LOCALAPPDATA", "APPDATA", "USERPROFILE", "HOME", "HOMEDRIVE", "HOMEPATH", "PROGRAMDATA", "ProgramFiles", "ProgramFiles(x86)", "COMSPEC", "HTTPS_PROXY", "HTTP_PROXY", "NO_PROXY", "SSL_CERT_FILE", "SSL_CERT_DIR" })
        {
            if (Environment.GetEnvironmentVariable(name) is { } value)
                start.Environment[name] = value;
        }
        start.Environment["GEMINI_CLI_HOME"] = HomeDirectory;
        start.Environment["GEMINI_CLI_TRUST_WORKSPACE"] = "true";
        string managedSettings = Path.Combine(HomeDirectory, ".gemini", "settings.json");
        start.Environment["GEMINI_CLI_SYSTEM_DEFAULTS_PATH"] = managedSettings;
        start.Environment["GEMINI_CLI_SYSTEM_SETTINGS_PATH"] = managedSettings;
    }

    public ValueTask DisposeAsync()
    {
        _accountLease.Dispose();
        _settingsLease.Dispose();
        try
        {
            if (!Directory.EnumerateFileSystemEntries(_workDirectory).Any())
                Directory.Delete(_workDirectory);
        }
        catch (IOException)
        {
            // CLI may release its work files after its process exits.
        }
        return ValueTask.CompletedTask;
    }
}
