namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Typed frame-operation emission command captured while an OpenXR eye is prepared.
/// It deliberately belongs to the preparation phase and is not carried by the
/// immutable command-worker input.
/// </summary>
internal readonly record struct OpenXrEyeFrameOpEmission(
    uint ViewIndex,
    int ResourcePlannerStateIndex);
