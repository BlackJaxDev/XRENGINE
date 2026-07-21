using System.Text.Json;

namespace XREngine.Editor.Benchmarks.PhysicsChain;

/// <summary>
/// Writes benchmark evidence only when an explicit bounded validation run root
/// is supplied by the launcher.
/// </summary>
public static class PhysicsChainBenchmarkResultWriter
{
    public const string RunRootEnvironmentVariable = "XRE_PHYSICS_CHAIN_BENCHMARK_RUN_ROOT";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static bool TryWriteConfiguredResult(
        PhysicsChainBenchmarkResult result,
        out string? resultPath,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(result);
        resultPath = null;
        error = null;

        string? configuredRoot = Environment.GetEnvironmentVariable(RunRootEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configuredRoot))
            return false;

        try
        {
            string repoRoot = Path.GetFullPath(Environment.CurrentDirectory);
            string reportsDirectory = ResolveReportsDirectory(repoRoot, configuredRoot);
            Directory.CreateDirectory(reportsDirectory);

            string safeScenario = SanitizeFileName(result.ScenarioName);
            string timestamp = result.CompletedAt.ToUniversalTime().ToString("yyyyMMdd-HHmmssfff");
            resultPath = Path.Combine(reportsDirectory, $"physics-chain-{timestamp}-{safeScenario}.json");
            File.WriteAllText(resultPath, JsonSerializer.Serialize(result, JsonOptions) + Environment.NewLine);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            resultPath = null;
            return false;
        }
    }

    public static string ResolveReportsDirectory(string repoRoot, string configuredRunRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredRunRoot);

        string fullRepoRoot = Path.GetFullPath(repoRoot);
        string validationRoot = Path.GetFullPath(Path.Combine(fullRepoRoot, "Build", "_AgentValidation"));
        string fullRunRoot = Path.GetFullPath(
            Path.IsPathRooted(configuredRunRoot)
                ? configuredRunRoot
                : Path.Combine(fullRepoRoot, configuredRunRoot));

        if (!fullRunRoot.StartsWith(validationRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Configured benchmark run root must be inside Build/_AgentValidation.", nameof(configuredRunRoot));

        return Path.Combine(fullRunRoot, "reports");
    }

    private static string SanitizeFileName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        Span<char> buffer = value.Length <= 256 ? stackalloc char[value.Length] : new char[value.Length];
        int length = 0;
        for (int i = 0; i < value.Length; ++i)
        {
            char character = value[i];
            bool isInvalid = false;
            for (int j = 0; j < invalid.Length; ++j)
            {
                if (character != invalid[j])
                    continue;

                isInvalid = true;
                break;
            }

            buffer[length++] = isInvalid || char.IsWhiteSpace(character) ? '-' : char.ToLowerInvariant(character);
        }

        return length == 0 ? "unnamed" : new string(buffer[..length]);
    }
}
