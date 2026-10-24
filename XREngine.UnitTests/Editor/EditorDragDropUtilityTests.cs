using NUnit.Framework;
using System.Collections;
using Shouldly;
using System.Numerics;
using System.Reflection;
using XREngine.Components;
using XREngine.Editor.UI;
using XREngine.Input.Devices;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Runtime.InputIntegration;
using XREngine.Scene;
using XREngine.UnitTests.Physics;
using XREngine.UnitTests.Rendering;

namespace XREngine.UnitTests.Editor;

[TestFixture, NonParallelizable]
public sealed class EditorDragDropUtilityTests
{
    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    public void RecycledDragSource_LosesItsCapturedIdentityBeforeDrop(bool disposeHoveredTarget,
        bool throwOnCancellation)
    {
        using UITestShaderScope shaders = new();
        UICanvasTransform canvasTransform = new() { Width = 100, Height = 100 };
        SceneNode root = new("Drag source canvas", canvasTransform) { IsActiveSelf = false };
        root.SetWorldContext(new TestWorldContext());
        try
        {
            UICanvasComponent canvas = root.AddComponent<UICanvasComponent>()!;
            UICanvasInputComponent input = root.AddComponent<UICanvasInputComponent>()!;
            PawnComponent pawn = new SceneNode(root, "Pawn").AddComponent<PawnComponent>()!;
            LocalPlayerController controller = new();
            pawn.Controller = controller;
            input.OwningPawn = pawn;
            WindowSnapshotMouse mouse = new(0);
            controller.Input.UpdateDevices(null, mouse, null);
            SceneNode row = new(root) { Name = "Recycled row" };
            row.SetTransform<UIBoundableTransform>();
            UIButtonComponent button = row.AddComponent<UIButtonComponent>()!;
            SceneNode targetNode = new(root) { Name = "Drop target" };
            UIBoundableTransform targetTransform = targetNode.SetTransform<UIBoundableTransform>();
            targetTransform.MinAnchor = Vector2.Zero;
            targetTransform.MaxAnchor = Vector2.Zero;
            targetTransform.NormalizedPivot = Vector2.Zero;
            targetTransform.Width = 100;
            targetTransform.Height = 100;
            root.IsActiveSelf = true;
            canvasTransform.UpdateLayoutAsync();
            SpinWait.SpinUntil(() => canvasTransform.PumpLayoutResults(), TimeSpan.FromSeconds(5))
                .ShouldBeTrue();

            long revision = button.InteractionRevision;
            var identity = button.AccessibilityIdentity;
            EditorDragDropUtility.IsCurrentDragSource(button, input, canvas, revision, identity)
                .ShouldBeTrue();

            WindowInputSnapshotAccumulator frames = new();
            frames.RecordMouseDown(EMouseButton.LeftClick);
            mouse.ApplySnapshot(frames.Publish(0, 1, 0, true, false));
            mouse.TickStates(1f / 60f);
            EditorDragDropUtility.IsLeftMouseButtonHeldFor(button).ShouldBeTrue();
            EditorDragDropUtility.BeginDrag(button,
                EditorDragDropUtility.CreateEditorPayload("row", "old"), Vector2.Zero)
                .ShouldBeTrue();
            EditorDragDropUtility.TryGetActivePayload(out _).ShouldBeTrue();
            int drops = 0;
            bool hovered = false;
            bool cancellationStarted = false;
            InvalidOperationException cancellationFailure = new("Expected hover cancellation failure.");
            using IDisposable registration = EditorDragDropUtility.RegisterDropTarget(targetTransform,
                _ => { drops++; return true; }, hoverChanged: value =>
                {
                    hovered = value;
                    if (!value && cancellationStarted && throwOnCancellation)
                    {
                        cancellationStarted = false;
                        throw cancellationFailure;
                    }
                });
            typeof(UICanvasInputComponent).GetProperty(nameof(UICanvasInputComponent.CursorPositionWorld2D))!
                .SetValue(input, new Vector2(10, 10));
            Type utility = typeof(EditorDragDropUtility);
            BindingFlags privateStatic = BindingFlags.Static | BindingFlags.NonPublic;
            object session = utility.GetMethod("GetPreferredSession", privateStatic)!
                .Invoke(null, null).ShouldNotBeNull();
            utility.GetMethod("UpdateHoveredTarget", privateStatic)!.Invoke(null, [session]);
            hovered.ShouldBeTrue();
            if (disposeHoveredTarget)
            {
                registration.Dispose();
                hovered.ShouldBeFalse();
                session.GetType().GetProperty("HoveredTarget")!.GetValue(session).ShouldBeNull();
            }

            button.CancelForRecycle();
            cancellationStarted = true;
            if (throwOnCancellation)
                Assert.That(Assert.Throws<InvalidOperationException>(
                    () => EditorDragDropUtility.CancelDragsForSource(button)), Is.SameAs(cancellationFailure));
            else
                EditorDragDropUtility.CancelDragsForSource(button);
            EditorDragDropUtility.IsDragging.ShouldBeFalse();
            utility.GetField("_tickSubscribed", privateStatic)!.GetValue(null).ShouldBe(false);
            hovered.ShouldBeFalse();
            EditorDragDropUtility.IsCurrentDragSource(button, input, canvas, revision, identity)
                .ShouldBeFalse();
            utility.GetMethod("OnPostUpdateFrame", privateStatic)!.Invoke(null, null);
            EditorDragDropUtility.IsDragging.ShouldBeFalse();
            hovered.ShouldBeFalse();
            drops.ShouldBe(0);
            EditorDragDropUtility.TryGetActivePayload(out _).ShouldBeFalse();
            EditorDragDropUtility.IsCurrentDragSource(button, input, canvas,
                button.InteractionRevision, identity).ShouldBeTrue();
            button.IsEnabled = false;
            EditorDragDropUtility.IsCurrentDragSource(button, input, canvas,
                button.InteractionRevision, identity).ShouldBeFalse();
        }
        finally { root.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DropTargetDisposalReleasesRegistryBeforeReentrantOrThrowingNotification(bool throwFromNotification)
    {
        using UITestShaderScope shaders = new();
        SceneNode node = new("Drop target", new UIBoundableTransform());
        IList targets = (IList)typeof(EditorDragDropUtility).GetField("_dropTargets",
            BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        int baseline = targets.Count;
        int notifications = 0;
        IDisposable? handle = null;
        InvalidOperationException failure = new("Expected hover observer failure.");
        try
        {
            handle = EditorDragDropUtility.RegisterDropTarget((UIBoundableTransform)node.Transform,
                _ => true, hoverChanged: hovering =>
                {
                    hovering.ShouldBeFalse();
                    targets.Count.ShouldBe(baseline);
                    notifications++;
                    handle!.Dispose();
                    if (throwFromNotification)
                        throw failure;
                });
            targets.Count.ShouldBe(baseline + 1);
            if (throwFromNotification)
                Assert.That(Assert.Throws<InvalidOperationException>(handle.Dispose), Is.SameAs(failure));
            else
                Assert.DoesNotThrow(handle.Dispose);
            targets.Count.ShouldBe(baseline);
            notifications.ShouldBe(1);
            Assert.DoesNotThrow(handle.Dispose);
            notifications.ShouldBe(1);
        }
        finally
        {
            handle?.Dispose();
            node.Destroy(true);
        }
    }

}
