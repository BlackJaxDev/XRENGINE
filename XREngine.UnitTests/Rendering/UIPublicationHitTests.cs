using System.Numerics;
using System.Collections.Generic;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Geometry;
using XREngine.Rendering.Info;
using XREngine.Rendering.UI;
using XREngine.Rendering.UI.State;
using XREngine.Components;
using XREngine.Scene;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class UIPublicationHitTests
{
    [Test]
    public void Contains_UsesFrozenLocalTransformAndClip()
    {
        using RenderInfo2D renderInfo = RenderInfo2D.New(null!);
        UIHitRecord record = new(
            renderInfo, renderInfo.MembershipId, new UIElementIdentity(1, 1), 1,
            Matrix4x4.CreateTranslation(-10, -20, 0), new Vector2(20, 10),
            new BoundingRectangleF(new Vector2(10, 20), new Vector2(20, 10)),
            BoundingRectangle.FromMinMaxSides(12, 25, 20, 30, 0, 0),
            0, -1, 1, 0, 0, 0, true, false);

        record.Contains(new Vector2(15, 25)).ShouldBeTrue();
        record.Contains(new Vector2(11, 25)).ShouldBeFalse();
        record.Contains(new Vector2(26, 25)).ShouldBeFalse();
        record.Contains(new Vector2(float.NaN, 25)).ShouldBeFalse();
    }

    [Test]
    public void SubtreeMembership_UsesFrozenLayoutIndices()
    {
        using RenderInfo2D renderInfo = RenderInfo2D.New(null!);
        UIHitRecord ancestor = new(renderInfo, renderInfo.MembershipId,
            new UIElementIdentity(1, 1), 0, Matrix4x4.Identity, Vector2.One,
            new BoundingRectangleF(Vector2.Zero, Vector2.One), null,
            2, 0, 7, 0, 0, 0, false, true);
        UIHitRecord descendant = ancestor with { NodeIndex = 6, ParentIndex = 4 };
        UIHitRecord followingSibling = ancestor with { NodeIndex = 7, ParentIndex = 0 };

        descendant.IsInSubtreeOf(ancestor).ShouldBeTrue();
        followingSibling.IsInSubtreeOf(ancestor).ShouldBeFalse();
    }

    [Test]
    public void OutputPackets_MapTheirOwnFrameWithoutChangingPreviousFrame()
    {
        using RenderInfo2D renderInfo = RenderInfo2D.New(null!);
        UIHitRecord firstHit = new(renderInfo, renderInfo.MembershipId,
            new UIElementIdentity(1, 1), 0, Matrix4x4.Identity, new Vector2(100, 50),
            new BoundingRectangleF(Vector2.Zero, new Vector2(100, 50)), null,
            0, -1, 1, 0, 0, 0, true, false);
        UIPublication frameA = CreatePacket(1, 1, [firstHit]).WithOutputMapping(
            Matrix4x4.Identity, Matrix4x4.Identity, ECanvasDrawSpace.Screen,
            new BoundingRectangleF(Vector2.Zero, new Vector2(100, 50)),
            Matrix4x4.Identity, Matrix4x4.Identity, 0, 1);
        UIHitRecord secondHit = firstHit with
        {
            CanvasToLocal = Matrix4x4.CreateTranslation(-20, 0, 0),
            Bounds = new BoundingRectangleF(new Vector2(20, 0), new Vector2(100, 50))
        };
        UIPublication frameB = CreatePacket(2, 2, [secondHit]).WithOutputMapping(
            Matrix4x4.Identity, Matrix4x4.CreateScale(0.5f, 0.5f, 1),
            ECanvasDrawSpace.Screen,
            new BoundingRectangleF(Vector2.Zero, new Vector2(200, 100)),
            Matrix4x4.Identity, Matrix4x4.Identity, 0, 1);

        frameA.TryMapPointer(new Vector2(40, 20), out Vector2 pointA).ShouldBeTrue();
        frameB.TryMapPointer(new Vector2(40, 20), out Vector2 pointB).ShouldBeTrue();
        pointA.ShouldBe(new Vector2(40, 20));
        pointB.ShouldBe(new Vector2(20, 10));
        frameA.Hits[0].Contains(pointA).ShouldBeTrue();
        frameB.Hits[0].Contains(pointB).ShouldBeTrue();
        frameA.Generation.ShouldBe(1UL);
        frameB.Generation.ShouldBe(2UL);
    }

    [Test]
    public void WorldPacket_MapsThroughFrozenCameraRayAndRejectsOutsideOutput()
    {
        UIPublication packet = CreatePacket(3, 1, []).WithOutputMapping(
            Matrix4x4.Identity, Matrix4x4.Identity, ECanvasDrawSpace.World,
            new BoundingRectangleF(new Vector2(10, 20), new Vector2(200, 100)),
            Matrix4x4.Identity, Matrix4x4.Identity, -1, 1);

        packet.TryMapPointer(new Vector2(110, 70), out Vector2 center).ShouldBeTrue();
        center.ShouldBe(Vector2.Zero);
        packet.TryMapPointer(new Vector2(9, 70), out _).ShouldBeFalse();
        packet.TryMapPointer(new Vector2(110, 120), out _).ShouldBeFalse();
        packet.TryMapPointer(new Vector2(9, 70), out Vector2 dragged,
            requireInsideOutput: false).ShouldBeTrue();
        float.IsFinite(dragged.X).ShouldBeTrue();
    }

    private static UIPublication CreatePacket(ulong generation, ulong hitGeneration, UIHitRecord[] hits)
        => new(generation, 1, 1, hitGeneration, 1,
            ReadOnlyMemory<UILayoutNodeOutput>.Empty, ReadOnlyMemory<UIElementIdentity>.Empty,
            hits, [], new BoundingRectangleF(Vector2.Zero, new Vector2(100, 50)),
            Matrix4x4.Identity, Matrix4x4.Identity);

    [Test]
    public void ScenePainterOrderChangesOnlyWhenMembershipChanges()
    {
        VisualScene2D scene = new();
        using RenderInfo2D first = RenderInfo2D.New(null!);
        using RenderInfo2D second = RenderInfo2D.New(null!);
        List<long> order = [];
        ulong revision = ulong.MaxValue;
        try
        {
            scene.AddRenderable(first);
            scene.AddRenderable(second);
            scene.TryCopyRenderableMembershipOrder(ref revision, order).ShouldBeTrue();
            order.Count.ShouldBe(2);
            order[0].ShouldBe(first.MembershipId);
            order[1].ShouldBe(second.MembershipId);
            scene.TryCopyRenderableMembershipOrder(ref revision, order).ShouldBeFalse();

            scene.RemoveRenderable(first);
            scene.TryCopyRenderableMembershipOrder(ref revision, order).ShouldBeTrue();
            order.Count.ShouldBe(1);
            order[0].ShouldBe(second.MembershipId);
        }
        finally
        {
            scene.Destroy();
        }
    }
}
