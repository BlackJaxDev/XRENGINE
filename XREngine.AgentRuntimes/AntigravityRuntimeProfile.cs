using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace XREngine.AgentRuntimes;

/// <summary>
/// Starts Antigravity ACP with a private configuration and an empty work folder.
/// The Google sign-in stays in the client's native keyring.
/// </summary>
internal sealed class AntigravityRuntimeProfile : IAsyncDisposable
{
    private const string SupportedVersion = "1.5.4";
    private const string ManagedSettings = """
        {
          "permissions": {
            "deny": [
              "read_file(*)", "write_file(*)", "command(*)", "unsandboxed(*)",
              "read_url(*)", "execute_url(*)"
            ],
            "allow": ["mcp(xrengine-run/*)"]
          }
        }
        """;
    private const string ManagedGlobalConfiguration = """
        { "plugins": { "swarm": { "enabled": false } } }
        """;
    private const string GeneratedGlobalConfiguration = """
        { "plugins": { "swarm": { "enabled": true } } }
        """;
    private readonly FileStream _settingsLease;
    private readonly FileStream _globalConfigurationLease;
    private readonly FileStream _mcpConfigurationLease;
    private readonly FileStream _accountLease;
    private readonly string _workDirectory;

    private AntigravityRuntimeProfile(FileStream settingsLease, FileStream globalConfigurationLease,
        FileStream mcpConfigurationLease, FileStream accountLease, string workDirectory)
    {
        _settingsLease = settingsLease;
        _globalConfigurationLease = globalConfigurationLease;
        _mcpConfigurationLease = mcpConfigurationLease;
        _accountLease = accountLease;
        _workDirectory = workDirectory;
    }

