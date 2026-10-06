using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanStreamlineProvisioningTests
{
    [TestCase(false, true, true, false)]
    [TestCase(true, false, true, false)]
    [TestCase(true, true, false, false)]
    [TestCase(true, true, true, true)]
    public void OptionalFrameGenerationProvisioning_RequiresTogglePolicyRuntimeAndAdapterSupport(
        bool provisionRuntimeToggles,
        bool runtimeDllsAvailable,
        bool featureSupported,
        bool expected)
    {
        VulkanRenderer.ShouldProvisionOptionalStreamlineFrameGeneration(
            provisionRuntimeToggles,
            runtimeDllsAvailable,
            featureSupported).ShouldBe(expected);
    }

    [Test]
    public void FrameGenerationProvisioning_UsesAdapterCapabilityAndKeepsExplicitRequestsStrict()
    {
        string nativeSource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/DLSS/StreamlineNative.cs");
        nativeSource.ShouldContain("TryLoadExport(\"slIsFeatureSupported\", out _isFeatureSupported)");
        nativeSource.ShouldContain("StreamlineResult supportResult = CallIsFeatureSupported(FeatureDlssG, ref adapterInfo);");
        nativeSource.ShouldContain("VkPhysicalDevice = (IntPtr)vulkanPhysicalDevice,");

        string requirementsSource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Features/Upscaling/VulkanUpscaleBridgeSidecar.Provisioning.cs");
        requirementsSource.ShouldContain("TryCheckFrameGenerationSupport(");
        requirementsSource.ShouldContain("ShouldProvisionOptionalStreamlineFrameGeneration(");
        string initializationSource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/Authority/VulkanFrameLoop.Lifecycle.cs");
        initializationSource.ShouldContain("_outputRuntime.ValidateStreamlineSelectedPhysicalDevice((nint)_deviceContext.PhysicalDevice.Handle);");
        requirementsSource.ShouldContain("if (frameGenerationRequested && !frameGenerationSupported)");
        requirementsSource.ShouldContain("if (NvidiaDlssManager.IsFrameGenerationRequested)");

        int physicalDeviceIndex = initializationSource.IndexOf("SelectPhysicalDevice();", StringComparison.Ordinal);
        int capabilityIndex = initializationSource.IndexOf("_outputRuntime.ValidateStreamlineSelectedPhysicalDevice((nint)_deviceContext.PhysicalDevice.Handle);", StringComparison.Ordinal);
        int logicalDeviceIndex = initializationSource.IndexOf("CreateLogicalDevice();", StringComparison.Ordinal);
        physicalDeviceIndex.ShouldBeGreaterThanOrEqualTo(0);
        capabilityIndex.ShouldBeGreaterThan(physicalDeviceIndex);
        logicalDeviceIndex.ShouldBeGreaterThan(capabilityIndex);

        string swapchainSource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Output/Authority/VulkanDesktopSwapchainService.cs");
        requirementsSource.ShouldContain("if (NvidiaDlssManager.IsFrameGenerationRequested)");
        swapchainSource.ShouldContain("Optional DLSS-G proxy-swapchain provisioning failed");
        swapchainSource.ShouldContain("_streamlineFrameGenerationProvisioned = false;");
    }

    [Test]
    public void OptionalStreamlineQueues_DegradeBeforeExplicitRequestsFail()
    {
        string logicalDeviceSource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/Device/VulkanDeviceContext.LogicalDeviceBootstrap.cs");

        logicalDeviceSource.ShouldContain("bool CanProvisionStreamlineQueues(out string failureReason)");
        logicalDeviceSource.ShouldContain("while (!CanProvisionStreamlineQueues(out string streamlineQueueFailure))");
        logicalDeviceSource.ShouldContain(
            "if (_outputRuntime._streamlineFrameGenerationProvisioned && !frameGenerationExplicitlyRequested)");
        logicalDeviceSource.ShouldContain(
            "ResolveStreamlineVulkanRequirements(_outputRuntime._streamlineDlssProvisioned, includeFrameGeneration: false);");
        logicalDeviceSource.ShouldContain(
            "if (_outputRuntime._streamlineDlssProvisioned && !dlssExplicitlyRequested)");
        logicalDeviceSource.ShouldContain(
            "ResolveStreamlineVulkanRequirements(includeDlss: false, includeFrameGeneration: false);");

        int optionalFrameGenerationFallback = logicalDeviceSource.IndexOf(
            "if (_outputRuntime._streamlineFrameGenerationProvisioned && !frameGenerationExplicitlyRequested)",
            StringComparison.Ordinal);
        int optionalDlssFallback = logicalDeviceSource.IndexOf(
            "if (_outputRuntime._streamlineDlssProvisioned && !dlssExplicitlyRequested)",
            StringComparison.Ordinal);
        int strictFailure = logicalDeviceSource.IndexOf(
            "throw new NotSupportedException(streamlineQueueFailure);",
            StringComparison.Ordinal);
        optionalFrameGenerationFallback.ShouldBeLessThan(optionalDlssFallback);
        optionalDlssFallback.ShouldBeLessThan(strictFailure);
    }

    private static string ReadWorkspaceFile(string relativePath)
        => SourceContractWorkspace.ReadFile(relativePath);
}
