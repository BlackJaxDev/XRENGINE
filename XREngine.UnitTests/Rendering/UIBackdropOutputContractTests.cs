using NUnit.Framework;
using Shouldly;
using XREngine.Data.Geometry;
using XREngine.Rendering.UI;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class UIBackdropOutputContractTests
{
    [Test]
    public void CompletedOutputMustCoverEveryPixelOfRequestedViewport()
    {
        BoundingRectangle completed = new(100, 50, 400, 300);
        UIBackdropOutputContract.Covers(in completed,
            new BoundingRectangle(100, 50, 400, 300)).ShouldBeTrue();
        UIBackdropOutputContract.Covers(in completed,
            new BoundingRectangle(150, 80, 100, 100)).ShouldBeTrue();
        UIBackdropOutputContract.Covers(in completed,
            new BoundingRectangle(99, 50, 100, 100)).ShouldBeFalse();
        UIBackdropOutputContract.Covers(in completed,
            new BoundingRectangle(100, 50, 401, 300)).ShouldBeFalse();
        UIBackdropOutputContract.Covers(in completed,
            new BoundingRectangle(100, 50, 400, 301)).ShouldBeFalse();
        UIBackdropOutputContract.Covers(in completed,
            new BoundingRectangle(100, 50, 0, 300)).ShouldBeFalse();
    }

    [Test]
    public void CheckedBackbufferCopyRejectsPartialOrOutOfBoundsExtents()
    {
        UIBackdropOutputContract.AllowsBackbufferCopy(
            new BoundingRectangle(120, 80, 320, 240), 640, 480, 320, 240).ShouldBeTrue();
        UIBackdropOutputContract.AllowsBackbufferCopy(
            new BoundingRectangle(320, 240, 320, 240), 640, 480, 320, 240).ShouldBeTrue();
        UIBackdropOutputContract.AllowsBackbufferCopy(
            new BoundingRectangle(321, 240, 320, 240), 640, 480, 320, 240).ShouldBeFalse();
        UIBackdropOutputContract.AllowsBackbufferCopy(
            new BoundingRectangle(0, -1, 320, 240), 640, 480, 320, 240).ShouldBeFalse();
        UIBackdropOutputContract.AllowsBackbufferCopy(
            new BoundingRectangle(0, 0, int.MaxValue, 240), 640, 480, 320, 240).ShouldBeFalse();
        UIBackdropOutputContract.AllowsBackbufferCopy(
            new BoundingRectangle(0, 0, 320, 240), 640, 480, 0, 240).ShouldBeFalse();
    }
}
