namespace XREngine.Rendering.Residency;

/// <summary>
/// Gives access to the shared <see cref="ITextureResidencyRequests"/> seam of imported texture streaming.
/// </summary>
public static class TextureResidencyRequests
{
    /// <summary>The residency request seam of the shared imported texture streaming manager.</summary>
    public static ITextureResidencyRequests Shared
        => ImportedTextureStreamingManager.Instance;
}
