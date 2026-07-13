namespace XREngine.Rendering.API.Rendering.OpenXR;

public sealed class OpenXrSmokeTemporalScenarioDefinition
{
    public string Scenario { get; set; } = string.Empty;
    public string Sample { get; set; } = string.Empty;
    public string VelocityOracle { get; set; } = string.Empty;
    public int CaptureStartFrame { get; set; }
    public int CaptureEndFrame { get; set; }
    public bool RequiresTemporalConvergence { get; set; }
    public bool IsDisocclusionBaseline { get; set; }
    public bool IsDisocclusionResult { get; set; }
}
