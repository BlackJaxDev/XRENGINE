using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using XREngine.Components;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Runtime.InputIntegration;
using XREngine.Scene;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
[NonParallelizable]
public sealed class UIPresentedScrollGeometryTests
{
    [Test]
    public void WheelUsesReceiverHitFrozenInverseAndMetadata()
    {
        using UITestShaderScope shaders = new();
        SceneNode root = new("Scroll canvas", new UICanvasTransform());
        root.SetWorldContext(new TestWorldContext());
        XRViewport viewport = new(null)
        {
            AutomaticallyCollectVisible = false,
            AutomaticallySwapBuffers = false,
            SetRenderPipelineFromCamera = false
        };
        UIPublication? publication = null;
        try
        {
            UICanvasComponent canvas = root.AddComponent<UICanvasComponent>()!;
            UICanvasInputComponent input = root.AddComponent<UICanvasInputComponent>()!;
            PawnComponent pawn = root.NewChild("Pawn").AddComponent<PawnComponent>()!;
            pawn.Controller = new LocalPlayerController { Viewport = viewport };
            input.OwningPawn = pawn;
            SceneNode target = root.NewChild("Plot");
            target.SetTransform<UIBoundableTransform>();
            UIPresentedScrollProbe probe = target.AddComponent<UIPresentedScrollProbe>()!;
            object marker = new();
            BoundingRectangleF canvasBounds = new(Vector2.Zero, new Vector2(200));
            BoundingRectangleF hitBounds = new(new Vector2(20, 30),
                new Vector2(100, 80));
            UIHitRecord hit = new(probe.RenderInfo2D,
                probe.RenderInfo2D.MembershipId, probe.UITransform.ElementIdentity,
                probe.InteractionRevision,
                Matrix4x4.CreateTranslation(-20, -30, 0),
                new Vector2(100, 80), hitBounds, null,
                1, 0, 2, 1, 0, 0, true, false,
                PointerGeometry: new UIPointerGeometry(new Vector2(100, 80),
                    Metadata: marker));
            UIScrollRecord route = new(probe, probe,
                probe.UITransform.ElementIdentity, 1, 2, 1, 0);
            publication = new UIPublication(1, canvas.PublicationLifetime,
                1, 1, 1, default, default, [hit], [], canvasBounds,
                Matrix4x4.Identity, Matrix4x4.Identity,
                scrollReceivers: [route]);
            canvas.MarkPresented(viewport, publication);
            publication.Release();
            publication = null;
            typeof(UICanvasInputComponent).GetProperty(
                nameof(UICanvasInputComponent.TopMostElement))!
                .SetValue(input, probe);
            typeof(UICanvasInputComponent).GetField("_cursorOutputPosition",
                BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(input, new Vector2(40, 50));
            target.GetTransformAs<UIBoundableTransform>(true)!.Translation =
                new Vector2(90, 90);
            typeof(UICanvasInputComponent).GetMethod("OnMouseScroll",
                BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(input, [1f]);
            Assert.That(probe.GeometryCalls, Is.EqualTo(1));
            Assert.That(probe.ScalarCalls, Is.Zero);
            Assert.That(probe.LastLocalPoint, Is.EqualTo(new Vector2(20, 20)));
            Assert.That(probe.LastMetadata, Is.SameAs(marker));

            publication = new UIPublication(2, canvas.PublicationLifetime,
                1, 2, 2, default, default,
                [hit with { PointerGeometry = default }], [], canvasBounds,
                Matrix4x4.Identity, Matrix4x4.Identity,
                scrollReceivers: [route]);
            canvas.MarkPresented(viewport, publication);
            publication.Release();
            publication = null;
            typeof(UICanvasInputComponent).GetMethod("OnMouseScroll",
                BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(input, [1f]);
            Assert.That(probe.GeometryCalls, Is.EqualTo(2));
            Assert.That(probe.ScalarCalls, Is.Zero);
            Assert.That(probe.LastMetadata, Is.Null);
        }
        finally
        {
            publication?.Release();
            root.Destroy(true);
        }
    }

    [Test]
    public void UnhandledWheelStopsOldAncestryAfterOutputPacketSwap()
    {
        using UITestShaderScope shaders = new();
        SceneNode root = new("Scroll canvas", new UICanvasTransform());
        root.SetWorldContext(new TestWorldContext());
        XRViewport viewport = new(null)
        {
            AutomaticallyCollectVisible = false,
            AutomaticallySwapBuffers = false,
            SetRenderPipelineFromCamera = false
        };
        UIPublication? publication = null;
        UIPublication? replacement = null;
        try
        {
            UICanvasComponent canvas = root.AddComponent<UICanvasComponent>()!;
            UICanvasInputComponent input = root.AddComponent<UICanvasInputComponent>()!;
            PawnComponent pawn = root.NewChild("Pawn").AddComponent<PawnComponent>()!;
            pawn.Controller = new LocalPlayerController { Viewport = viewport };
            input.OwningPawn = pawn;
            SceneNode parent = root.NewChild("Parent");
            parent.SetTransform<UIBoundableTransform>();
            UIPresentedScrollProbe ancestor =
                parent.AddComponent<UIPresentedScrollProbe>()!;
            SceneNode child = parent.NewChild("Child");
            child.SetTransform<UIBoundableTransform>();
            UIPresentedScrollProbe target =
                child.AddComponent<UIPresentedScrollProbe>()!;
            BoundingRectangleF bounds = new(Vector2.Zero, new Vector2(200));
            UIHitRecord parentHit = new(ancestor.RenderInfo2D,
                ancestor.RenderInfo2D.MembershipId,
                ancestor.UITransform.ElementIdentity,
                ancestor.InteractionRevision, Matrix4x4.Identity,
                new Vector2(200), bounds, null, 1, 0, 3, 1, 0, 0,
                true, false,
                PointerGeometry: new UIPointerGeometry(new Vector2(200)));
            UIHitRecord childHit = new(target.RenderInfo2D,
                target.RenderInfo2D.MembershipId,
                target.UITransform.ElementIdentity,
                target.InteractionRevision,
                Matrix4x4.CreateTranslation(-20, -30, 0),
                new Vector2(100, 80),
                new BoundingRectangleF(new Vector2(20, 30),
                    new Vector2(100, 80)), null,
                2, 1, 3, 2, 0, 1, true, false,
                PointerGeometry: new UIPointerGeometry(new Vector2(100, 80)));
            UIScrollRecord parentRoute = new(ancestor, ancestor,
                ancestor.UITransform.ElementIdentity, 1, 3, 1, 0);
            UIScrollRecord childRoute = new(target, target,
                target.UITransform.ElementIdentity, 2, 3, 2, 0);
            publication = new UIPublication(1, canvas.PublicationLifetime,
                1, 1, 1, default, default, [parentHit, childHit], [],
                bounds, Matrix4x4.Identity, Matrix4x4.Identity,
                scrollReceivers: [parentRoute, childRoute]);
            canvas.MarkPresented(viewport, publication);
            publication.Release();
            publication = null;
            typeof(UICanvasInputComponent).GetProperty(
                nameof(UICanvasInputComponent.TopMostElement))!
                .SetValue(input, target);
            typeof(UICanvasInputComponent).GetField("_cursorOutputPosition",
                BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(input, new Vector2(40, 50));
            replacement = new UIPublication(2, canvas.PublicationLifetime,
                2, 2, 2, default, default, [], [], bounds,
                Matrix4x4.Identity, Matrix4x4.Identity);
            target.OnGeometry = () =>
            {
                canvas.MarkPresented(viewport, replacement!);
                return false;
            };
            typeof(UICanvasInputComponent).GetMethod("OnMouseScroll",
                BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(input, [1f]);
            Assert.That(target.GeometryCalls, Is.EqualTo(1));
            Assert.That(ancestor.GeometryCalls, Is.Zero);
            Assert.That(ancestor.ScalarCalls, Is.Zero);
        }
        finally
        {
            publication?.Release();
            replacement?.Release();
            root.Destroy(true);
        }
    }
}
