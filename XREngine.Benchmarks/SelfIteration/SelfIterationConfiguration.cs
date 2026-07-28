using System.Text.Json;
using System.Text.Json.Serialization;

namespace XREngine.Benchmarks.SelfIteration;

/// <summary>
/// Describes one bounded LLM-driven rendering performance campaign.
/// </summary>
public sealed class SelfIterationConfiguration
{
    public string CampaignId { get; set; } = "render-performance";
    public string Objective { get; set; } = "Reduce steady-state frame time without changing rendered behavior.";
    public int MaxIterations { get; set; } = 5;
    public int MaxProposalAttemptsPerIteration { get; set; } = 3;
    public bool RequireCleanTrackedWorktree { get; set; } = true;
    public string[] AllowedPathPrefixes { get; set; } =
    [
        "XRENGINE/Rendering",
        "XREngine.Runtime.Rendering",
        "XREngine.Runtime.Rendering.OpenGL",
        "XREngine.Runtime.Rendering.Vulkan",
    ];
    public string ProgressDocument { get; set; } = string.Empty;
    public string RejectedAttemptsDocument { get; set; } = string.Empty;
    public SelfIterationAgentConfiguration Agent { get; set; } = new();
    public SelfIterationMeasurementConfiguration Measurement { get; set; } = new();
    public SelfIterationAcceptanceConfiguration Acceptance { get; set; } = new();
    public List<SelfIterationScenario> Scenarios { get; set; } = [];

    /// <summary>
    /// Loads JSON or JSONC while allowing trailing commas.
    /// </summary>
    public static SelfIterationConfiguration Load(string path)
    {
        string json = File.ReadAllText(path);
        var options = new JsonSerializerOptions
        {
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            PropertyNameCaseInsensitive = true,
        };
        options.Converters.Add(new JsonStringEnumConverter());

        return JsonSerializer.Deserialize<SelfIterationConfiguration>(json, options)
            ?? throw new InvalidDataException($"Self-iteration configuration was empty: {path}");
    }

    /// <summary>
    /// Applies derived document paths and rejects unsafe or incomplete settings.
    /// </summary>
    public void NormalizeAndValidate(string workspaceRoot, bool requireAgent)
    {
        if (string.IsNullOrWhiteSpace(CampaignId) ||
            CampaignId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new InvalidDataException(
                "CampaignId must contain only ASCII letters, digits, '-' or '_'.");
        }
        if (string.IsNullOrWhiteSpace(Objective))
            throw new InvalidDataException("Objective is required.");
        if (MaxIterations is < 1 or > 100)
            throw new InvalidDataException("MaxIterations must be between 1 and 100.");
        if (MaxProposalAttemptsPerIteration is < 1 or > 20)
            throw new InvalidDataException("MaxProposalAttemptsPerIteration must be between 1 and 20.");
        if (AllowedPathPrefixes.Length == 0)
            throw new InvalidDataException("At least one AllowedPathPrefixes entry is required.");
        if (Scenarios.Count == 0)
            throw new InvalidDataException("At least one scenario is required.");

        ProgressDocument = NormalizeDocumentPath(
            workspaceRoot,
            ProgressDocument,
            $"docs/work/progress/rendering/{CampaignId}-self-improvement.md");
        RejectedAttemptsDocument = NormalizeDocumentPath(
            workspaceRoot,
            RejectedAttemptsDocument,
            $"docs/work/investigations/rendering/{CampaignId}-rejected-attempts.md");

        AllowedPathPrefixes = AllowedPathPrefixes
            .Select(path => NormalizeAllowedPath(workspaceRoot, path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Measurement.Validate();
        Acceptance.Validate();
        Agent.Validate(requireAgent);

        var scenarioNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SelfIterationScenario scenario in Scenarios)
        {
            scenario.Validate(workspaceRoot, Measurement.ProfileMode);
            if (!scenarioNames.Add(scenario.Name))
                throw new InvalidDataException($"Duplicate scenario name: {scenario.Name}");
        }
    }

    private static string NormalizeDocumentPath(
        string workspaceRoot,
        string configuredPath,
        string defaultPath)
    {
        string relative = NormalizeRelativePath(
            string.IsNullOrWhiteSpace(configuredPath) ? defaultPath : configuredPath);
        string absolute = Path.GetFullPath(Path.Combine(workspaceRoot, relative));
        string rootPrefix = Path.GetFullPath(workspaceRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        if (!absolute.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Document path escapes the workspace: {configuredPath}");
        return relative;
    }

    private static string NormalizeAllowedPath(string workspaceRoot, string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            throw new InvalidDataException("AllowedPathPrefixes cannot contain an empty path.");

        string absolute = Path.GetFullPath(
            Path.IsPathRooted(configuredPath)
                ? configuredPath
                : Path.Combine(workspaceRoot, configuredPath));
        string root = Path.GetFullPath(workspaceRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string rootPrefix = root + Path.DirectorySeparatorChar;
        if (!absolute.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Allowed path prefix escapes the workspace: {configuredPath}");
        }
        return NormalizeRelativePath(Path.GetRelativePath(root, absolute));
    }

    internal static string NormalizeRelativePath(string path)
        => path.Replace('\\', '/').Trim().TrimStart('/');
}
