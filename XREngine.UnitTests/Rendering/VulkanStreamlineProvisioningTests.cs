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
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Features/Upscaling/VulkanRenderer.StreamlineRequirements.cs");
        requirementsSource.ShouldContain("TryCheckFrameGenerationSupport(");
        requirementsSource.ShouldContain("ShouldProvisionOptionalStreamlineFrameGeneration(");
        requirementsSource.ShouldContain("ValidateStreamlineSelectedPhysicalDevice()");
        requirementsSource.ShouldContain("if (frameGenerationRequested && !frameGenerationSupported)");
        requirementsSource.ShouldContain("if (NvidiaDlssManager.IsFrameGenerationRequested)");

        string initializationSource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.Initialization.cs");
        int physicalDeviceIndex = initializationSource.IndexOf("PickPhysicalDevice();", StringComparison.Ordinal);
        int capabilityIndex = initializationSource.IndexOf("ValidateStreamlineSelectedPhysicalDevice();", StringComparison.Ordinal);
        int logicalDeviceIndex = initializationSource.IndexOf("CreateLogicalDevice();", StringComparison.Ordinal);
        physicalDeviceIndex.ShouldBeGreaterThanOrEqualTo(0);
        capabilityIndex.ShouldBeGreaterThan(physicalDeviceIndex);
        logicalDeviceIndex.ShouldBeGreaterThan(capabilityIndex);

        string swapchainSource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.Swapchain.cs");
        swapchainSource.ShouldContain("if (NvidiaDlssManager.IsFrameGenerationRequested)");
        swapchainSource.ShouldContain("Optional DLSS-G proxy-swapchain provisioning failed");
        swapchainSource.ShouldContain("_streamlineFrameGenerationProvisioned = false;");
    }

    [Test]
    public void OptionalStreamlineQueues_DegradeBeforeExplicitRequestsFail()
    {
        string logicalDeviceSource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.LogicalDevice.cs");

        logicalDeviceSource.ShouldContain("bool CanProvisionStreamlineQueues(out string failureReason)");
        logicalDeviceSource.ShouldContain("while (!CanProvisionStreamlineQueues(out string streamlineQueueFailure))");
        logicalDeviceSource.ShouldContain(
            "if (_streamlineFrameGenerationProvisioned && !frameGenerationExplicitlyRequested)");
        logicalDeviceSource.ShouldContain(
            "ResolveStreamlineVulkanRequirements(_streamlineDlssProvisioned, includeFrameGeneration: false);");
        logicalDeviceSource.ShouldContain(
            "if (_streamlineDlssProvisioned && !dlssExplicitlyRequested)");
        logicalDeviceSource.ShouldContain(
            "ResolveStreamlineVulkanRequirements(includeDlss: false, includeFrameGeneration: false);");

        int optionalFrameGenerationFallback = logicalDeviceSource.IndexOf(
            "if (_streamlineFrameGenerationProvisioned && !frameGenerationExplicitlyRequested)",
            StringComparison.Ordinal);
        int optionalDlssFallback = logicalDeviceSource.IndexOf(
            "if (_streamlineDlssProvisioned && !dlssExplicitlyRequested)",
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
