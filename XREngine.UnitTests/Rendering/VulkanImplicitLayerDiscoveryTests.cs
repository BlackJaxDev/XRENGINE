using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanImplicitLayerDiscoveryTests
{
    [Test]
    public void ManifestDefinesLayer_AcceptsSingleLayerObject()
    {
        const string json = """
            {
              "file_format_version": "1.2.0",
              "layer": {
                "name": "VK_LAYER_OBS_HOOK"
              }
            }
            """;

        VulkanImplicitLayerDiscovery.ManifestDefinesLayer(json, "VK_LAYER_OBS_HOOK").ShouldBeTrue();
    }

    [Test]
    public void ManifestDefinesLayer_AcceptsLayersArray()
    {
        const string json = """
            {
              "file_format_version": "1.2.0",
              "layers": [
                { "name": "VK_LAYER_OTHER" },
                { "name": "VK_LAYER_OBS_HOOK" }
              ]
            }
            """;

        VulkanImplicitLayerDiscovery.ManifestDefinesLayer(json, "VK_LAYER_OBS_HOOK").ShouldBeTrue();
    }

    [Test]
    public void ManifestDefinesLayer_RejectsDifferentLayer()
    {
        const string json = """
            {
              "layer": {
                "name": "VK_LAYER_OTHER"
              }
            }
            """;

        VulkanImplicitLayerDiscovery.ManifestDefinesLayer(json, "VK_LAYER_OBS_HOOK").ShouldBeFalse();
    }

    [Test]
    public void ManifestDefinesLayer_RejectsMalformedJson()
        => VulkanImplicitLayerDiscovery.ManifestDefinesLayer(
            "{ not-valid-json",
            "VK_LAYER_OBS_HOOK").ShouldBeFalse();
}
