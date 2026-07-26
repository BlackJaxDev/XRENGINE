namespace XREngine.Rendering;

/// <summary>
/// Backend-specific sparse or sparse-compatible texture transition behavior.
/// Stable host code resolves this capability instead of casting to a concrete renderer.
/// </summary>
public interface ISparseTextureStreamingBackendCapability
{
    SparseTextureStreamingSupport GetSparseTextureStreamingSupport(ESizedInternalFormat format);

    bool TryScheduleSparseTextureStreamingTransitionAsync(
        XRTexture2D texture,
        SparseTextureStreamingTransitionRequest request,
        CancellationToken cancellationToken,
        Action<SparseTextureStreamingTransitionResult> onCompleted,
        Action<Exception>? onError = null);

    SparseTextureStreamingFinalizeResult FinalizeSparseTextureStreamingTransition(
        XRTexture2D texture,
        SparseTextureStreamingTransitionRequest request,
        SparseTextureStreamingTransitionResult transitionResult);
}
