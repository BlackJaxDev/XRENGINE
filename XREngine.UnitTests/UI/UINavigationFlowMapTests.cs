using System.Collections.Generic;
using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.UnitTests.UI;

[TestFixture]
public sealed class UINavigationFlowMapTests
{
    private static UIButtonComponent CreateButtonNode(SceneNode parent, string name, float x, float y, float width = 200.0f, float height = 44.0f)
    {
        SceneNode node = new(parent, name);
        UIBoundableTransform transform = node.SetTransform<UIBoundableTransform>();
        transform.MinAnchor = Vector2.Zero;
        transform.MaxAnchor = Vector2.Zero;
        transform.NormalizedPivot = Vector2.Zero;
        transform.Translation = new Vector2(x, y);
        transform.Width = width;
        transform.Height = height;
        transform.Arrange(new BoundingRectangleF(0.0f, 0.0f, 1000.0f, 1000.0f));
        transform.RecalculateMatrices(forceWorldRecalc: true);
        return node.AddComponent<UIButtonComponent>()!;
    }

    [Test]
    public void FlowMap_ConnectVerticalChain_NavigatesWrapsAndBlocksHorizontal()
    {
        SceneNode root = new("PausePanel");
        root.SetTransform<UIBoundableTransform>();

        UIButtonComponent resume = CreateButtonNode(root, "Resume", 0.0f, 120.0f);
        UIButtonComponent close = CreateButtonNode(root, "Close", 0.0f, 60.0f);
        root.OnActivated();

        UINavigationFlowMap flowMap = new()
        {
            AllowedDirections = EUINavigationAllowedDirections.Vertical,
            AllowSpatialFallback = false,
        };
        flowMap.ConnectVerticalChain(wrap: true, resume, close);

        flowMap.ResolveInitialFocus().ShouldBeSameAs(resume);
        flowMap.ResolveNext(null, EUINavigationDirection.Down).ShouldBeSameAs(resume);
        flowMap.ResolveNext(resume, EUINavigationDirection.Down).ShouldBeSameAs(close);
        flowMap.ResolveNext(close, EUINavigationDirection.Down).ShouldBeSameAs(resume);
        flowMap.ResolveNext(resume, EUINavigationDirection.Up).ShouldBeSameAs(close);
        flowMap.ResolveNext(close, EUINavigationDirection.Up).ShouldBeSameAs(resume);

        flowMap.ResolveNext(resume, EUINavigationDirection.Left).ShouldBeNull();
        flowMap.ResolveNext(resume, EUINavigationDirection.Right).ShouldBeNull();
    }

    [Test]
    public void FlowMap_ExplicitBlockedAndBidirectionalEdges_RespectPrecedenceAndSkipHiddenTargets()
    {
        SceneNode root = new("GridMenu");
        root.SetTransform<UIBoundableTransform>();

        UIButtonComponent topLeft = CreateButtonNode(root, "TopLeft", 0.0f, 120.0f);
        UIButtonComponent topRight = CreateButtonNode(root, "TopRight", 240.0f, 120.0f);
        UIButtonComponent midLeft = CreateButtonNode(root, "MidLeft", 0.0f, 60.0f);
        UIButtonComponent bottomLeft = CreateButtonNode(root, "BottomLeft", 0.0f, 0.0f);
        root.OnActivated();
        List<UIInteractableComponent> candidates = [topLeft, topRight, midLeft, bottomLeft];

        UINavigationFlowMap flowMap = new()
        {
            AllowedDirections = EUINavigationAllowedDirections.Cardinal,
            AllowSpatialFallback = true,
        };
        flowMap.SetEdge(topLeft, EUINavigationDirection.Down, midLeft, EUINavigationRule.Explicit, bidirectional: true);
        flowMap.SetEdge(midLeft, EUINavigationDirection.Down, bottomLeft, EUINavigationRule.Explicit, bidirectional: true);
        flowMap.BlockDirection(topLeft, EUINavigationDirection.Right);

        // Blocked edge overrides spatial candidate sitting directly to the right.
        flowMap.ResolveNext(topLeft, EUINavigationDirection.Right, candidates).ShouldBeNull();

        // Bidirectional edge resolves upward back to topLeft.
        flowMap.ResolveNext(midLeft, EUINavigationDirection.Up, candidates).ShouldBeSameAs(topLeft);

        // Hiding midLeft skips cleanly along the explicit chain to bottomLeft.
        midLeft.BoundableTransform.Visibility = EVisibility.Collapsed;
        flowMap.ResolveNext(topLeft, EUINavigationDirection.Down, candidates).ShouldBeSameAs(bottomLeft);
    }

    [Test]
    public void FlowMap_AutoSpatialFallbackAndAxisWrap_SelectsAlignedNeighborAndWrapsOppositeEnd()
    {
        SceneNode root = new("SpatialMenu");
        root.SetTransform<UIBoundableTransform>();

        UIButtonComponent top = CreateButtonNode(root, "Top", 100.0f, 200.0f);
        UIButtonComponent middle = CreateButtonNode(root, "Middle", 100.0f, 120.0f);
        UIButtonComponent bottom = CreateButtonNode(root, "Bottom", 100.0f, 40.0f);
        root.OnActivated();
        List<UIInteractableComponent> candidates = [top, middle, bottom];

        UINavigationFlowMap flowMap = new()
        {
            AllowedDirections = EUINavigationAllowedDirections.Vertical,
            AllowSpatialFallback = true,
            WrapVertical = true,
        };

        flowMap.ResolveNext(top, EUINavigationDirection.Down, candidates).ShouldBeSameAs(middle);
        flowMap.ResolveNext(middle, EUINavigationDirection.Down, candidates).ShouldBeSameAs(bottom);
        flowMap.ResolveNext(bottom, EUINavigationDirection.Down, candidates).ShouldBeSameAs(top);
        flowMap.ResolveNext(top, EUINavigationDirection.Up, candidates).ShouldBeSameAs(bottom);
    }

