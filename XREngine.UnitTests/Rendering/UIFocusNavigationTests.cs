using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Geometry;
using XREngine.Rendering.Info;
using XREngine.Rendering.UI;
using XREngine.Rendering.UI.State;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class UIFocusNavigationTests
{
    [Test]
    public void ModalScopeKeepsTabAndDirectionalNavigationInsideItsIdentity()
    {
        using RenderInfo2D origin = RenderInfo2D.New(null!);
        using RenderInfo2D outside = RenderInfo2D.New(null!);
        using RenderInfo2D inside = RenderInfo2D.New(null!);
        UIElementIdentity scope = new(10, 4);
        UIHitRecord[] records =
        [
            Record(origin, 0, Vector2.Zero) with { FocusScopeIdentity = scope },
            Record(outside, 1, new Vector2(12, 0)) with { FocusScopeIdentity = new(10, 3) },
            Record(inside, 2, new Vector2(20, 0)) with { FocusScopeIdentity = scope },
        ];
        UIFocusNavigation.FindTabTarget(records, origin.MembershipId, false, scope).ShouldBe(2);
        UIFocusNavigation.FindTabTarget(records, inside.MembershipId, false, scope).ShouldBe(0);
        UIFocusNavigation.FindDirectionalTarget(records, origin.MembershipId, Vector2.UnitX, scope).ShouldBe(2);
    }

    [Test]
    public void TabOrder_UsesAuthoredIndexAndWrapsInBothDirections()
    {
        using RenderInfo2D first = RenderInfo2D.New(null!);
        using RenderInfo2D second = RenderInfo2D.New(null!);
        using RenderInfo2D excluded = RenderInfo2D.New(null!);
        UIHitRecord[] records =
        [
            Record(first, 0, Vector2.Zero) with { TabIndex = 2 },
            Record(second, 1, Vector2.UnitX) with { TabIndex = 1 },
            Record(excluded, 2, Vector2.UnitY) with { IsTabStop = false },
        ];
        UIFocusNavigation.FindTabTarget(records, 0, false).ShouldBe(1);
        UIFocusNavigation.FindTabTarget(records, second.MembershipId, false).ShouldBe(0);
        UIFocusNavigation.FindTabTarget(records, first.MembershipId, false).ShouldBe(1);
        UIFocusNavigation.FindTabTarget(records, second.MembershipId, true).ShouldBe(0);
    }

    [Test]
    public void Navigation_ExcludesFullyClippedTargetsButKeepsVisibleEdges()
    {
        using RenderInfo2D hidden = RenderInfo2D.New(null!);
        using RenderInfo2D visible = RenderInfo2D.New(null!);
        UIHitRecord[] records =
        [
            Record(hidden, 0, Vector2.Zero) with { Clip = new BoundingRectangle(20, 20, 10, 10) },
            Record(visible, 1, Vector2.Zero) with { Clip = new BoundingRectangle(5, 5, 10, 10) },
        ];

        UIFocusNavigation.FindTabTarget(records, 0, false).ShouldBe(1);
        UIFocusNavigation.FindTabTarget(records, visible.MembershipId, true).ShouldBe(1);
    }

    [Test]
    public void DirectionalNavigation_UsesFrozenGeometryAndRejectsOppositeHalfPlane()
    {
        using RenderInfo2D origin = RenderInfo2D.New(null!);
        using RenderInfo2D right = RenderInfo2D.New(null!);
        using RenderInfo2D diagonal = RenderInfo2D.New(null!);
        using RenderInfo2D left = RenderInfo2D.New(null!);
        UIHitRecord[] records =
        [
            Record(origin, 0, Vector2.Zero), Record(right, 1, new Vector2(20, 0)),
            Record(diagonal, 2, new Vector2(10, 30)), Record(left, 3, new Vector2(-10, 0)),
        ];
        UIFocusNavigation.FindDirectionalTarget(records, origin.MembershipId, Vector2.UnitX).ShouldBe(1);
        UIFocusNavigation.FindDirectionalTarget(records, origin.MembershipId, -Vector2.UnitX).ShouldBe(3);
        UIFocusNavigation.FindDirectionalTarget(records, origin.MembershipId, Vector2.Zero).ShouldBe(-1);
    }

    private static UIHitRecord Record(RenderInfo2D info, int index, Vector2 position)
        => new(info, info.MembershipId, new UIElementIdentity(index + 1, 1), 0,
            Matrix4x4.CreateTranslation(-position.X, -position.Y, 0), new Vector2(10),
            new BoundingRectangleF(position, new Vector2(10)), null,
            index, -1, index + 1, 0, 0, index, true, false);
}
