namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Which graphics contract a sealed Advanced target closure satisfies.
/// </summary>
internal enum EVulkanAdvancedVisibilityTargetKind : byte
{
    /// <summary>The canonical visibility buffer: identity, metadata, selection and depth-stencil.</summary>
    Visibility = 0,

    /// <summary>A depth-only directional shadow atlas page written by the directional shadow lane.</summary>
    DirectionalShadow = 1,

    /// <summary>The mesh surface decal buffer: two RGBA8 targets and the read-only resolved depth.</summary>
    DecalBuffer = 2,
}
