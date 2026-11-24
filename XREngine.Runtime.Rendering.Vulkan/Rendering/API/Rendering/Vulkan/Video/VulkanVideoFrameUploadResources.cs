namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Gives <see cref="VulkanVideoFrameUploadContext"/> the device state, the synchronous upload
/// gate inputs, and the wrapper lookups of one renderer generation's resource runtime.
/// </summary>
internal sealed class VulkanVideoFrameUploadResources(VulkanResourceRuntime resources) : IVulkanVideoFrameUploadResources
{
    private readonly VulkanResourceRuntime _resources = resources ?? throw new ArgumentNullException(nameof(resources));

    /// <inheritdoc />
    public bool IsDeviceOperational => _resources.BackendObjectContext is { IsDeviceOperational: true };

    /// <inheritdoc />
    public bool AllowSynchronousResourceUploads => _resources.AllowSynchronousResourceUploads;

    /// <inheritdoc />
    public bool ShouldAvoidSynchronousImageAllocationForOpenXr()
    {
        VulkanBackendObjectContext context = _resources.BackendObjectContext
            ?? throw new InvalidOperationException("The OpenXR allocation pressure check needs a published backend object context.");
        return _resources.ShouldAvoidSynchronousImageAllocationForOpenXr(
            context.Api,
            context.DeviceContext,
            RuntimeRenderingHostServices.Presentation,
            RuntimeRenderingHostServices.FrameTiming,
            out _);
    }

    /// <inheritdoc />
    public AbstractRenderAPIObject? GetPublishedWrapper(XRTexture2D texture)
        => _resources.BackendObjects.Get(texture);

    /// <inheritdoc />
    public AbstractRenderAPIObject? CreateWrapper(XRTexture2D texture)
        => _resources.CreateAPIRenderObject(texture);
}
