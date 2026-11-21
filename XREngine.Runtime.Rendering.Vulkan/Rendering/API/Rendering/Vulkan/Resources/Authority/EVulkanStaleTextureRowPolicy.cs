namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Selects how publication-source validation treats a canonical texture row whose live
/// source changed after the publication captured it. Streaming does this when an imported
/// texture replaces its loading placeholder with resident data. The scene publisher
/// refreshes such a row only when a material that binds the texture is planned again, so
/// a row of a hidden or off-screen material can stay stale for many publications.
/// </summary>
internal enum EVulkanStaleTextureRowPolicy
{
    /// <summary>A stale row fails the validation.</summary>
    Reject,

    /// <summary>
    /// A stale row passes. The texture-row preparation then publishes the row as not
    /// resident, so each material binding samples its own fallback, the same as a texture
    /// that is still loading.
    /// </summary>
    AllowAsLoading,

    /// <summary>
    /// A stale row passes only if the first output of the same publication in this frame
    /// slot already published that row as not resident. A row that became stale after
    /// that output fails, because the shared texture table still marks it resident.
    /// </summary>
    AllowRecorded,
}
