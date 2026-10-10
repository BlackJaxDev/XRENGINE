using System.Numerics;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    private RenderFrameViewSelection? _frozenView;

    internal RenderFrameViewSelection RequireFrozenView()
        => _frozenView ?? throw new InvalidOperationException("WebGPU.View.CaptureMissing: draw uniforms require their frozen pass view.");

    internal WebGpuFrozenViewScope PushFrozenView(in RenderFrameViewSelection view)
    {
        RenderFrameViewSelection? previous = _frozenView;
        SetFrozenView(view);
        return new(this, previous);
    }

    internal WebGpuFrozenViewScope PushFrozenView(XRCamera? camera)
    {
        if (camera is null || _frozenView is { } enclosing &&
            enclosing.View.SourceCameraIdentity == camera.RenderIdentity)
            return new(this, _frozenView);
        var viewport = ResolveEngineDrawArea().Viewport;
        WebGpuFrameBuffer? framebuffer = GetBoundEngineFrameBuffer();
        var output = CurrentFrameOutput ?? throw new InvalidOperationException("WebGPU.View.OutputMissing: a frozen draw requires an active output.");
        float width = viewport is { } area ? area.Width : framebuffer?.Width ?? output.Properties.Width;
        float height = viewport is { } region ? region.Height : framebuffer?.Height ?? output.Properties.Height;
        Vector2 size = new(width, height);
        RenderFrameViewSelection view = RenderFrameViewSetCapture.SelectForDraw(
            RuntimeRenderingHostServices.FrameTiming.ActiveRenderCommandExecutionState,
            camera, RuntimeEngine.Rendering.State.RenderingPipelineState?.UseUnjitteredProjection == true,
            size, RuntimeRenderingHostServices.FrameTiming.ElapsedTime, requireCapturedView: false);
        return PushFrozenView(in view);
    }

    internal void SetFrozenView(RenderFrameViewSelection? view)
        => SetField(ref _frozenView, view, publishNotifications: false);
}
