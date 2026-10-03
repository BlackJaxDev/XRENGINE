using XREngine.Components;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>Frozen numeric state and exact image generation for one authored decal at world swap.</summary>
internal readonly record struct AdvancedAuthoredDecalCaptureRow(
    DeferredDecalComponent Source,
    AdvancedDecalRecord Record,
    AdvancedGpuResourceBindingSource Image);
