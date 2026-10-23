using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Runtime.InputIntegration;
using XREngine.Scene;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class UIPresentedInputTests
{
    private UITestShaderScope _shaderScope = null!;
    [SetUp] public void SetUpShaderAssets() => _shaderScope = new UITestShaderScope();
    [TearDown] public void RestoreShaderAssets() => _shaderScope.Dispose();

    [Test]
    public void FocusCallbacksCannotLeaveOldInputRegistrations()
    {
        var root = new SceneNode("Focus ownership", new UICanvasTransform());
        root.SetWorldContext(new TestWorldContext());
        try
        {
            root.AddComponent<UICanvasComponent>();
            var input = root.AddComponent<UICanvasInputComponent>()!;
            var device = new XREngine.Input.Devices.LocalInputInterface();
            var first = root.NewChild("First").AddComponent<FocusRegistrationProbe>()!;
            var second = root.NewChild("Second").AddComponent<FocusRegistrationProbe>()!;
            var last = root.NewChild("Last").AddComponent<FocusRegistrationProbe>()!;
            first.RegisterInputsOnFocus = second.RegisterInputsOnFocus = last.RegisterInputsOnFocus = true;
            input.RegisterInput(device);
            input.FocusedComponent = first;
            first.Registrations.ShouldBe(1);
            second.GotFocus += _ => input.FocusedComponent = last;

            input.FocusedComponent = second;

            input.FocusedComponent.ShouldBeSameAs(last);
            first.IsFocused.ShouldBeFalse();
            second.IsFocused.ShouldBeFalse();
            last.IsFocused.ShouldBeTrue();
            first.Removals.ShouldBe(1);
            second.Registrations.ShouldBe(0);
            last.Registrations.ShouldBe(1);
            device.Unregister.ShouldBeFalse();

            last.RegisterInputsOnFocus = false;
            last.Removals.ShouldBe(1);
            last.RegisterInputsOnFocus = true;
            last.Registrations.ShouldBe(2);
            last.LostFocus += _ => input.FocusedComponent = first;
            input.IsActive = false;
            first.IsFocused.ShouldBeFalse();
            last.IsFocused.ShouldBeFalse();
            last.Removals.ShouldBe(2);
            input.FocusedComponent.ShouldBeNull();
            device.Unregister.ShouldBeFalse();
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void ContactQuery_UsesPresentedShapeAndRejectsRecycledTarget()
    {
        var root = new SceneNode("Presented input", new UICanvasTransform());
        root.SetWorldContext(new TestWorldContext());
        var viewport = new XRViewport(null)
        {
            AutomaticallyCollectVisible = false,
            AutomaticallySwapBuffers = false,
            SetRenderPipelineFromCamera = false,
        };
        try
        {
            var canvas = root.AddComponent<UICanvasComponent>()!;
            var input = root.AddComponent<UICanvasInputComponent>()!;
            var pawn = root.NewChild("Pawn").AddComponent<PawnComponent>()!;
            var controller = new LocalPlayerController { Viewport = viewport };
            pawn.Controller = controller;
            input.OwningPawn = pawn;
            var button = root.NewChild("Virtual button").AddComponent<UIVirtualButtonComponent>()!;
            var bounds = new BoundingRectangleF(Vector2.Zero, new Vector2(100));
            UIHitRecord hit = new(button.RenderInfo2D, button.RenderInfo2D.MembershipId,
                button.UITransform.ElementIdentity, button.InteractionRevision,
                Matrix4x4.Identity, new Vector2(20), bounds, null,
                1, 0, 2, 1, 0, 0, true, false);
            var packet = new UIPublication(1, 1, 1, 1, 1, default, default, [hit], [],
                bounds, Matrix4x4.Identity, Matrix4x4.Identity);
            canvas.MarkPresented(viewport, packet);

            // The displayed packet remains authoritative while a new layout is pending.
            button.UITransform.Translation = new Vector2(80, 80);
            input.FindVirtualControl(new Vector2(10, 10), out Vector2 local, out bool overflow, out bool hasHit)
                .ShouldBeSameAs(button);
            local.ShouldBe(new Vector2(10, 10));
            overflow.ShouldBeFalse();
            hasHit.ShouldBeTrue();

            button.CancelForRecycle();
            input.FindVirtualControl(new Vector2(10, 10), out _, out _, out _).ShouldBeNull();
        }
        finally
        {
            root.Destroy(true);
            viewport.Destroy();
        }
    }

    [Test]
    public void CanvasReactivation_DoesNotAcceptOldPresentationReceipt()
    {
        var root = new SceneNode("Canvas lifetime", new UICanvasTransform());
        root.SetWorldContext(new TestWorldContext());
        object output = new();
        try
        {
            var canvas = root.AddComponent<UICanvasComponent>()!;
            var bounds = new BoundingRectangleF(Vector2.Zero, new Vector2(100));
            var packet = new UIPublication(1, 1, 1, 1, 1, default, default, [], [],
                bounds, Matrix4x4.Identity, Matrix4x4.Identity);
            canvas.MarkPresented(output, packet);
            canvas.GetPresentedPublication(output).ShouldBeSameAs(packet);

            canvas.IsActive = false;
            canvas.IsActive = true;
            canvas.MarkPresented(output, packet);

            canvas.GetPresentedPublication(output).ShouldBeNull();
        }
        finally { root.Destroy(true); }
    }
}
