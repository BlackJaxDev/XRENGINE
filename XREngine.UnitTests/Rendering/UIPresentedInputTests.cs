using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Data.Geometry;
using XREngine.Input.Devices;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Rendering.UI.State;
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
        UIPublication? packet = null;
        try
        {
            var canvas = root.AddComponent<UICanvasComponent>()!;
            var bounds = new BoundingRectangleF(Vector2.Zero, new Vector2(100));
            packet = new UIPublication(1, 1, 1, 1, 1, default, default, [], [],
                bounds, Matrix4x4.Identity, Matrix4x4.Identity);
            canvas.MarkPresented(output, packet);
            canvas.TryAcquirePresentedPublication(output, out UIPublication? presented).ShouldBeTrue();
            try { presented.ShouldBeSameAs(packet); }
            finally { presented?.Release(); }

            canvas.IsActive = false;
            canvas.IsActive = true;
            canvas.MarkPresented(output, packet);

            canvas.TryAcquirePresentedPublication(output, out presented).ShouldBeFalse();
            presented.ShouldBeNull();
        }
        finally
        {
            packet?.Release();
            root.Destroy(true);
        }
    }

    [Test]
    public void PresentedReaderKeepsPacketUntilAfterOutputAndCanvasRetire()
    {
        var root = new SceneNode("Presented reader", new UICanvasTransform());
        root.SetWorldContext(new TestWorldContext());
        object output = new();
        try
        {
            UICanvasComponent canvas = root.AddComponent<UICanvasComponent>()!;
            var bounds = new BoundingRectangleF(Vector2.Zero, new Vector2(100));
            UIPublication packet = new(1, 1, 1, 1, 1, default, default, [], [],
                bounds, Matrix4x4.Identity, Matrix4x4.Identity);
            canvas.MarkPresented(output, packet);
            packet.Release();

            canvas.TryAcquirePresentedPublication(output, out UIPublication? reader).ShouldBeTrue();
            reader.ShouldNotBeNull();
            canvas.RemovePublicationOutput(output);
            canvas.IsActive = false;

            reader.ResourcesRetired.ShouldBeFalse();
            reader.Release();
            reader.ResourcesRetired.ShouldBeTrue();
            Should.Throw<InvalidOperationException>(reader.Release);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void RegisteredGamepad_EditsSliderBeforeDirectionalFocusMoves()
    {
        var root = new SceneNode("Gamepad navigation", new UICanvasTransform());
        root.SetWorldContext(new TestWorldContext());
        var viewport = new XRViewport(null)
        {
            AutomaticallyCollectVisible = false,
            AutomaticallySwapBuffers = false,
            SetRenderPipelineFromCamera = false,
        };
        try
        {
            UICanvasComponent canvas = root.AddComponent<UICanvasComponent>()!;
            UICanvasInputComponent input = root.AddComponent<UICanvasInputComponent>()!;
            PawnComponent pawn = root.NewChild("Pawn").AddComponent<PawnComponent>()!;
            LocalPlayerController controller = new() { Viewport = viewport };
            pawn.Controller = controller;
            input.OwningPawn = pawn;
            WindowSnapshotGamepad gamepad = new(0);
            controller.Input.UpdateDevices(null, null, gamepad);
            input.RegisterInput(controller.Input);

            SceneNode sliderNode = root.NewChild("Slider");
            sliderNode.SetTransform<UIBoundableTransform>();
            UIRangeSliderComponent slider = sliderNode.AddComponent<UIRangeSliderComponent>()!;
            slider.Step = 0.25;
            SceneNode belowNode = root.NewChild("Below");
            belowNode.SetTransform<UIBoundableTransform>();
            UIButtonComponent below = belowNode.AddComponent<UIButtonComponent>()!;
            UIElementIdentity scope = canvas.CanvasTransform.ElementIdentity;
            Present(1, Hit(slider, 0, Vector2.Zero, scope),
                Hit(below, 1, new Vector2(0, -40), scope));
            input.FocusedComponent = slider;

            Press(EGamePadButton.DPadRight);
            slider.Value.ShouldBe(0.25);
            input.FocusedComponent.ShouldBeSameAs(slider);
            Release(EGamePadButton.DPadRight);
            Press(EGamePadButton.DPadDown);
            input.FocusedComponent.ShouldBeSameAs(below);

            void Present(ulong generation, params UIHitRecord[] hits)
            {
                BoundingRectangleF bounds = new(new Vector2(-100), new Vector2(300));
                UIPublication packet = new(generation, canvas.PublicationLifetime, generation, generation,
                    canvas.CanvasTransform.ChangeJournal.Revision, default,
                    new UIElementIdentity[] { scope }, hits, [], bounds,
                    Matrix4x4.Identity, Matrix4x4.Identity);
                canvas.MarkPresented(viewport, packet);
                packet.Release();
                RefreshPresentedInput(input);
            }

            void Press(EGamePadButton button)
            {
                ushort mask = (ushort)(1 << (int)button);
                gamepad.ApplySnapshot(new WindowInputSnapshotAccumulator().Publish(1, 0, 1, true, false,
                    primaryGamepad: new WindowGamepadSnapshot(true, mask, 0, 0, 0, 0, 0, 0)));
                controller.Input.TickStates(1f / 60f);
            }

            void Release(EGamePadButton button)
            {
                gamepad.ApplySnapshot(new WindowInputSnapshotAccumulator().Publish(1, 0, 1, true, false,
                    primaryGamepad: new WindowGamepadSnapshot(true, 0, 0, 0, 0, 0, 0, 0)));
                controller.Input.TickStates(1f / 60f);
            }
        }
        finally
        {
            root.Destroy(true);
            viewport.Destroy();
        }
    }

    [Test]
    public void RegisteredTab_NestedPopupScopesTrapAndRestoreFocusOnDismiss()
    {
        var root = new SceneNode("Nested popup focus", new UICanvasTransform());
        root.SetWorldContext(new TestWorldContext());
        var viewport = new XRViewport(null)
        {
            AutomaticallyCollectVisible = false,
            AutomaticallySwapBuffers = false,
            SetRenderPipelineFromCamera = false,
        };
        try
        {
            UICanvasComponent canvas = root.AddComponent<UICanvasComponent>()!;
            UICanvasInputComponent input = root.AddComponent<UICanvasInputComponent>()!;
            PawnComponent pawn = root.NewChild("Pawn").AddComponent<PawnComponent>()!;
            LocalPlayerController controller = new() { Viewport = viewport };
            pawn.Controller = controller;
            input.OwningPawn = pawn;
            WindowSnapshotKeyboard keyboard = new(0);
            controller.Input.UpdateDevices(keyboard, null, null);
            input.RegisterInput(controller.Input);
            UIButtonComponent outside = root.NewChild("Outside").AddComponent<UIButtonComponent>()!;
            SceneNode outerNode = root.NewChild("Outer popup");
            outerNode.SetTransform<UIBoundableTransform>();
            UIContextMenuComponent outer = outerNode.AddComponent<UIContextMenuComponent>()!;
            SceneNode innerNode = root.NewChild("Inner popup");
            innerNode.SetTransform<UIBoundableTransform>();
            UIContextMenuComponent inner = innerNode.AddComponent<UIContextMenuComponent>()!;
            UIElementIdentity rootScope = canvas.CanvasTransform.ElementIdentity;
            WindowInputSnapshotAccumulator frames = new();

            Present(1, [Hit(outside, 0, Vector2.Zero, rootScope)], []);
            input.FocusedComponent = outside;
            outer.Show(Vector2.Zero, new ContextMenuItem("Outer", () => { }));
            outerNode.GetTransformAs<UIBoundableTransform>(true)!.Visibility = EVisibility.Visible;
            UIButtonComponent outerButton = outerNode.FirstChild!.FirstChild!.GetComponent<UIButtonComponent>()!;
            UIElementIdentity outerScope = outerNode.GetTransformAs<UIBoundableTransform>(true)!.ElementIdentity;
            Present(2,
                [Hit(outside, 0, Vector2.Zero, rootScope), Hit(outerButton, 1, new Vector2(30, 0), outerScope)],
                [new UIFocusScopeRecord(outerScope, 1, 2, 1, 10)]);
            input.FocusedComponent.ShouldBeSameAs(outerButton);
            Tab(true);
            input.FocusedComponent.ShouldBeSameAs(outerButton);
            Tab(false);

            inner.Show(Vector2.Zero, new ContextMenuItem("Inner", () => { }));
            innerNode.GetTransformAs<UIBoundableTransform>(true)!.Visibility = EVisibility.Visible;
            UIButtonComponent innerButton = innerNode.FirstChild!.FirstChild!.GetComponent<UIButtonComponent>()!;
            UIElementIdentity innerScope = innerNode.GetTransformAs<UIBoundableTransform>(true)!.ElementIdentity;
            Present(3,
                [Hit(outside, 0, Vector2.Zero, rootScope), Hit(outerButton, 1, new Vector2(30, 0), outerScope),
                    Hit(innerButton, 2, new Vector2(60, 0), innerScope)],
                [new UIFocusScopeRecord(outerScope, 1, 3, 1, 10),
                    new UIFocusScopeRecord(innerScope, 2, 3, 2, 20)]);
            input.FocusedComponent.ShouldBeSameAs(innerButton);
            Tab(true);
            input.FocusedComponent.ShouldBeSameAs(innerButton);
            Tab(false);

            inner.Hide();
            Present(4,
                [Hit(outside, 0, Vector2.Zero, rootScope), Hit(outerButton, 1, new Vector2(30, 0), outerScope)],
                [new UIFocusScopeRecord(outerScope, 1, 2, 1, 10)]);
            input.FocusedComponent.ShouldBeSameAs(outerButton);
            outer.Hide();
            Present(5, [Hit(outside, 0, Vector2.Zero, rootScope)], []);
            input.FocusedComponent.ShouldBeSameAs(outside);

            void Present(ulong generation, UIHitRecord[] hits, UIFocusScopeRecord[] scopes)
            {
                BoundingRectangleF bounds = new(new Vector2(-100), new Vector2(300));
                UIPublication packet = new(generation, canvas.PublicationLifetime, generation, generation,
                    canvas.CanvasTransform.ChangeJournal.Revision, default,
                    new UIElementIdentity[] { rootScope }, hits, [], bounds,
                    Matrix4x4.Identity, Matrix4x4.Identity, focusScopes: scopes);
                canvas.MarkPresented(viewport, packet);
                packet.Release();
                RefreshPresentedInput(input);
            }

            void Tab(bool pressed)
            {
                if (pressed)
                    frames.RecordKeyDown(EKey.Tab);
                else
                    frames.RecordKeyUp(EKey.Tab);
                keyboard.ApplySnapshot(frames.Publish(1, 0, 0, true, false));
                controller.Input.TickStates(1f / 60f);
            }
        }
        finally
        {
            root.Destroy(true);
            viewport.Destroy();
        }
    }

    [Test]
    public void VirtualItemFocus_WaitsForPresentedLeaseAndRejectsRecycledRow()
    {
        var root = new SceneNode("Virtual focus", new UICanvasTransform());
        root.SetWorldContext(new TestWorldContext());
        try
        {
            UICanvasComponent canvas = root.AddComponent<UICanvasComponent>()!;
            UICanvasInputComponent input = root.AddComponent<UICanvasInputComponent>()!;
            UIListTransform list = new() { ItemSize = 20, VirtualOverscan = 0 };
            SceneNode listNode = new(root, "List", list);
            UIVirtualTreeProvider provider = new(
                () =>
                {
                    UIBoundableTransform createdRow = new();
                    SceneNode createdNode = new(listNode, "Row", createdRow);
                    createdNode.NewChild("Button").AddComponent<UIVirtualButtonComponent>();
                    return createdRow;
                },
                (_, _, _, _) => { },
                (_, _, _) => { });
            provider.Insert(1, 0, 0, "First");
            provider.Insert(2, 0, 1, "Second");
            list.VirtualProvider = provider;
            list.SynchronizeVirtualRows(20);
            list.TryGetRealizedVirtualRow(1, out UIBoundableTransform? row,
                out UIVirtualRowLease lease).ShouldBeTrue();
            row.ShouldNotBeNull();
            UIVirtualButtonComponent focusButton =
                row!.SceneNode!.FirstChild!.GetComponent<UIVirtualButtonComponent>()!;

            input.RequestVirtualItemFocus(list, 1).ShouldBeTrue();
            input.FocusedComponent.ShouldBeNull();
            focusButton.UserInterfaceCanvas.ShouldBeSameAs(canvas);
            focusButton.CanInteract.ShouldBeTrue();
            BoundingRectangleF bounds = new(Vector2.Zero, new Vector2(100));
            UIHitRecord hit = new(focusButton.RenderInfo2D, focusButton.RenderInfo2D.MembershipId,
                focusButton.UITransform.ElementIdentity, focusButton.InteractionRevision,
                Matrix4x4.Identity, new Vector2(20), bounds, null,
                3, 2, 4, 3, 0, 0, true, false,
                FocusScopeIdentity: canvas.CanvasTransform.ElementIdentity,
                VirtualListIdentity: list.ElementIdentity,
                VirtualRowIdentity: row!.ElementIdentity,
                VirtualRowLease: lease);
            UIPublication packet = new(1, canvas.PublicationLifetime, 1, 1,
                list.ParentCanvas!.ChangeJournal.Revision, default,
                new UIElementIdentity[] { canvas.CanvasTransform.ElementIdentity, list.ElementIdentity,
                    row!.ElementIdentity, focusButton.UITransform.ElementIdentity }, [hit], [],
                bounds, Matrix4x4.Identity, Matrix4x4.Identity);
            canvas.MarkPresented(canvas, packet);
            packet.Release();

            RefreshPresentedInput(input);
            input.FocusedComponent.ShouldBeSameAs(focusButton);

            UIVirtualButtonComponent elsewhere =
                root.NewChild("Other focus").AddComponent<UIVirtualButtonComponent>()!;
            input.RequestVirtualItemFocus(list, 1).ShouldBeTrue();
            input.FocusedComponent = elsewhere;
            RefreshPresentedInput(input);
            input.FocusedComponent.ShouldBeSameAs(elsewhere);

            input.FocusedComponent = null;
            focusButton.CancelForRecycle();
            input.RequestVirtualItemFocus(list, 1).ShouldBeTrue();
            RefreshPresentedInput(input);
            input.FocusedComponent.ShouldBeNull();

            list.ContentScrollOffset = 20;
            list.SynchronizeVirtualRows(20);
            input.RequestVirtualItemFocus(list, 2).ShouldBeTrue();
            RefreshPresentedInput(input);
            input.FocusedComponent.ShouldBeNull();
        }
        finally { root.Destroy(true); }
    }

    private static void RefreshPresentedInput(UICanvasInputComponent input)
    {
        var method = typeof(UICanvasInputComponent).GetMethod("RefreshPresentedInput",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        UIPublication? publication = (UIPublication?)method.Invoke(input, null);
        publication?.Release();
    }

    private static UIHitRecord Hit(UIInteractableComponent control, int index, Vector2 position,
        UIElementIdentity scope)
        => new(control.RenderInfo2D, control.RenderInfo2D.MembershipId,
            control.UITransform.ElementIdentity, control.InteractionRevision,
            Matrix4x4.CreateTranslation(-position.X, -position.Y, 0), new Vector2(20),
            new BoundingRectangleF(position, new Vector2(20)), null,
            index, -1, index + 1, 0, 0, index, true, false,
            FocusScopeIdentity: scope);
}
