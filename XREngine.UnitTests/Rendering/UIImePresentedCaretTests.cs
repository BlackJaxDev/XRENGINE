using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Rendering.UI.State;
using XREngine.Runtime.InputIntegration;
using XREngine.Scene;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class UIImePresentedCaretTests
{
    [Test]
    public void CandidateUsesPresentedCaretEvenWhenLaterCaretHasDifferentPosition()
    {
        UIElementIdentity caret = new(17, 3);
        UIPublication presented = CreatePacket(CaretRecord(caret, 41, 10, 20), generation: 1);
        UIPublication pending = CreatePacket(CaretRecord(caret, 41, 70, 20), generation: 2);
        try
        {
            UICanvasInputComponent.TryResolvePresentedImeCaretClientPoint(
                presented, 41, caret, out Vector2 displayed).ShouldBeTrue();
            displayed.ShouldBe(new Vector2(20, 40));

            UICanvasInputComponent.TryResolvePresentedImeCaretClientPoint(
                pending, 41, caret, out Vector2 later).ShouldBeTrue();
            later.ShouldBe(new Vector2(140, 40));
            displayed.ShouldNotBe(later);
        }
        finally
        {
            presented.Release();
            pending.Release();
        }
    }

    [Test]
    public void CandidateRequiresVisibleCaretWithExactMembershipAndIncarnation()
    {
        UIElementIdentity caret = new(17, 3);
        UIPublication presented = CreatePacket(CaretRecord(caret, 41, 10, 20), generation: 1);
        UIPublication hidden = CreatePacket(CaretRecord(caret, 41, 10, 20) with
        {
            Visible = false
        }, generation: 2);
        try
        {
            UICanvasInputComponent.TryResolvePresentedImeCaretClientPoint(
                presented, 42, caret, out _).ShouldBeFalse();
            UICanvasInputComponent.TryResolvePresentedImeCaretClientPoint(
                presented, 41, new UIElementIdentity(17, 4), out _).ShouldBeFalse();
            UICanvasInputComponent.TryResolvePresentedImeCaretClientPoint(
                hidden, 41, caret, out _).ShouldBeFalse();
        }
        finally
        {
            presented.Release();
            hidden.Release();
        }
    }

    [Test]
    public void DeactivationRetiresImeCandidateTickAndNewFocusCanRegisterAgain()
    {
        using UITestShaderScope shaders = new();
        SceneNode root = new("IME lifecycle canvas", new UICanvasTransform());
        root.SetWorldContext(new TestWorldContext());
        XRViewport viewport = new(null)
        {
            AutomaticallyCollectVisible = false,
            AutomaticallySwapBuffers = false,
            SetRenderPipelineFromCamera = false
        };
        UIPublication? packet = null;
        try
        {
            UICanvasComponent canvas = root.AddComponent<UICanvasComponent>()!;
            UICanvasInputComponent dispatcher = root.AddComponent<UICanvasInputComponent>()!;
            PawnComponent pawn = root.NewChild("Pawn").AddComponent<PawnComponent>()!;
            pawn.Controller = new LocalPlayerController { Viewport = viewport };
            dispatcher.OwningPawn = pawn;
            SceneNode field = new(root) { Name = "IME field" };
            field.SetTransform<UIBoundableTransform>();
            field.AddComponent<UITextComponent>();
            UITextInputComponent control = field.AddComponent<UITextInputComponent>()!;
            dispatcher.FocusedComponent = control;

            BoundingRectangleF bounds = new(Vector2.Zero, new Vector2(200));
            UIHitRecord hit = new(control.RenderInfo2D, control.RenderInfo2D.MembershipId,
                control.AccessibilityIdentity, control.InteractionRevision,
                Matrix4x4.Identity, new Vector2(100), bounds, null,
                1, 0, 2, 1, 0, 0, true, false);
            packet = new UIPublication(1, canvas.PublicationLifetime, 1, 1, 1,
                default, default, [hit], [], bounds, Matrix4x4.Identity, Matrix4x4.Identity);
            canvas.MarkPresented(viewport, packet);

            dispatcher.DispatchImeEvents(
            [
                new WindowImeEvent(WindowImeEventKind.Start, "", 0,
                    control.AccessibilityIdentity, control.FocusEpoch)
            ]);
            control.IsComposing.ShouldBeTrue();
            dispatcher.ImeCandidateTickRegistered.ShouldBeTrue();

            dispatcher.IsActive = false;
            control.IsComposing.ShouldBeFalse();
            dispatcher.ImeCandidateTickRegistered.ShouldBeFalse();

            dispatcher.IsActive = true;
            dispatcher.FocusedComponent = control;
            dispatcher.DispatchImeEvents(
            [
                new WindowImeEvent(WindowImeEventKind.Start, "", 0,
                    control.AccessibilityIdentity, control.FocusEpoch)
            ]);
            control.IsComposing.ShouldBeTrue();
            dispatcher.ImeCandidateTickRegistered.ShouldBeTrue();
        }
        finally
        {
            root.Destroy(true);
            packet?.Release();
            viewport.Destroy();
        }
    }

    private static UIRenderRecord CaretRecord(UIElementIdentity identity, long membershipId,
        float x, float y) => new()
    {
        RenderInfo = null!,
        MembershipId = membershipId,
        Identity = identity,
        WorldMatrix = Matrix4x4.CreateTranslation(x, y, 0),
        Bounds = new Vector4(0, 0, 1, 16),
        RenderPass = 0,
        ZIndex = 20,
        Material = null,
        Visible = true
    };

    private static UIPublication CreatePacket(UIRenderRecord caret, ulong generation)
        => new(generation, 1, generation, 1, 1, default, default, [], [caret],
            new BoundingRectangleF(Vector2.Zero, new Vector2(200)),
            Matrix4x4.CreateScale(2), Matrix4x4.CreateScale(0.5f));
}
