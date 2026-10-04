namespace XREngine.Browser.Diagnostics;

/// <summary>Explicit startup inputs for the ordinary-unlit Default pipeline diagnostic.</summary>
internal sealed record EngineUnlitDiagnosticProfile(string Name, uint SampleCount, bool AmbientOcclusion)
{
    public static EngineUnlitDiagnosticProfile Baseline { get; } = new("cpu-x1", 1, false);

    public static EngineUnlitDiagnosticProfile Parse(string name) => name switch
    {
        "cpu-x1" => Baseline,
        "cpu-x4" => new(name, 4, false),
        "cpu-x4-ao" => new(name, 4, true),
        _ => throw new ArgumentException("EngineMeshDiagnostic.UnlitProfileUnsupported: select cpu-x1, cpu-x4, or cpu-x4-ao.", nameof(name)),
    };
}
