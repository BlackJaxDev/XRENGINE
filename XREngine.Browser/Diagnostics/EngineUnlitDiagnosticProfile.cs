using XREngine.Data.Rendering;

namespace XREngine.Browser.Diagnostics;

/// <summary>Explicit startup inputs for the ordinary-unlit Default pipeline diagnostic.</summary>
internal sealed record EngineUnlitDiagnosticProfile(string Name, uint SampleCount, bool AmbientOcclusion,
    EMeshSubmissionStrategy SubmissionStrategy)
{
    public static EngineUnlitDiagnosticProfile Baseline { get; } = new("cpu-x1", 1, false, EMeshSubmissionStrategy.CpuDirect);

    public static EngineUnlitDiagnosticProfile Parse(string name) => name switch
    {
        "cpu-x1" => Baseline,
        "cpu-x4" => new(name, 4, false, EMeshSubmissionStrategy.CpuDirect),
        "cpu-x4-ao" => new(name, 4, true, EMeshSubmissionStrategy.CpuDirect),
        "gpu-indirect-x1" => new(name, 1, false, EMeshSubmissionStrategy.GpuIndirectZeroReadback),
        "gpu-indirect-x4" => new(name, 4, false, EMeshSubmissionStrategy.GpuIndirectZeroReadback),
        _ => throw new ArgumentException("EngineMeshDiagnostic.UnlitProfileUnsupported: select cpu-x1, cpu-x4, cpu-x4-ao, gpu-indirect-x1, or gpu-indirect-x4.", nameof(name)),
    };
}
