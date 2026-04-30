using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class CameraComponentLazyInitializationTests
{
    [Test]
    public void AssigningScreenSpaceUserInterface_DoesNotReenterCameraLazyFactory()
    {
        var component = new CameraComponent();
        var ui = new TestUserInterface(isScreenSpace: true);

        Assert.DoesNotThrow(() => component.UserInterface = ui);

        ui.CameraSpaceResizeCount.ShouldBe(0);
        ui.LastCamera.ShouldBeNull();
        ui.LastParameters.ShouldBeNull();
    }

    [Test]
    public void AssigningCameraSpaceUserInterface_BindsConstructedCameraWithoutLazyReentry()
    {
        var component = new CameraComponent();
        var ui = new TestUserInterface(isScreenSpace: false);

        Assert.DoesNotThrow(() => component.UserInterface = ui);

        ui.CameraSpaceResizeCount.ShouldBe(1);
        ReferenceEquals(ui.LastCamera, component.Camera).ShouldBeTrue();
        ReferenceEquals(ui.LastParameters, component.Camera.Parameters).ShouldBeTrue();
    }

    private sealed class TestUserInterface : IRuntimeScreenSpaceUserInterface
    {
        public TestUserInterface(bool isScreenSpace)
            => IsScreenSpace = isScreenSpace;

        public bool IsActive => true;
        public bool IsScreenSpace { get; }
        public int CameraSpaceResizeCount { get; private set; }
        public XRCamera? LastCamera { get; private set; }
        public XRCameraParameters? LastParameters { get; private set; }

        public void ResizeScreenSpace(Vector2 size)
        {
        }

        public void ResizeCameraSpace(XRCamera camera, XRCameraParameters parameters)
        {
            CameraSpaceResizeCount++;
            LastCamera = camera;
            LastParameters = parameters;
        }

        public void ClearCameraSpaceCamera(XRCamera camera)
        {
            if (ReferenceEquals(LastCamera, camera))
                LastCamera = null;
        }

        public bool TryGetImGuiDisplayMetrics(
            IRuntimeViewportHost? viewport,
            XRCamera? camera,
            out Vector2 displaySize,
            out Vector2 displayPosition,
            out Vector2 framebufferScale)
        {
            displaySize = Vector2.Zero;
            displayPosition = Vector2.Zero;
            framebufferScale = Vector2.One;
            return false;
        }

        public void CollectVisibleItemsScreenSpace(IRuntimeViewportHost? viewport)
        {
        }

        public void SwapBuffersScreenSpace()
        {
        }

        public void RenderScreenSpace(IRuntimeViewportHost? viewport, XRFrameBuffer? outputFBO)
        {
        }
    }
}
