using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanResourceBindingKeyTests
{
    [TestCase("tex::SceneColor", nameof(EVulkanResourceBindingKind.Texture), "SceneColor", "")]
    [TestCase("TEX::SceneColor", nameof(EVulkanResourceBindingKind.Texture), "SceneColor", "")]
    [TestCase("buf::DrawCommands", nameof(EVulkanResourceBindingKind.Buffer), "DrawCommands", "")]
    [TestCase("fbo::Lighting", nameof(EVulkanResourceBindingKind.FrameBuffer), "Lighting", "color")]
    [TestCase("fbo::Lighting::depth", nameof(EVulkanResourceBindingKind.FrameBuffer), "Lighting", "depth")]
    [TestCase("LegacyName", nameof(EVulkanResourceBindingKind.Unqualified), "LegacyName", "")]
    public void TryParse_ClassifiesBindingGrammar(
        string source,
        string expectedKind,
        string expectedName,
        string expectedSlot)
    {
        VulkanResourceBindingKey.TryParse(source, out VulkanResourceBindingKey key)
            .ShouldBeTrue();

        key.Kind.ToString().ShouldBe(expectedKind);
        key.Name.ShouldBe(expectedName);
        key.Slot.ShouldBe(expectedSlot);
        key.IsExplicit.ShouldBe(
            expectedKind is nameof(EVulkanResourceBindingKind.Texture)
                or nameof(EVulkanResourceBindingKind.FrameBuffer)
                or nameof(EVulkanResourceBindingKind.Buffer));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("  ")]
    [TestCase("tex::")]
    [TestCase("buf::")]
    [TestCase("fbo::")]
    [TestCase("fbo::::depth")]
    public void TryParse_RejectsMissingBindingOrExplicitResourceName(string? source)
    {
        VulkanResourceBindingKey.TryParse(source, out VulkanResourceBindingKey key)
            .ShouldBeFalse();
        key.ShouldBe(default);
    }

    [Test]
    public void TryParse_RecognizesOutputBoundaryCaseInsensitively()
    {
        VulkanResourceBindingKey.TryParse(
                RenderGraphResourceNames.OutputRenderTarget.ToUpperInvariant(),
                out VulkanResourceBindingKey key)
            .ShouldBeTrue();

        key.Kind.ShouldBe(EVulkanResourceBindingKind.Output);
        key.Name.ShouldBe(RenderGraphResourceNames.OutputRenderTarget);
        key.Slot.ShouldBeEmpty();
        key.IsExplicit.ShouldBeFalse();
    }
}
