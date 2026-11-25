using NUnit.Framework;
using Shouldly;
using XREngine.Data.Geometry;
using XREngine.Rendering.UI;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class UILayoutGridKernelTests
{
    [Test]
    public void ArrangeGrid_UsesAutoSpanAndRemainingProportionalExtent()
    {
        UILayoutNodeInput grid = new UILayoutNodeInput() with
        {
            Operator = UILayoutOperator.Grid,
            ChildCount = 2,
            GridRows = new UILayoutGridTrack[]
            {
                new(ESizingMode.Auto, 0, null, 0, null, 0),
                new(ESizingMode.Proportional, 1, null, 0, null, 0)
            },
            GridColumns = new UILayoutGridTrack[]
            {
                new(ESizingMode.Fixed, 20, null, 0, null, 0),
                new(ESizingMode.Proportional, 1, null, 0, null, 0)
            }
        };
        UILayoutNodeInput[] nodes = [grid, default, default];
        UILayoutChildInput[] children = [new(1, 0, 0, 1, 2), new(2, 1, 1, 1, 1)];
        UILayoutOutputPageBuilder outputs = new(3);
        outputs.Edit(1).DesiredSize = new(50, 20);

        UILayoutGridKernel.ArrangeGrid(grid, new BoundingRectangleF(0, 0, 100, 60),
            UILayoutValuePageView<UILayoutNodeInput>.CopyFrom(nodes), children, outputs, true);

        outputs[1].AllocatedBounds.Width.ShouldBe(100f);
        outputs[1].AllocatedBounds.Height.ShouldBe(20f);
        outputs[1].AllocatedBounds.Y.ShouldBe(40f);
        outputs[2].AllocatedBounds.X.ShouldBe(20f);
        outputs[2].AllocatedBounds.Width.ShouldBe(80f);
        outputs[2].AllocatedBounds.Height.ShouldBe(40f);
    }

    [Test]
    public void ArrangeGrid_RedistributesAfterProportionalMinimum()
    {
        UILayoutNodeInput grid = new UILayoutNodeInput() with
        {
            Operator = UILayoutOperator.Grid,
            ChildCount = 3,
            GridRows = new UILayoutGridTrack[] { new(ESizingMode.Fixed, 10, null, 0, null, 0) },
            GridColumns = new UILayoutGridTrack[]
            {
                new(ESizingMode.Proportional, 1, ESizingMode.Fixed, 50, null, 0),
                new(ESizingMode.Proportional, 1, null, 0, null, 0),
                new(ESizingMode.Proportional, 1, null, 0, null, 0)
            }
        };
        UILayoutNodeInput[] nodes = [grid, default, default, default];
        UILayoutChildInput[] children = [new(1, 0, 0, 1, 1), new(2, 0, 1, 1, 1), new(3, 0, 2, 1, 1)];
        UILayoutOutputPageBuilder outputs = new(4);
        outputs.Edit(0) = default;

        UILayoutGridKernel.ArrangeGrid(grid, new BoundingRectangleF(0, 0, 100, 10),
            UILayoutValuePageView<UILayoutNodeInput>.CopyFrom(nodes), children, outputs, true);

        outputs[1].AllocatedBounds.Width.ShouldBe(50f);
        outputs[2].AllocatedBounds.Width.ShouldBe(25f);
        outputs[3].AllocatedBounds.X.ShouldBe(75f);
    }

    [Test]
    public void ArrangeGrid_RejectsSpanOutsideFrozenTracks()
    {
        UILayoutNodeInput grid = new UILayoutNodeInput() with
        {
            Operator = UILayoutOperator.Grid,
            ChildCount = 1,
            GridRows = new UILayoutGridTrack[] { new(ESizingMode.Fixed, 10, null, 0, null, 0) },
            GridColumns = new UILayoutGridTrack[] { new(ESizingMode.Fixed, 10, null, 0, null, 0) }
        };
        UILayoutNodeInput[] nodes = [grid, default];
        UILayoutChildInput[] children = [new(1, 0, 0, 2, 1)];

        Should.Throw<InvalidOperationException>(() => UILayoutGridKernel.ArrangeGrid(
            grid, new BoundingRectangleF(0, 0, 10, 10),
            UILayoutValuePageView<UILayoutNodeInput>.CopyFrom(nodes), children,
            new UILayoutOutputPageBuilder(2), true));
    }
}
