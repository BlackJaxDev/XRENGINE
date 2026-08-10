namespace XREngine.Rendering.Vulkan;

public sealed unsafe partial class VulkanRenderer
{
    public SparseTextureStreamingSupport GetSparseTextureStreamingSupport(ESizedInternalFormat format)
        => ResourceRuntime.SparseTextureStreaming.GetSupport(format);

    public bool TryScheduleSparseTextureStreamingTransitionAsync(
        XRTexture2D texture,
        SparseTextureStreamingTransitionRequest request,
        CancellationToken cancellationToken,
        Action<SparseTextureStreamingTransitionResult> onCompleted,
        Action<Exception>? onError = null)
        => ResourceRuntime.SparseTextureStreaming.TryScheduleTransitionAsync(
            texture, request, cancellationToken, onCompleted, onError);

    public SparseTextureStreamingFinalizeResult FinalizeSparseTextureStreamingTransition(
        XRTexture2D texture,
        SparseTextureStreamingTransitionRequest request,
        SparseTextureStreamingTransitionResult result)
        => ResourceRuntime.SparseTextureStreaming.FinalizeTransition(texture, request, result);
}
