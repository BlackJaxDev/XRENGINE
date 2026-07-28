namespace XREngine.Benchmarks.SelfIteration;

/// <summary>
/// Selects one Unit Testing World render backend and mesh-submission path.
/// </summary>
public sealed class SelfIterationScenario
{
    public string Name { get; set; } = string.Empty;
    public string RenderBackend { get; set; } = "Vulkan";
    public string MeshSubmissionStrategy { get; set; } = "GpuIndirectZeroReadback";
    public string UnitTestingWorldSettingsPath { get; set; } = "Assets/UnitTestingWorldSettings.jsonc";
    public SelfIterationScenarioMeasurementOverrides Overrides { get; set; } = new();
    public Dictionary<string, string> Environment { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    internal void Validate(string workspaceRoot, string profileMode)
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new InvalidDataException("Every scenario requires a Name.");
        if (RenderBackend is not ("OpenGL" or "Vulkan"))
            throw new InvalidDataException($"Scenario '{Name}' has an invalid RenderBackend.");

        string[] strategies =
        [
            "CpuDirect",
            "GpuIndirectInstrumented",
            "GpuIndirectZeroReadback",
            "GpuMeshletInstrumented",
            "GpuMeshletZeroReadback",
        ];
        if (!strategies.Contains(MeshSubmissionStrategy, StringComparer.Ordinal))
            throw new InvalidDataException($"Scenario '{Name}' has an invalid MeshSubmissionStrategy.");

        string fullSettingsPath = Path.GetFullPath(
            Path.IsPathRooted(UnitTestingWorldSettingsPath)
                ? UnitTestingWorldSettingsPath
                : Path.Combine(workspaceRoot, UnitTestingWorldSettingsPath));
        if (!File.Exists(fullSettingsPath))
            throw new FileNotFoundException($"Scenario '{Name}' settings JSONC was not found.", fullSettingsPath);
        UnitTestingWorldSettingsPath = fullSettingsPath;
        Overrides.Validate(Name, profileMode);

        foreach (KeyValuePair<string, string> entry in Environment)
        {
            if (string.IsNullOrWhiteSpace(entry.Key) || entry.Key.Contains('='))
                throw new InvalidDataException($"Scenario '{Name}' has an invalid environment-variable name.");
        }
    }
}
