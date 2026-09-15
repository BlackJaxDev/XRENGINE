namespace XREngine.Rendering.Vulkan;

/// <summary>Captures the originating XR frame and independently acquired eye images for a copy submission.</summary>
internal readonly record struct OpenXrImageSubmissionIdentity(
    OpenXrSubmissionMetadata Frame,
    uint ViewMask,
    uint FirstEyeImageIndex,
    uint SecondEyeImageIndex)
{
    /// <summary>Preserves the runtime image index in the corresponding eye, including right-eye-only copies.</summary>
    public static OpenXrImageSubmissionIdentity ForEye(
        OpenXrSubmissionMetadata frame,
        uint viewIndex,
        uint imageIndex)
        => new(frame, 1u << checked((int)viewIndex),
            viewIndex == 0 ? imageIndex : 0u,
            viewIndex == 1 ? imageIndex : 0u);
}
