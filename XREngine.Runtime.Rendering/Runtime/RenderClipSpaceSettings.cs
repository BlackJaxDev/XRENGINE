namespace XREngine.Rendering;

/// <summary>
/// Logical clip-space Y direction requested by rendering settings.
/// </summary>
public enum ERenderClipSpaceYDirection
{
    /// <summary>
    /// Positive clip-space Y maps upward on screen, matching the engine's OpenGL-style convention.
    /// </summary>
    YUp = 0,

    /// <summary>
    /// Positive clip-space Y maps downward on screen, matching Vulkan's positive-height viewport convention.
    /// </summary>
    YDown = 1
}

/// <summary>
/// Logical normalized-device-coordinate depth range requested by rendering settings.
/// </summary>
public enum ERenderClipDepthRange
{
    /// <summary>
    /// Near and far clip depth are represented in the native Vulkan/D3D range [0, 1].
    /// </summary>
    ZeroToOne = 0,

    /// <summary>
    /// Near and far clip depth are represented in the legacy OpenGL range [-1, 1].
    /// </summary>
    NegativeOneToOne = 1
}

public static class RenderClipSpacePolicy
{
    public static ERenderClipSpaceYDirection FramebufferTextureYDirection(RuntimeGraphicsApiKind backend)
    {
        ERenderClipSpaceYDirection clipY = RuntimeEngine.Rendering.Settings.ClipSpaceYDirection;
        if (backend != RuntimeGraphicsApiKind.Vulkan)
            return clipY;

        // Vulkan uses a negative-height viewport for the engine's Y-up clip-space
        // policy. That preserves screen-space handedness for rasterization, but FBO
        // texture row 0 is still the top of the image, so clip->texture sampling must
        // use the opposite Y mapping.
        return clipY == ERenderClipSpaceYDirection.YUp
            ? ERenderClipSpaceYDirection.YDown
            : ERenderClipSpaceYDirection.YUp;
    }

    public static float DepthBufferToClipZ(float depth, ERenderClipDepthRange range)
        => range == ERenderClipDepthRange.NegativeOneToOne
            ? depth * 2.0f - 1.0f
            : depth;

    public static float ClipZToDepthBuffer(float clipZ, ERenderClipDepthRange range)
        => range == ERenderClipDepthRange.NegativeOneToOne
            ? clipZ * 0.5f + 0.5f
            : clipZ;
}
