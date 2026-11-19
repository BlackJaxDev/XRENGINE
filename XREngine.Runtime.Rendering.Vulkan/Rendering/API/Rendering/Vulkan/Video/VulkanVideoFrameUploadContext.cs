using XREngine.Rendering.VideoStreaming;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Resolves the Vulkan wrapper of a video texture for one frame upload. It first confirms
/// that this generation admits synchronous resource uploads.
/// </summary>
/// <remarks>
/// <para>
/// The gate is the same as the one that descriptor preparation applies to an assigned
/// texture: the generation's synchronous upload policy, and the OpenXR image allocation
/// pressure check. When the gate blocks, the context creates no wrapper and returns a
/// failure reason, so the video keeps its last uploaded frame.
/// </para>
/// <para>
/// A stream size change resizes the texture before the upload. The resize destroys the
/// image, and the registry then drops the wrapper identity. On a registry miss the context
/// resolves the wrapper through the generation's creating lookup, which is the path that
/// descriptor preparation uses for the same draw. The registry owns wrapper identity, and
/// only that lookup publishes a new identity, under the registry's creation lock. The
/// descriptor preparation that follows then finds the same wrapper.
/// </para>
/// <para>Use one instance on the render thread only.</para>
/// </remarks>
internal sealed class VulkanVideoFrameUploadContext : IVulkanVideoFrameUploadContext
{
    /// <summary>The failure reason when the generation does not admit synchronous resource uploads.</summary>
    internal const string SynchronousUploadsBlockedReason =
        "This Vulkan renderer generation does not admit synchronous resource uploads, so the video frame was not uploaded.";

    /// <summary>The failure reason when OpenXR image allocation pressure blocks synchronous uploads.</summary>
    internal const string OpenXrAllocationPressureReason =
        "OpenXR image allocation pressure blocks synchronous resource uploads, so the video frame was not uploaded.";

    private const string DeviceNotOperationalReason = "The Vulkan device is not operational.";

    private readonly VulkanResourceRuntime _resources;
    private VulkanVideoFrameTextureHandle? _cachedHandle;

    public VulkanVideoFrameUploadContext(VulkanResourceRuntime resources)
        => _resources = resources ?? throw new ArgumentNullException(nameof(resources));

    /// <inheritdoc />
    public IVulkanVideoFrameTextureHandle? ResolveTexture(XRTexture2D texture, out string? failureReason)
    {
        if (_resources.BackendObjectContext is not { IsDeviceOperational: true } context)
        {
            failureReason = DeviceNotOperationalReason;
            return null;
        }

        bool allowSynchronousUploads = _resources.AllowSynchronousResourceUploads;
        bool avoidImageAllocationForOpenXr =
            allowSynchronousUploads &&
            _resources.ShouldAvoidSynchronousImageAllocationForOpenXr(
                context.Api,
                context.DeviceContext,
                RuntimeRenderingHostServices.Presentation,
                RuntimeRenderingHostServices.FrameTiming,
                out _);
        failureReason = GetSynchronousUploadBlockReason(allowSynchronousUploads, avoidImageAllocationForOpenXr);
        if (failureReason is not null)
            return null;

        AbstractRenderAPIObject? apiObject = _resources.BackendObjects.Get(texture) ?? _resources.CreateAPIRenderObject(texture);
        if (apiObject is not VkTexture2D wrapper)
        {
            failureReason = $"The video texture resolved to '{apiObject?.GetType().Name ?? "<null>"}', not a Vulkan 2D texture wrapper.";
            return null;
        }

        // Keep one handle while the wrapper stays the same, so a frame upload does not allocate.
        VulkanVideoFrameTextureHandle? handle = _cachedHandle;
        if (handle is null || !handle.Wraps(wrapper))
            _cachedHandle = handle = new VulkanVideoFrameTextureHandle(wrapper);

        return handle;
    }

    /// <summary>
    /// Returns why a synchronous video frame upload is blocked, or <c>null</c> when it is admitted.
    /// The checks match the synchronous texture upload gate of descriptor preparation.
    /// </summary>
    /// <param name="allowSynchronousUploads">The generation's synchronous resource upload policy.</param>
    /// <param name="avoidImageAllocationForOpenXr">The result of the OpenXR image allocation pressure check.</param>
    internal static string? GetSynchronousUploadBlockReason(bool allowSynchronousUploads, bool avoidImageAllocationForOpenXr)
    {
        if (!allowSynchronousUploads)
            return SynchronousUploadsBlockedReason;

        return avoidImageAllocationForOpenXr
            ? OpenXrAllocationPressureReason
            : null;
    }
}
