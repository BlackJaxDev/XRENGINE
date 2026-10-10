namespace XREngine.Rendering;

/// <summary>Optional ambient-visibility publication for standard lit material consumers.</summary>
public interface IRenderPipelineAmbientOcclusionProvider
{
    /// <summary>Returns false for neutral visibility; enabled output must be generation-owned.</summary>
    bool TryGetAmbientOcclusion(out XRTexture2D? visibility, out float power, out bool multiBounce);
}
