using NUnit.Framework;
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
    [Test]
    public void RecycledDragSource_LosesItsCapturedIdentityBeforeDrop()
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
            using IDisposable registration = EditorDragDropUtility.RegisterDropTarget(targetTransform,
                _ => { drops++; return true; }, hoverChanged: value => hovered = value);
            typeof(UICanvasInputComponent).GetProperty(nameof(UICanvasInputComponent.CursorPositionWorld2D))!
                .SetValue(input, new Vector2(10, 10));
            Type utility = typeof(EditorDragDropUtility);
            BindingFlags privateStatic = BindingFlags.Static | BindingFlags.NonPublic;
            object session = utility.GetMethod("GetPreferredSession", privateStatic)!
                .Invoke(null, null).ShouldNotBeNull();
            utility.GetMethod("UpdateHoveredTarget", privateStatic)!.Invoke(null, [session]);
            hovered.ShouldBeTrue();

            button.CancelForRecycle();
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
}
