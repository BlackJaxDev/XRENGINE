namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Supplies the generation state and the wrapper lookups that
/// <see cref="VulkanVideoFrameUploadContext"/> uses for one frame upload: the device state, the
/// inputs of the synchronous upload gate, and the published and creating wrapper lookups.
/// </summary>
/// <remarks>
/// The context applies the gate and the lookup order itself. This interface only gives the
/// inputs, so a test can replace them and check the gate wiring without a Vulkan device.
/// </remarks>
internal interface IVulkanVideoFrameUploadResources
{
    /// <summary>Gets whether the generation's device is operational.</summary>
    bool IsDeviceOperational { get; }

    /// <summary>Gets the generation's synchronous resource upload policy.</summary>
    bool AllowSynchronousResourceUploads { get; }

    /// <summary>
    /// Returns whether OpenXR image allocation pressure blocks synchronous image uploads now.
    /// Call this only while the device is operational.
    /// </summary>
    bool ShouldAvoidSynchronousImageAllocationForOpenXr();

    /// <summary>Returns the published wrapper of <paramref name="texture"/>, or <c>null</c>. It creates no wrapper.</summary>
    AbstractRenderAPIObject? GetPublishedWrapper(XRTexture2D texture);

    /// <summary>Creates and publishes the wrapper of <paramref name="texture"/> through the generation's creating lookup.</summary>
    AbstractRenderAPIObject? CreateWrapper(XRTexture2D texture);
}
