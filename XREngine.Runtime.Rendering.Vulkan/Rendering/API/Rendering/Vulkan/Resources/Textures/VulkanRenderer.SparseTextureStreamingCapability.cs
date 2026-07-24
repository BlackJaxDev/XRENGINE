namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    public SparseTextureStreamingSupport GetSparseTextureStreamingSupport(ESizedInternalFormat format)
        => SparseTextureStreamingSupport.Unsupported(
            "Vulkan true sparse image page residency is not implemented yet. " +
            "Vulkan sparse-transition requests use a dense resident mip upload compatibility path.");

    public bool TryScheduleSparseTextureStreamingTransitionAsync(
        XRTexture2D texture,
        SparseTextureStreamingTransitionRequest request,
        CancellationToken cancellationToken,
        Action<SparseTextureStreamingTransitionResult> onCompleted,
        Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(texture);
        ArgumentNullException.ThrowIfNull(onCompleted);

        string textureName = string.IsNullOrWhiteSpace(texture.Name) ? "UnnamedTexture" : texture.Name;
        RuntimeRenderingHostServices.Scheduling.EnqueueRenderThreadTask(() =>
        {
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    onCompleted(SparseTextureStreamingTransitionResult.Unsupported(
                        "Vulkan sparse texture transition was canceled before compatibility upload."));
                    return;
                }

                onCompleted(texture.ApplySparseTextureStreamingTransition(request));
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex);
            }
        }, $"XRTexture2D.ScheduleVulkanSparseCompatTransition[{textureName}]", RenderThreadJobKind.TextureUpload);

        return true;
    }

    public SparseTextureStreamingFinalizeResult FinalizeSparseTextureStreamingTransition(
        XRTexture2D texture,
        SparseTextureStreamingTransitionRequest request,
        SparseTextureStreamingTransitionResult transitionResult)
    {
        if (!transitionResult.Applied)
        {
            return SparseTextureStreamingFinalizeResult.Failed(
                transitionResult.FailureReason ?? "Vulkan sparse compatibility transition did not apply.");
        }

        return transitionResult.ExposureDeferred
            ? SparseTextureStreamingFinalizeResult.Failed(
                "Vulkan dense sparse-compat transitions are not deferred; no sparse fence finalization is available.")
            : SparseTextureStreamingFinalizeResult.Success();
    }
}
