namespace XREngine.Rendering.Occlusion;

/// <summary>Hi-Z GPU work phase measured by delayed timestamp queries.</summary>
public enum EOcclusionGpuElapsedStage : byte
{
    Build,
    Test,
}
