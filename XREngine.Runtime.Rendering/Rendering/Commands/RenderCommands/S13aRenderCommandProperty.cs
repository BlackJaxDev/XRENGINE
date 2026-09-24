namespace XREngine.Rendering.Commands;

/// <summary>Known render-command notification names for numeric S13a traces.</summary>
public enum S13aRenderCommandProperty : byte
{
    Other = 0,
    PublishCanonicalDrawIdentities = 1,
    RenderPass = 2,
    Enabled = 3,
    GPUCommandIndex = 4,
    Mesh = 5,
    WorldMatrix = 6,
    MaterialOverride = 7,
    RenderOptionsOverride = 8,
    Instances = 9,
    WorldMatrixIsModelMatrix = 10,
    ForceCpuRendering = 11,
    EditorHighlightBits = 12,
    WorldCullingVolumeOverride = 13,
    GpuProfilingLabel = 14,
}
