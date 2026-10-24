using System.Numerics;
using System.Reflection;
using NUnit.Framework;
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

/// <summary>Checks a registered pointer drag across accepted output changes.</summary>
[TestFixture, NonParallelizable]
public sealed class UIPointerCaptureGenerationTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void DragKeepsPressedGeometryAndBlocksCameraUntilReleaseOrRecycle(bool recycle)
    {
        using UITestShaderScope shaders = new();
        SceneNode root = new("Pointer capture", new UICanvasTransform());
        root.SetWorldContext(new TestWorldContext());
        XRViewport viewport = new(null)
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
            WindowSnapshotMouse mouse = new(0);
            controller.Input.UpdateDevices(null, mouse, null);
            input.RegisterInput(controller.Input);
            UIRangeSliderComponent slider = root.NewChild("Slider").AddComponent<UIRangeSliderComponent>()!;
            UIElementIdentity scope = canvas.CanvasTransform.ElementIdentity;
            WindowInputSnapshotAccumulator frames = new();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            Action collect = typeof(UICanvasInputComponent).GetMethod("CollectVisible", flags)!
                .CreateDelegate<Action>(input);
            Action swap = typeof(UICanvasInputComponent).GetMethod("SwapBuffers", flags)!
                .CreateDelegate<Action>(input);
            FlyingCameraPawnComponent camera = new();
            camera.LinkedUICanvasInputs.Add(input);
            try
            {
                Present(1, 100, Matrix4x4.Identity);
                Move(25);
                frames.RecordMouseDown(EMouseButton.LeftClick);
                Tick();
                Assert.That(slider.IsCaptured, Is.True);
                Assert.That(slider.Value, Is.EqualTo(0.25).Within(0.001));

                // The new output moves the slider away. The held drag keeps its press mapping.
                Present(2, 400, Matrix4x4.CreateTranslation(-500, 0, 0));
                Move(75);
                Assert.That(slider.Value, Is.EqualTo(0.75).Within(0.001));
                Assert.That(camera.IsHoveringUI(), Is.True);
                if (recycle)
                {
                    slider.CancelForRecycle();
                    Assert.That(slider.Value, Is.Zero);
                    Move(90);
                    Assert.That(slider.Value, Is.Zero);
                    Assert.That(camera.IsHoveringUI(), Is.False);
                }
                frames.RecordMouseUp(EMouseButton.LeftClick);
                Tick();
                Assert.That(slider.IsCaptured, Is.False);
                Assert.That(camera.IsHoveringUI(), Is.False);
            }
            finally { camera.Destroy(true); }

            void Present(ulong generation, float width, Matrix4x4 outputToCanvas)
            {
                UIHitRecord hit = new(slider.RenderInfo2D, slider.RenderInfo2D.MembershipId,
                    slider.UITransform.ElementIdentity, slider.InteractionRevision,
                    Matrix4x4.Identity, new Vector2(width, 20),
                    new BoundingRectangleF(Vector2.Zero, new Vector2(width, 20)), null,
                    1, 0, 2, 1, 0, 0, true, false, FocusScopeIdentity: scope,
                    PointerGeometry: new UIPointerGeometry(new Vector2(width, 20)));
                Matrix4x4.Invert(outputToCanvas, out Matrix4x4 canvasToOutput);
                UIPublication packet = new(generation, canvas.PublicationLifetime, generation, generation,
                    canvas.CanvasTransform.ChangeJournal.Revision, default, new[] { scope }, [hit], [],
                    new BoundingRectangleF(Vector2.Zero, new Vector2(1000, 100)),
                    canvasToOutput, outputToCanvas);
                try { canvas.MarkPresented(viewport, packet); }
                finally { packet.Release(); }
            }

            void Move(float x)
            {
                frames.RecordPointerPosition(x, 10);
                Tick();
                collect();
                swap();
            }

            void Tick()
            {
                mouse.ApplySnapshot(frames.Publish(0, 1, 0, true, false));
                controller.Input.TickStates(1f / 60f);
            }
        }
        finally
        {
            root.Destroy(true);
            viewport.Destroy();
        }
    }
}
