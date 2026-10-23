using NUnit.Framework;
using Shouldly;
using Silk.NET.Vulkan;
using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Rendering.Vulkan;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class UIBatchScissorTests
{
    [Test]
    public void CanvasCropMapsIntoOffsetScaledRasterRegion()
    {
        BoundingRectangle result = UIBatchCollector.MapCanvasCropToRaster(
            new BoundingRectangle(10, 20, 30, 40),
            new BoundingRectangleF(0, 0, 100, 100),
            new BoundingRectangle(200, 300, 400, 200));

        result.ShouldBe(new BoundingRectangle(240, 340, 120, 80));
    }

    [Test]
    public void CanvasCropClampsToActualRasterTarget()
    {
        BoundingRectangle result = UIBatchCollector.MapCanvasCropToRaster(
            new BoundingRectangle(-10, 80, 30, 40),
            new BoundingRectangleF(0, 0, 100, 100),
            new BoundingRectangle(50, 75, 200, 100));

        result.ShouldBe(new BoundingRectangle(50, 155, 40, 20));
    }

    [Test]
    public void SplitViewportCropSubtractsCanvasOriginBeforeDpiScale()
    {
        BoundingRectangle result = UIBatchCollector.MapCanvasCropToRaster(
            new BoundingRectangle(120, 220, 20, 10),
            new BoundingRectangleF(100, 200, 100, 50),
            new BoundingRectangle(300, 400, 200, 100));

        result.ShouldBe(new BoundingRectangle(340, 440, 40, 20));
    }

    [Test, NonParallelizable]
    public void NestedClipsMapThroughOffsetDpiRasterRegionAndVulkanOrigin()
    {
        using UITestShaderScope shaders = new();
        using IDisposable target = RuntimeEngineMaterialConstructionServices.InstallForCurrentThread(
            EngineMaterialConstructionTarget.DesktopGlsl);
        SceneNode root = new("Clipped canvas", new UICanvasTransform()) { IsActiveSelf = false };
        root.SetWorldContext(new TestWorldContext());
        root.AddComponent<XREngine.Components.UICanvasComponent>();
        UICanvasTransform canvas = (UICanvasTransform)root.Transform;
        canvas.Width = 100;
        canvas.Height = 100;
        canvas.DpiScale = 2;
        SceneNode outerNode = new(root) { Name = "Outer clip" };
        UIBoundableTransform outer = outerNode.SetTransform<UIBoundableTransform>();
        outerNode.AddComponent<UIMaterialComponent>()!.ClipToBounds = true;
        SceneNode innerNode = new(outerNode) { Name = "Inner clip" };
        UIBoundableTransform inner = innerNode.SetTransform<UIBoundableTransform>();
        innerNode.AddComponent<UIMaterialComponent>()!.ClipToBounds = true;
        SceneNode contentNode = new(innerNode) { Name = "Clipped content" };
        UIBoundableTransform content = contentNode.SetTransform<UIBoundableTransform>();
        try
        {
            root.IsActiveSelf = true;
            canvas.ApplyCalculatedLayout(new UILayoutNodeOutput { ActualSize = new Vector2(100) }, Vector2.Zero);
            outer.ApplyCalculatedLayout(new UILayoutNodeOutput
            {
                ActualSize = new Vector2(60, 50), LocalTranslation = new Vector2(10, 20)
            }, Vector2.Zero);
            inner.ApplyCalculatedLayout(new UILayoutNodeOutput
            {
                ActualSize = new Vector2(40), LocalTranslation = new Vector2(30, 15)
            }, Vector2.Zero);
            content.ApplyCalculatedLayout(new UILayoutNodeOutput
            {
                ActualSize = new Vector2(30), LocalTranslation = new Vector2(5)
            }, Vector2.Zero);
            outer.RefreshAxisAlignedRegion();
            inner.RefreshAxisAlignedRegion();
            content.RefreshAxisAlignedRegion();

            BoundingRectangle? resolved = UIClipRegion.ResolveCrop(content, false);
            resolved.ShouldNotBeNull();
            BoundingRectangle canvasClip = resolved.Value;
            canvasClip.ShouldBe(new BoundingRectangle(40, 35, 30, 35));
            BoundingRectangle rasterClip = UIBatchCollector.MapCanvasCropToRaster(canvasClip,
                new BoundingRectangleF(0, 0, 100, 100), new BoundingRectangle(200, 300, 200, 200));
            rasterClip.ShouldBe(new BoundingRectangle(280, 370, 60, 70));
            Rect2D vkClip = VulkanStateTracker.CreateVulkanScissor(rasterClip, new Extent2D(800, 600));
            vkClip.Offset.X.ShouldBe(280);
            vkClip.Offset.Y.ShouldBe(160);
            vkClip.Extent.Width.ShouldBe(60u);
            vkClip.Extent.Height.ShouldBe(70u);
        }
        finally { root.Destroy(true); }
    }

    [TestCase(-10, 20, 0, 10)]
    [TestCase(90, 20, 90, 10)]
    [TestCase(-20, 10, 0, 0)]
    [TestCase(110, 10, 100, 0)]
    public void ScissorClampsOriginalHorizontalIntersection(int x, int width, int expectedX, uint expectedWidth)
    {
        Rect2D result = VulkanStateTracker.CreateVulkanScissor(
            new BoundingRectangle(x, 30, width, 20), new Extent2D(100, 100));

        result.Offset.X.ShouldBe(expectedX);
        result.Extent.Width.ShouldBe(expectedWidth);
        result.Offset.Y.ShouldBe(50);
        result.Extent.Height.ShouldBe(20u);
    }

    [TestCase(-10, 20, 90, 10)]
    [TestCase(90, 20, 0, 10)]
    [TestCase(-20, 10, 100, 0)]
    [TestCase(110, 10, 0, 0)]
    public void ScissorClampsOriginalVerticalIntersection(int y, int height, int expectedY, uint expectedHeight)
    {
        Rect2D result = VulkanStateTracker.CreateVulkanScissor(
            new BoundingRectangle(30, y, 20, height), new Extent2D(100, 100));

        result.Offset.Y.ShouldBe(expectedY);
        result.Extent.Height.ShouldBe(expectedHeight);
        result.Offset.X.ShouldBe(30);
        result.Extent.Width.ShouldBe(20u);
    }
}
