using System;

namespace XREngine.Rendering;

/// <summary>
/// Bitmask describing specific reasons for invalidating and resetting temporal history buffers.
/// </summary>
[Flags]
public enum AdvancedTemporalResetFlags : uint
{
    None = 0u,
    Resize = 1u << 0,
    PipelineSwitch = 1u << 1,
    CameraCut = 1u << 2,
    ViewCountChange = 1u << 3,
    RenderScaleChange = 1u << 4,
    FormatChange = 1u << 5,
    ShaderGenerationReplacement = 1u << 6,

    All = Resize | PipelineSwitch | CameraCut | ViewCountChange | RenderScaleChange | FormatChange | ShaderGenerationReplacement,
}
