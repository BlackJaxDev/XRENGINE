using System.Text.Json;

namespace XREngine.Editor.Benchmarks.PhysicsChain;

/// <summary>
/// Persists complete evidence and matched summaries under the configured
/// bounded validation root. Raw frame samples remain in the nested result.
/// </summary>
public static class PhysicsChainBenchmarkEvidenceWriter
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Write(
        string repoRoot,
        string configuredRunRoot,
        PhysicsChainBenchmarkEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        string directory = PhysicsChainBenchmarkResultWriter.ResolveReportsDirectory(repoRoot, configuredRunRoot);
        Directory.CreateDirectory(directory);
        string fileName = $"evidence-{evidence.StableFileStem()}.json";
        string path = Path.Combine(directory, fileName);
        File.WriteAllText(path, JsonSerializer.Serialize(evidence, Options) + Environment.NewLine);
        return path;
    }

    public static string WriteSummary(
        string repoRoot,
        string configuredRunRoot,
        PhysicsChainBenchmarkMatchedSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        string directory = PhysicsChainBenchmarkResultWriter.ResolveReportsDirectory(repoRoot, configuredRunRoot);
        Directory.CreateDirectory(directory);
        string safeCase = Sanitize(summary.MatrixCase.StableName);
        string path = Path.Combine(directory, $"summary-{safeCase}-{summary.MeasurementKind}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(summary, Options) + Environment.NewLine);
        return path;
    }

    private static string StableFileStem(this PhysicsChainBenchmarkEvidence evidence)
        => $"{Sanitize(evidence.MatrixCase.StableName)}-{evidence.MeasurementKind}-run{evidence.MatchedRunIndex}";

    private static string Sanitize(string value)
    {
        Span<char> buffer = value.Length <= 512 ? stackalloc char[value.Length] : new char[value.Length];
        int length = 0;
        for (int i = 0; i < value.Length; ++i)
        {
            char character = value[i];
            buffer[length++] = char.IsLetterOrDigit(character) || character is '-' or '_'
                ? char.ToLowerInvariant(character)
                : '-';
        }
        return length == 0 ? "unnamed" : new string(buffer[..length]);
    }
}
