using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanLogicalDeviceExtensionSelectionTests
{
    [Test]
    public void NormalizeDeviceExtensionSelection_PrefersKhrAndRemovesDuplicates()
    {
        string[] normalized = VulkanDeviceContext.NormalizeDeviceExtensionSelection(
            [
                "VK_KHR_swapchain",
                "VK_KHR_buffer_device_address",
                "VK_EXT_buffer_device_address",
                "VK_KHR_buffer_device_address",
            ],
            vulkan12PromotedToCore: false);

        normalized.ShouldBe(
        [
            "VK_KHR_swapchain",
            "VK_KHR_buffer_device_address",
        ]);
    }

    [Test]
    public void NormalizeDeviceExtensionSelection_UsesCoreInsteadOfLegacyExtOnVulkan12()
    {
        string[] normalized = VulkanDeviceContext.NormalizeDeviceExtensionSelection(
            [
                "VK_KHR_swapchain",
                "VK_EXT_buffer_device_address",
            ],
            vulkan12PromotedToCore: true);

        normalized.ShouldBe(["VK_KHR_swapchain"]);
    }

    [Test]
    public void NormalizeDeviceExtensionSelection_RetainsLegacyExtBeforeVulkan12WhenNoKhrPathExists()
    {
        string[] normalized = VulkanDeviceContext.NormalizeDeviceExtensionSelection(
            [
                "VK_KHR_swapchain",
                "VK_EXT_buffer_device_address",
            ],
            vulkan12PromotedToCore: false);

        normalized.ShouldBe(
        [
            "VK_KHR_swapchain",
            "VK_EXT_buffer_device_address",
        ]);
    }

    [Test]
    public void OpenXrStreamlineFeatureRequirements_ArePassedThroughTypedBootstrapFacts()
    {
        string source = ReadLogicalDeviceBootstrapSource();

        source.ShouldContain("StreamlineRequirementSet(");
        source.ShouldContain("string[] RequiredFeatures12");
        source.ShouldContain("string[] RequiredFeatures13");
        source.ShouldContain("_outputRuntime._streamlineRequiredFeatures12");
        source.ShouldContain("_outputRuntime._streamlineRequiredFeatures13");
    }

    [Test]
    public void OpenXrStreamlineFeatureRequirements_UseTheGranularFeatureChainAtTheBootstrapBoundary()
    {
        string source = ReadLogicalDeviceBootstrapSource();

        source.ShouldContain("useGranularOpenXrStreamlineFeatureChain");
        source.ShouldContain("TryUseGranularOpenXrStreamlineFeatureChain(");
        source.ShouldContain("throw new InvalidOperationException(granularFeatureFailure)");
    }

    private static string ReadLogicalDeviceBootstrapSource()
        => SourceContractWorkspace.ReadFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/Device/VulkanDeviceContext.LogicalDeviceBootstrap.cs");
}
