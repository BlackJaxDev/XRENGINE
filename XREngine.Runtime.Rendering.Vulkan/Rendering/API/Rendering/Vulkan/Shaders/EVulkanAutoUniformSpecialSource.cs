namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Mesh-owned sources that are not represented by <see cref="EEngineUniform"/>.
/// </summary>
internal enum EVulkanAutoUniformSpecialSource : byte
{
    None = 0,
    TransformId,
    SkinPaletteBase,
    SkinPaletteCount,
    SkinningInfluenceCap,
    BlendshapeActiveCount,
    BlendshapeWeightThreshold,
    UsePrecombinedBlendshapeDeltas,
}
