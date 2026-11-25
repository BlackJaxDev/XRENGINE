using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Data.Geometry;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class UISplitterHandleTests
{
    private UITestShaderScope _shaderScope = null!;

    [SetUp]
    public void SetUp() => _shaderScope = new UITestShaderScope();

    [TearDown]
    public void TearDown() => _shaderScope.Dispose();

    [Test]
    public void AcceptedDividerIgnoresChildSizeLimits()
    {
        UILayoutNodeInput[] nodes =
        [
            default(UILayoutNodeInput) with
            {
                Operator = UILayoutOperator.DualSplit, Parent = -1,
                FirstChild = 0, ChildCount = 2, Width = 200, Height = 80,
                MaxAnchor = Vector2.Zero,
                OperatorValues = new Vector4(0.5f, 80, 5, 0),
                OperatorFlags = 2
            },
            default(UILayoutNodeInput) with
            {
                Operator = UILayoutOperator.Boundable, Parent = 0,
                MinWidth = 150, MaxAnchor = Vector2.One
            },
            default(UILayoutNodeInput) with
            {
                Operator = UILayoutOperator.Boundable, Parent = 0,
                MaxAnchor = Vector2.One
            }
        ];
        UILayoutChildInput[] children = [new(1, 0, 0, 1, 1), new(2, 0, 0, 1, 1)];

        UILayoutNodeOutput[] result = UILayoutValueKernel.Run(nodes, children,
            new BoundingRectangleF(0, 0, 200, 80));

        result[0].AcceptedSplitExtent.ShouldBe(200);
        result[0].AcceptedSplitFirstSize.ShouldBe(80);
        result[0].AcceptedSplitSecondSize.ShouldBe(120);
        result[0].AcceptedSplitFixedSide.ShouldBe(1);
        result[1].ActualSize.X.ShouldBe(150);
    }

    [Test]
    public void DragUsesPressedDividerAndStopsAfterTargetReplacement()
    {
        SceneNode root = CreateHandle(out UIDualSplitTransform split,
            out UISplitterHandleComponent handle);
        try
        {
            UILayoutNodeOutput accepted = new()
            {
                AcceptedSplitExtent = 200,
                AcceptedSplitFirstSize = 80,
                AcceptedSplitSecondSize = 120,
                AcceptedSplitFixedSide = 1
            };
            handle.TryCapturePointerGeometry(accepted, out UISplitterPointerMetrics metrics)
                .ShouldBeTrue();
            UIPointerGeometry geometry = new(new Vector2(10, 80), Splitter: metrics);

            handle.PointerPressed(new Vector2(5, 5), geometry);
            handle.IsDragging.ShouldBeTrue();
            split.Width = 300;
            handle.MouseMoved(new Vector2(5, 5), new Vector2(45, 5), geometry);
            split.FixedSize.ShouldBe(120);

            SceneNode replacementNode = new(root) { Name = "Replacement split" };
            UIDualSplitTransform replacement = replacementNode.SetTransform<UIDualSplitTransform>();
            replacement.FirstFixedSize = true;
            replacement.FixedSize = 90;
            handle.SplitTarget = replacement;
            handle.MouseMoved(new Vector2(45, 5), new Vector2(75, 5), geometry);
            handle.PointerReleased(new Vector2(75, 5), true, geometry);
            handle.IsDragging.ShouldBeFalse();
            split.FixedSize.ShouldBe(120);
            replacement.FixedSize.ShouldBe(90);
        }
        finally
        {
            root.Destroy(true);
        }
    }

    [Test]
    public void KeyboardAdjustmentObeysBothPaneBoundsAndRecycleCancelsDrag()
    {
        SceneNode root = CreateHandle(out UIDualSplitTransform split,
            out UISplitterHandleComponent handle);
        try
        {
            handle.MinimumSize = 64;
            handle.MaximumSize = 180;
            handle.MinimumOtherSize = 100;
            handle.KeyboardStep = 32;
            UICanvasTransform canvas = (UICanvasTransform)root.Transform;
            canvas.UpdateLayoutAsync();
            SpinWait.SpinUntil(() => canvas.PumpLayoutResults() && split.ActualWidth == 200,
                TimeSpan.FromSeconds(5)).ShouldBeTrue();

            handle.TryConsumeFocusDirection(Vector2.UnitX).ShouldBeTrue();
            split.FixedSize.ShouldBe(100);
            handle.TryConsumeFocusDirection(-Vector2.UnitX).ShouldBeTrue();
            split.FixedSize.ShouldBe(68);

            UILayoutNodeOutput accepted = new()
            {
                AcceptedSplitExtent = 200,
                AcceptedSplitFirstSize = 68,
                AcceptedSplitSecondSize = 132,
                AcceptedSplitFixedSide = 1
            };
            handle.TryCapturePointerGeometry(accepted, out UISplitterPointerMetrics metrics)
                .ShouldBeTrue();
            UIPointerGeometry geometry = new(new Vector2(10, 80), Splitter: metrics);
            handle.PointerPressed(new Vector2(5, 5), geometry);
            handle.MouseMoved(new Vector2(5, 5), new Vector2(55, 5), geometry);
            split.FixedSize.ShouldBe(100);
            handle.CancelForRecycle();
            handle.MouseMoved(new Vector2(5, 5), new Vector2(75, 5), geometry);
            handle.IsDragging.ShouldBeFalse();
            handle.SplitTarget.ShouldBeNull();
            split.FixedSize.ShouldBe(68);
        }
        finally
        {
            root.Destroy(true);
        }
    }

    [Test]
    public void VerticalSecondPaneFollowsPointerAndCancelRestoresAcceptedValue()
    {
        SceneNode root = CreateHandle(out UIDualSplitTransform split,
            out UISplitterHandleComponent handle);
        try
        {
            split.VerticalSplit = true;
            split.FirstFixedSize = false;
            split.FixedSize = 50;
            UILayoutNodeOutput accepted = new()
            {
                AcceptedSplitExtent = 80,
                AcceptedSplitFirstSize = 30,
                AcceptedSplitSecondSize = 50,
                AcceptedSplitFixedSide = 2,
                AcceptedSplitVertical = true
            };
            handle.TryCapturePointerGeometry(accepted, out UISplitterPointerMetrics metrics)
                .ShouldBeTrue();
            UIPointerGeometry geometry = new(new Vector2(200, 10), Splitter: metrics);

            handle.PointerPressed(new Vector2(5, 5), geometry);
            handle.MouseMoved(new Vector2(5, 5), new Vector2(5, 15), geometry);
            split.FixedSize.ShouldBe(60);
            handle.PointerCancelled();
            split.FixedSize.ShouldBe(50);
            handle.IsDragging.ShouldBeFalse();
        }
        finally
        {
            root.Destroy(true);
        }
    }

    private static SceneNode CreateHandle(out UIDualSplitTransform split,
        out UISplitterHandleComponent handle)
    {
        UICanvasTransform canvas = new();
        canvas.SetSize(new Vector2(200, 80));
        SceneNode root = new("Split test", canvas) { IsActiveSelf = false };
        root.SetWorldContext(new TestWorldContext());
        root.AddComponent<UICanvasComponent>();
        SceneNode splitNode = new(root) { Name = "Split" };
        split = splitNode.SetTransform<UIDualSplitTransform>();
        split.MinAnchor = Vector2.Zero;
        split.MaxAnchor = Vector2.Zero;
        split.Width = 200;
        split.Height = 80;
        split.FirstFixedSize = true;
        split.FixedSize = 80;
        SceneNode handleNode = new(root) { Name = "Handle" };
        UIBoundableTransform handleBounds = handleNode.SetTransform<UIBoundableTransform>();
        handleBounds.Width = 10;
        handleBounds.Height = 80;
        handle = handleNode.AddComponent<UISplitterHandleComponent>()!;
        handle.SplitTarget = split;
        root.IsActiveSelf = true;
        return root;
    }
}
