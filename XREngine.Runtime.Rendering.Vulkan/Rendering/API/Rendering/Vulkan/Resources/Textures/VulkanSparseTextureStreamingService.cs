namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Provides Vulkan's current dense-upload compatibility implementation for the
/// engine sparse-texture contract.  It owns no renderer state and therefore can
/// be shared by every logical-device resource lifetime.
/// </summary>
internal sealed class VulkanSparseTextureStreamingService
{
    internal SparseTextureStreamingSupport GetSupport(ESizedInternalFormat format)
        => SparseTextureStreamingSupport.Unsupported(
            "Vulkan true sparse image page residency is not implemented yet. " +
            "Vulkan sparse-transition requests use a dense resident mip upload compatibility path.");

    internal bool TryScheduleTransitionAsync(
        XRTexture2D texture,
        SparseTextureStreamingTransitionRequest request,
        CancellationToken cancellationToken,
        Action<SparseTextureStreamingTransitionResult> onCompleted,
        Action<Exception>? onError)
    {
        ArgumentNullException.ThrowIfNull(texture);
        ArgumentNullException.ThrowIfNull(onCompleted);
        string name = string.IsNullOrWhiteSpace(texture.Name) ? "UnnamedTexture" : texture.Name;
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
            catch (Exception exception)
            {
                onError?.Invoke(exception);
            }
        }, $"XRTexture2D.ScheduleVulkanSparseCompatTransition[{name}]", RenderThreadJobKind.TextureUpload);
        return true;
    }

    internal SparseTextureStreamingFinalizeResult FinalizeTransition(
        XRTexture2D texture,
        SparseTextureStreamingTransitionRequest request,
        SparseTextureStreamingTransitionResult result)
    {
        if (!result.Applied)
            return SparseTextureStreamingFinalizeResult.Failed(
                result.FailureReason ?? "Vulkan sparse compatibility transition did not apply.");

        return result.ExposureDeferred
            ? SparseTextureStreamingFinalizeResult.Failed(
                "Vulkan dense sparse-compat transitions are not deferred; no sparse fence finalization is available.")
            : SparseTextureStreamingFinalizeResult.Success();
    }
}