    public static string HomeDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "XREngine", "AgentRuntimes", "Antigravity", "profile");
    public string WorkDirectory => _workDirectory;

    public static async Task<AntigravityRuntimeProfile> OpenAsync(
        LocalCliRuntimeOptions options, bool signingIn, CancellationToken token)
    {
        string home = HomeDirectory;
        string settingsDirectory = Path.Combine(home, "antigravity-cli");
        Directory.CreateDirectory(settingsDirectory);
        string globalDirectory = Path.Combine(home, "config");
        Directory.CreateDirectory(globalDirectory);
        string globalPath = Path.Combine(globalDirectory, "config.json");
        using (var exclusive = new FileStream(globalPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            if (exclusive.Length > 16_384)
                throw new InvalidOperationException("The Antigravity profile has unsupported global configuration.");
            using var reader = new StreamReader(exclusive, Encoding.UTF8, leaveOpen: true);
            string current = await reader.ReadToEndAsync(token);
            JsonNode? parsed = current.Length == 0 ? null : JsonNode.Parse(current);
            if (parsed is not null && !JsonNode.DeepEquals(parsed, JsonNode.Parse(ManagedGlobalConfiguration)))
            {
                // Antigravity creates this default before the host can pin the profile.
                if (!JsonNode.DeepEquals(parsed, JsonNode.Parse(GeneratedGlobalConfiguration)))
                    throw new InvalidOperationException("The Antigravity profile has unsupported global configuration.");
            }
            if (!JsonNode.DeepEquals(parsed, JsonNode.Parse(ManagedGlobalConfiguration)))
            {
                exclusive.SetLength(0);
                exclusive.Position = 0;
                byte[] managed = Encoding.UTF8.GetBytes(ManagedGlobalConfiguration);
                await exclusive.WriteAsync(managed, token);
            }
        }
        string settingsPath = Path.Combine(settingsDirectory, "settings.json");
        if (!File.Exists(settingsPath))
        {
            try
            {
                using var created = new FileStream(settingsPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                created.Write(Encoding.UTF8.GetBytes(ManagedSettings));
            }
            catch (IOException) when (File.Exists(settingsPath))
            {
                // Another editor created the same managed settings first.
            }
        }

        FileStream? settingsLease = null;
        FileStream? globalConfigurationLease = null;
        FileStream? mcpConfigurationLease = null;
        FileStream? accountLease = null;
        try
        {
            globalConfigurationLease = new FileStream(globalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            string mcpPath = Path.Combine(globalDirectory, "mcp_config.json");
            mcpConfigurationLease = new FileStream(mcpPath, FileMode.OpenOrCreate, FileAccess.Read, FileShare.Read);
            if (mcpConfigurationLease.Length != 0)
                throw new InvalidOperationException("The Antigravity profile contains unsupported MCP server configuration.");
            foreach (string path in Directory.EnumerateFileSystemEntries(globalDirectory))
            {
                string name = Path.GetFileName(path);
                if (name is "config.json" or "mcp_config.json")
                    continue;
                if (name == ".migrated" && File.Exists(path) && new FileInfo(path).Length == 0)
                    continue;
                if (name is "agents" or "projects" && Directory.Exists(path)
                    && !Directory.EnumerateFileSystemEntries(path).Any())
                    continue;
                throw new InvalidOperationException("The Antigravity profile contains unsupported native customizations.");
            }
            foreach (string folder in new[] { "plugins", "extensions", "skills", "agents", "hooks" })
            {
                if (Directory.Exists(Path.Combine(home, folder)))
                    throw new InvalidOperationException("The Antigravity profile contains unsupported native customizations.");
            }
            settingsLease = new FileStream(settingsPath, FileMode.Open, FileAccess.Read,
                signingIn ? FileShare.ReadWrite | FileShare.Delete : FileShare.Read);
            if (settingsLease.Length > 16_384)
                throw new InvalidOperationException("The Antigravity profile settings changed. Restore the managed settings before connecting.");
            using (var reader = new StreamReader(settingsLease, Encoding.UTF8, leaveOpen: true))
            {
                JsonNode? stored = JsonNode.Parse(await reader.ReadToEndAsync(token));
                if (!JsonNode.DeepEquals(stored, JsonNode.Parse(ManagedSettings)))
                    throw new InvalidOperationException("The Antigravity profile settings changed. Restore the managed settings before connecting.");
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
                    // Another editor created the account lock first.
                }
            }
            try
            {
                accountLease = new FileStream(leasePath, FileMode.Open,
                    signingIn ? FileAccess.ReadWrite : FileAccess.Read,
                    signingIn ? FileShare.None : FileShare.Read);
            }
            catch (IOException)
            {
                throw new InvalidOperationException("Wait for active Antigravity runs or sign-in to finish before changing the account.");
            }
            string sessions = options.SessionDirectory ?? Path.Combine(Path.GetDirectoryName(home)!, "sessions");
            string workingDirectory = Path.Combine(Path.GetFullPath(sessions), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workingDirectory);
            return new AntigravityRuntimeProfile(settingsLease, globalConfigurationLease,
                mcpConfigurationLease, accountLease, workingDirectory);
        }
        catch
        {
            accountLease?.Dispose();
            settingsLease?.Dispose();
            globalConfigurationLease?.Dispose();
            mcpConfigurationLease?.Dispose();
            throw;
        }
    }

    public async Task<string> ReadVersionAsync(LocalCliRuntimeOptions options, CancellationToken token)
    {
        using Process process = new() { StartInfo = CreateStartInfo(options, acp: false) };
        if (!process.Start())
            throw new Win32Exception("Antigravity CLI did not start.");
        Task stderrDrain = process.StandardError.BaseStream.CopyToAsync(Stream.Null, token);
        try
        {
            var version = new StringBuilder();
            char[] character = new char[1];
            while (version.Length < 64)
            {
                int count = await process.StandardOutput.ReadAsync(character, token);
                if (count == 0 || character[0] is '\r' or '\n')
                    break;
                version.Append(character[0]);
            }
            if (version.Length >= 64)
                throw new InvalidDataException("Antigravity returned an invalid version string.");
            await process.WaitForExitAsync(token);
            await stderrDrain;
            string line = version.ToString().Trim();
            if (process.ExitCode != 0 || line != SupportedVersion)
                throw new InvalidOperationException($"Antigravity subscription access requires the validated CLI {SupportedVersion}.");
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

    public ProcessStartInfo CreateStartInfo(LocalCliRuntimeOptions options, bool acp)
    {
        string executable = options.ExecutablePath;
        if (string.IsNullOrWhiteSpace(executable) || executable.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new ArgumentException("Select an Antigravity CLI executable.", nameof(options));
        if (executable is "agy" or "agy.exe")
        {
            string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "agy", "bin", "agy.exe");
            if (File.Exists(installed))
                executable = installed;
        }
        if (Path.GetExtension(executable) is ".cmd" or ".bat" or ".ps1" or ".js")
            throw new ArgumentException("Select the Antigravity executable, not a shell script.", nameof(options));

        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = _workDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        start.Environment.Clear();
        foreach (string name in new[]
        {
            "SystemRoot", "WINDIR", "PATH", "PATHEXT", "TEMP", "TMP", "LOCALAPPDATA",
            "APPDATA", "USERPROFILE", "HOME", "HOMEDRIVE", "HOMEPATH", "PROGRAMDATA",
            "ProgramFiles", "ProgramFiles(x86)", "COMSPEC", "HTTPS_PROXY", "HTTP_PROXY",
            "NO_PROXY", "SSL_CERT_FILE", "SSL_CERT_DIR",
        })
        {
            if (Environment.GetEnvironmentVariable(name) is { } value)
                start.Environment[name] = value;
        }
        if (acp)
        {
            start.ArgumentList.Add("acp");
            start.ArgumentList.Add("--gemini-dir");
            start.ArgumentList.Add(HomeDirectory);
            start.ArgumentList.Add("--app-data-dir");
            start.ArgumentList.Add("antigravity-cli");
            start.ArgumentList.Add("--disable-telemetry");
            start.ArgumentList.Add("--include-default-customizations=false");
        }
        else
            start.ArgumentList.Add("--version");
        return start;
    }

    public ValueTask DisposeAsync()
    {
        _accountLease.Dispose();
        _settingsLease.Dispose();
        _globalConfigurationLease.Dispose();
        _mcpConfigurationLease.Dispose();
        try
        {
            if (!Directory.EnumerateFileSystemEntries(_workDirectory).Any())
                Directory.Delete(_workDirectory);
        }
        catch (IOException)
        {
            // The CLI can release its work files after its process exits.
        }
        return ValueTask.CompletedTask;
    }
}
