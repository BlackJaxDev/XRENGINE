using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Checks the view kind and history key that the mono frame-view capture gives an OpenXR per-eye
/// render and a desktop render.
/// </summary>
[TestFixture]
public sealed class RenderFrameViewSetCaptureEyeTests
{
    [TestCase(true, EVrOutputViewKind.LeftEye, RenderFrameViewSetCapture.LeftEyeHistoryKey)]
    [TestCase(false, EVrOutputViewKind.RightEye, RenderFrameViewSetCapture.RightEyeHistoryKey)]
    public void PerEyeMonoCapture_UsesEyeKindAndEyeHistoryKey(bool leftEye, EVrOutputViewKind expectedKind, ulong expectedHistoryKey)
    {
        XRCamera camera = new() { Parameters = new XROVRCameraParameters(leftEye, 0.1f, 1000.0f) };
        XRViewport viewport = CreateViewport(rendersToExternalSwapchainTarget: true);

        RenderFrameViewSet views = RenderFrameViewSetCapture.Capture(new TestExecutionState(viewport, camera));

        views.ViewCount.ShouldBe(1);
        RenderFrameViewDescriptor view = views.GetView(0);
        view.Kind.ShouldBe(expectedKind);
        view.HistoryKey.ShouldBe(expectedHistoryKey);
        view.IsLeftEyeFamily.ShouldBe(leftEye);
        view.IsRightEyeFamily.ShouldBe(!leftEye);
    }

    [Test]
    public void DesktopMonoCapture_KeepsDesktopKindAndMonoHistoryKey()
    {
        XRCamera camera = new() { Parameters = new XRPerspectiveCameraParameters(0.1f, 1000.0f) };
        XRViewport viewport = CreateViewport(rendersToExternalSwapchainTarget: false);

        RenderFrameViewSet views = RenderFrameViewSetCapture.Capture(new TestExecutionState(viewport, camera));

        views.ViewCount.ShouldBe(1);
        RenderFrameViewDescriptor view = views.GetView(0);
        view.Kind.ShouldBe(EVrOutputViewKind.DesktopEditor);
        view.HistoryKey.ShouldBe(RenderFrameViewSetCapture.MonoHistoryKey);
    }

    [Test]
    public void EyeCameraOnDesktopViewport_KeepsDesktopKind()
    {
        // An eye camera that renders into an engine-owned target (for example a mirror or a
        // preview) is not an OpenXR per-eye swapchain render.
        XRCamera camera = new() { Parameters = new XROVRCameraParameters(true, 0.1f, 1000.0f) };
        XRViewport viewport = CreateViewport(rendersToExternalSwapchainTarget: false);

        RenderFrameViewSet views = RenderFrameViewSetCapture.Capture(new TestExecutionState(viewport, camera));

        views.GetView(0).Kind.ShouldBe(EVrOutputViewKind.DesktopEditor);
        views.GetView(0).HistoryKey.ShouldBe(RenderFrameViewSetCapture.MonoHistoryKey);
    }

    private static XRViewport CreateViewport(bool rendersToExternalSwapchainTarget)
        => new(null, 64u, 64u)
        {
            AutomaticallyCollectVisible = false,
            AutomaticallySwapBuffers = false,
            SetRenderPipelineFromCamera = false,
            RendersToExternalSwapchainTarget = rendersToExternalSwapchainTarget,
        };

    private sealed class TestExecutionState(XRViewport viewport, XRCamera camera) : IRuntimeRenderCommandExecutionState
    {
        public IRuntimeViewportHost? WindowViewport => viewport;
        public IRuntimeRenderCommandSceneContext? RenderingScene => null;
        public IRuntimeRenderCamera? SceneCamera => camera;
        public IRuntimeRenderCamera? RenderingCamera => camera;
        public IRuntimeRenderCamera? StereoRightEyeCamera => null;
        public bool StereoPass => false;
    }
}
