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
        string[] normalized = VulkanRenderer.NormalizeDeviceExtensionSelection(
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
        string[] normalized = VulkanRenderer.NormalizeDeviceExtensionSelection(
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
        string[] normalized = VulkanRenderer.NormalizeDeviceExtensionSelection(
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
    public void GranularOpenXrStreamlineFeatureChain_AcceptsRepresentedPromotedFeatures()
    {
        bool accepted = VulkanRenderer.TryUseGranularOpenXrStreamlineFeatureChain(
            ["timelineSemaphore", "descriptorIndexing", "bufferDeviceAddress"],
            ["dynamicRendering", "synchronization2", "maintenance4", "pipelineCreationCacheControl", "privateData"],
            out string failureReason);

        accepted.ShouldBeTrue(failureReason);
        failureReason.ShouldBeEmpty();
    }

    [Test]
    public void GranularOpenXrStreamlineFeatureChain_RejectsUnrepresentedPromotedFeatures()
    {
        bool accepted = VulkanRenderer.TryUseGranularOpenXrStreamlineFeatureChain(
            ["shaderFloat16"],
            [],
            out string failureReason);

        accepted.ShouldBeFalse();
        failureReason.ShouldContain("shaderFloat16");
    }
}
