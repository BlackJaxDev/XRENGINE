using Silk.NET.Vulkan;
using XREngine.Rendering.API.Rendering.OpenXR;

namespace XREngine.Rendering.Vulkan;

/// <summary>Typed Streamline provisioning publication owned by the output runtime.</summary>
internal sealed partial class VulkanOutputRuntime
{
    internal Format SwapchainImageFormat => Desktop.ImageFormat;
    internal bool StreamlineFrameGenerationSwapchainIncludesDlss => Desktop.StreamlineFrameGenerationIncludesDlss;

    internal bool StreamlineFrameGenerationProvisioned => CaptureStreamlineProvisioning().FrameGenerationProvisioned;

    internal void PrepareStreamlineVulkanRequirements(bool isSecondaryGpuContext, bool renderDocFriendly)
        => PublishStreamlineProvisioning(VulkanUpscaleBridgeSidecar.PrepareStreamlineVulkanRequirements(isSecondaryGpuContext, renderDocFriendly));

    internal void ValidateStreamlineSelectedPhysicalDevice(nint physicalDeviceHandle)
        => PublishStreamlineProvisioning(VulkanUpscaleBridgeSidecar.ValidateSelectedPhysicalDevice(CaptureStreamlineProvisioning(), physicalDeviceHandle));

    internal bool TryCreateDlssSession(VulkanDeviceContext deviceContext, uint viewportId, out IRuntimeVendorUpscaleSession? session, out string failureReason)
        => VulkanUpscaleBridgeSidecar.TryCreateDlssSession(CaptureStreamlineDeviceBinding(deviceContext), viewportId, out session, out failureReason);

    internal bool TryCreateFrameGenerationSession(VulkanDeviceContext deviceContext, uint viewportId, out IRuntimeVendorUpscaleSession? session, out string failureReason)
        => VulkanUpscaleBridgeSidecar.TryCreateFrameGenerationSession(CaptureStreamlineDeviceBinding(deviceContext), CaptureFrameGenerationOutput(), viewportId, out session, out failureReason);

    internal static bool ShouldProvisionOptionalStreamlineFrameGeneration(bool toggles, bool runtimeAvailable, bool supported)
        => VulkanUpscaleBridgeSidecar.ShouldProvisionOptionalStreamlineFrameGeneration(toggles, runtimeAvailable, supported);

    internal static void ResetPhase524bDesktopRejectionEvidence(bool injectionRequested)
    {
        lock (Phase524bDesktopRejectionEvidenceLock)
            Phase524bDesktopRejectionEvidence = new() { Injected = injectionRequested };
    }

    internal static OpenXrSmokeDesktopRejectionEvidence CapturePhase524bDesktopRejectionEvidence()
    {
        lock (Phase524bDesktopRejectionEvidenceLock)
            return Phase524bDesktopRejectionEvidence;
    }

    internal VulkanStreamlineProvisioningSnapshot CaptureStreamlineProvisioning()
        => new(
            _streamlineDlssProvisioned,
            _streamlineFrameGenerationProvisioned,
            _streamlineRequiredInstanceExtensions,
            _streamlineRequiredDeviceExtensions,
            _streamlineRequiredFeatures12,
            _streamlineRequiredFeatures13,
            _streamlineQueueRequirements,
            _streamlineMinimumApiVersion,
            _streamlineGraphicsQueueFamily,
            _streamlineGraphicsQueueIndex,
            _streamlineComputeQueueFamily,
            _streamlineComputeQueueIndex,
            _streamlineOpticalFlowQueueFamily,
            _streamlineOpticalFlowQueueIndex);

    internal VulkanFrameGenerationOutputSnapshot CaptureFrameGenerationOutput()
        => new(Desktop.Extent, (uint)(Desktop.Images?.Length ?? 0), Desktop.ImageFormat);

    internal void PublishStreamlineProvisioning(VulkanStreamlineProvisioningSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _streamlineDlssProvisioned = snapshot.DlssProvisioned;
        _streamlineFrameGenerationProvisioned = snapshot.FrameGenerationProvisioned;
        _streamlineRequiredInstanceExtensions = snapshot.RequiredInstanceExtensions;
        _streamlineRequiredDeviceExtensions = snapshot.RequiredDeviceExtensions;
        _streamlineRequiredFeatures12 = snapshot.RequiredFeatures12;
        _streamlineRequiredFeatures13 = snapshot.RequiredFeatures13;
        _streamlineQueueRequirements = snapshot.QueueRequirements;
        _streamlineMinimumApiVersion = snapshot.MinimumApiVersion;
        _streamlineGraphicsQueueFamily = snapshot.GraphicsQueueFamily;
        _streamlineGraphicsQueueIndex = snapshot.GraphicsQueueIndex;
        _streamlineComputeQueueFamily = snapshot.ComputeQueueFamily;
        _streamlineComputeQueueIndex = snapshot.ComputeQueueIndex;
        _streamlineOpticalFlowQueueFamily = snapshot.OpticalFlowQueueFamily;
        _streamlineOpticalFlowQueueIndex = snapshot.OpticalFlowQueueIndex;
    }
}
