using NUnit.Framework;
using Shouldly;
using Silk.NET.Vulkan;
using XREngine.Data.Geometry;
using XREngine.Rendering.Vulkan;
using XREngine.Rendering.UI;

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
