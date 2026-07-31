namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Temporal matrices authored outside the legacy engine-uniform enum.
/// </summary>
internal enum EVulkanTemporalUniformSource : byte
{
    None = 0,
    CurrentViewProjection,
    PreviousViewProjection,
    CurrentStereoViewProjection,
    PreviousStereoViewProjection,
}
