namespace XREngine;

/// <summary>
/// Captures the effective rendering configuration resolved by the application settings cascade.
/// </summary>
public readonly record struct EffectiveRenderSettingsSnapshot(
    EffectiveCommonRenderSettings Common,
    EffectiveOpenGLRenderSettings OpenGL,
    EffectiveVulkanRenderSettings Vulkan);
