namespace XREngine.Rendering;

/// <summary>
/// Logical frame-varying tables written through the advanced frame-slot upload arena.
/// The values are backend-neutral and intentionally stable for telemetry and capture tools.
/// </summary>
public enum EAdvancedFrameUploadStream
{
    Instance = 0,
    View = 1,
    DeformationJob = 2,
    Light = 3,
    Material = 4,
}
