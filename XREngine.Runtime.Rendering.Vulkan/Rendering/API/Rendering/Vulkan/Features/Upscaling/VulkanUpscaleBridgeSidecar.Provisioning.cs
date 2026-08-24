using Silk.NET.Vulkan;
using XREngine.Rendering.DLSS;

namespace XREngine.Rendering.Vulkan;

/// <summary>Streamline requirement policy isolated from renderer and output authorities.</summary>
internal sealed unsafe partial class VulkanUpscaleBridgeSidecar
{
    internal static VulkanStreamlineProvisioningSnapshot PrepareStreamlineVulkanRequirements(
        bool isSecondaryGpuContext,
        bool renderDocFriendly)
    {
        if (isSecondaryGpuContext)
        {
            Debug.Rendering("[Vulkan] Streamline disabled for the secondary GPU compute context; the process-global runtime remains owned by the presentation renderer.");
            return VulkanStreamlineProvisioningSnapshot.Empty;
        }

        bool dlssRequested = RuntimeEngine.EffectiveSettings.EnableNvidiaDlss
            || RuntimeEngine.EffectiveSettings.AntiAliasingMode == EAntiAliasingMode.Dlaa;
        bool frameGenerationRequested = NvidiaDlssManager.IsFrameGenerationRequested;
        bool provisionRuntimeToggles = !renderDocFriendly && ShouldProvisionStreamlineRuntimeToggles();
        if (renderDocFriendly
            && !dlssRequested
            && !frameGenerationRequested
            && (NvidiaDlssManager.RequiredRuntimeDllsAvailable
                || NvidiaDlssManager.FrameGenerationRuntimeDllsAvailable))
        {
            Debug.Rendering("[Vulkan] RenderDoc-friendly diagnostics skipped optional Streamline runtime-toggle provisioning. Explicit DLSS/DLSS-G requests remain strict.");
        }

        bool frameGenerationRuntimeAvailable = NvidiaDlssManager.FrameGenerationRuntimeDllsAvailable;
        if (frameGenerationRequested && !frameGenerationRuntimeAvailable)
            throw new InvalidOperationException(NvidiaDlssManager.FrameGenerationRuntimeDllsUnavailableReason);

        bool frameGenerationSupported = false;
        string unavailableReason = string.Empty;
        if ((frameGenerationRequested || provisionRuntimeToggles) && frameGenerationRuntimeAvailable)
        {
            frameGenerationSupported = NvidiaDlssManager.Native.TryCheckFrameGenerationSupport(
                vulkanPhysicalDevice: 0,
                out unavailableReason);
        }

        if (frameGenerationRequested && !frameGenerationSupported)
        {
            throw new InvalidOperationException(
                $"Requested NVIDIA DLSS frame generation is unsupported before Vulkan instance creation: {unavailableReason}");
        }
        if (!frameGenerationRequested
            && provisionRuntimeToggles
            && frameGenerationRuntimeAvailable
            && !frameGenerationSupported)
        {
            Debug.RenderingWarning(
                "[Vulkan] Optional DLSS-G runtime-toggle provisioning skipped because no supported adapter was found. Reason={0}",
                unavailableReason);
        }

        bool includeDlss = dlssRequested
            || (provisionRuntimeToggles && NvidiaDlssManager.RequiredRuntimeDllsAvailable);
        bool includeFrameGeneration = frameGenerationRequested
            || ShouldProvisionOptionalStreamlineFrameGeneration(
                provisionRuntimeToggles,
                frameGenerationRuntimeAvailable,
                frameGenerationSupported);
        if (!includeDlss && !includeFrameGeneration)
            return VulkanStreamlineProvisioningSnapshot.Empty;
        if (dlssRequested && !NvidiaDlssManager.RequiredRuntimeDllsAvailable)
            throw new InvalidOperationException(NvidiaDlssManager.RequiredRuntimeDllsUnavailableReason);

        return ResolveStreamlineVulkanRequirements(includeDlss, includeFrameGeneration);
    }

    internal static VulkanStreamlineProvisioningSnapshot ValidateSelectedPhysicalDevice(
        VulkanStreamlineProvisioningSnapshot snapshot,
        nint physicalDeviceHandle)
    {
        if (!snapshot.FrameGenerationProvisioned)
            return snapshot;
        if (NvidiaDlssManager.Native.TryCheckFrameGenerationSupport(
                physicalDeviceHandle,
                out string failureReason))
            return snapshot;
        if (NvidiaDlssManager.IsFrameGenerationRequested)
        {
            throw new InvalidOperationException(
                $"Requested NVIDIA DLSS frame generation is unsupported on the selected Vulkan physical device: {failureReason}");
        }

        Debug.RenderingWarning(
            "[Vulkan] Optional DLSS-G runtime-toggle provisioning disabled for the selected physical device. Reason={0}",
            failureReason);
        return ResolveStreamlineVulkanRequirements(snapshot.DlssProvisioned, includeFrameGeneration: false);
    }

    internal static VulkanStreamlineProvisioningSnapshot ResolveStreamlineVulkanRequirements(
        bool includeDlss,
        bool includeFrameGeneration)
    {
        if (!includeDlss && !includeFrameGeneration)
            return VulkanStreamlineProvisioningSnapshot.Empty;
        if (!NvidiaDlssManager.Native.TryGetRequiredVulkanRequirements(
                includeDlss,
                includeFrameGeneration,
                out string[] instanceExtensions,
                out string[] deviceExtensions,
                out string[] features12,
                out string[] features13,
                out NvidiaDlssManager.Native.StreamlineQueueRequirements queueRequirements,
                out string failureReason))
        {
            throw new InvalidOperationException(
                $"Requested NVIDIA DLSS Vulkan requirements could not be resolved before device creation: {failureReason}");
        }

        uint minimumApiVersion = features13.Length > 0
            ? Vk.Version13
            : features12.Length > 0
                ? Vk.Version12
                : Vk.Version11;
        Debug.Rendering(
            "[Vulkan] Streamline requirements prepared. DLSS={0} DLSS-G={1} InstanceExtensions=[{2}] DeviceExtensions=[{3}] Features12=[{4}] Features13=[{5}] ExtraQueues=G{6}/C{7}/OF{8}",
            includeDlss,
            includeFrameGeneration,
            string.Join(",", instanceExtensions),
            string.Join(",", deviceExtensions),
            string.Join(",", features12),
            string.Join(",", features13),
            queueRequirements.GraphicsQueues,
            queueRequirements.ComputeQueues,
            queueRequirements.OpticalFlowQueues);
        return new VulkanStreamlineProvisioningSnapshot(
            includeDlss,
            includeFrameGeneration,
            instanceExtensions,
            deviceExtensions,
            features12,
            features13,
            queueRequirements,
            minimumApiVersion);
    }

    internal static bool ShouldProvisionOptionalStreamlineFrameGeneration(
        bool provisionRuntimeToggles,
        bool runtimeDllsAvailable,
        bool featureSupported)
        => provisionRuntimeToggles && runtimeDllsAvailable && featureSupported;

    private static bool ShouldProvisionStreamlineRuntimeToggles()
    {
        if (!NvidiaDlssManager.RequiredRuntimeDllsAvailable
            && !NvidiaDlssManager.FrameGenerationRuntimeDllsAvailable)
            return false;

        VulkanUpscaleBridgeProbeResult probe = VulkanUpscaleBridgeProbe.Probe("NVIDIA", null);
        const uint nvidiaVendorId = 0x10DE;
        return probe.ProbeSucceeded && probe.SelectedVendorId == nvidiaVendorId;
    }
}