    [Test]
    public void NavigationRepeater_StickFlickHysteresisAndHoldRepeat_EmitPredictably()
    {
        UINavigationRepeater repeater = new();
        UINavigationRepeatSettings settings = new()
        {
            StickEngageThreshold = 0.55f,
            StickReleaseThreshold = 0.30f,
            AllowHoldRepeat = true,
            InitialRepeatDelaySeconds = 0.40f,
            RepeatIntervalSeconds = 0.15f,
            AllowDiagonalStick = false,
        };

        // Deflection below engage threshold does not trigger.
        repeater.SetStick(0.0f, -0.45f);
        repeater.EvaluateImmediate(EUINavigationAllowedDirections.Vertical, in settings)
            .ShouldBe(EUINavigationDirection.None);

        // Flick past engage threshold triggers immediately.
        repeater.SetStick(0.25f, -0.75f);
        repeater.EvaluateImmediate(EUINavigationAllowedDirections.Vertical, in settings)
            .ShouldBe(EUINavigationDirection.Down);

        // Staying above release threshold keeps direction held and fires after initial delay.
        repeater.SetStick(0.0f, -0.40f);
        repeater.Tick(0.20f, EUINavigationAllowedDirections.Vertical, in settings)
            .ShouldBe(EUINavigationDirection.None);
        repeater.Tick(0.21f, EUINavigationAllowedDirections.Vertical, in settings)
            .ShouldBe(EUINavigationDirection.Down);

        // Subsequent hold repeat uses RepeatIntervalSeconds.
        repeater.Tick(0.10f, EUINavigationAllowedDirections.Vertical, in settings)
            .ShouldBe(EUINavigationDirection.None);
        repeater.Tick(0.06f, EUINavigationAllowedDirections.Vertical, in settings)
            .ShouldBe(EUINavigationDirection.Down);

        // Dropping below release threshold disarms so a new flick triggers immediately.
        repeater.SetStick(0.0f, -0.15f);
        repeater.EvaluateImmediate(EUINavigationAllowedDirections.Vertical, in settings)
            .ShouldBe(EUINavigationDirection.None);

        repeater.SetStick(0.0f, 0.70f);
        repeater.EvaluateImmediate(EUINavigationAllowedDirections.Vertical, in settings)
            .ShouldBe(EUINavigationDirection.Up);

        // Pure horizontal flick on a vertical-only flow map produces no vertical step.
        repeater.SetStick(0.90f, -0.10f);
        repeater.EvaluateImmediate(EUINavigationAllowedDirections.Vertical, in settings)
            .ShouldBe(EUINavigationDirection.None);
    }

    [Test]
    public void NavigationRepeater_DPadPriorityAndDisabledHoldRepeat_BehaveAsConfigured()
    {
        UINavigationRepeater repeater = new();
        UINavigationRepeatSettings noRepeat = UINavigationRepeatSettings.Default;
        noRepeat.AllowHoldRepeat = false;

        // Stick pushes down, D-pad pushes up -> D-pad takes priority.
        repeater.SetStick(0.0f, -0.90f);
        repeater.SetDPadState(EUINavigationDirection.Up, true);
        repeater.EvaluateImmediate(EUINavigationAllowedDirections.Cardinal, in noRepeat)
            .ShouldBe(EUINavigationDirection.Up);

        // Holding does not repeat when AllowHoldRepeat is false.
        repeater.Tick(1.0f, EUINavigationAllowedDirections.Cardinal, in noRepeat)
            .ShouldBe(EUINavigationDirection.None);

        // Releasing D-pad lets the held stick take over cleanly.
        repeater.SetDPadState(EUINavigationDirection.Up, false);
        repeater.EvaluateImmediate(EUINavigationAllowedDirections.Cardinal, in noRepeat)
            .ShouldBe(EUINavigationDirection.Down);
    }

    [Test]
    public void UIButtonComponent_GamepadSelectedAndMouseHover_CoordinateHighlightCleanly()
    {
        SceneNode root = new("ButtonRoot");
        root.SetTransform<UIBoundableTransform>();
        UIButtonComponent button = CreateButtonNode(root, "ActionButton", 0.0f, 0.0f);
        root.OnActivated();
        button.Style = new UIStyle
        {
            Base = new UIStyleProperties { BackgroundColor = ColorF4.Gray },
            Hover = new UIStyleProperties { BackgroundColor = ColorF4.Blue },
            Focused = new UIStyleProperties { BackgroundColor = ColorF4.White, BorderWidth = 2.0f },
            Pressed = new UIStyleProperties { BackgroundColor = ColorF4.DarkGray },
        };

        button.IsHighlighted.ShouldBeFalse();

        button.IsGamepadSelected = true;
        button.IsHighlighted.ShouldBeTrue();

        button.IsMouseDirectlyOver = true;
        button.IsGamepadSelected = false;
        button.IsHighlighted.ShouldBeTrue();

        button.IsMouseDirectlyOver = false;
        button.IsHighlighted.ShouldBeFalse();
    }
}
