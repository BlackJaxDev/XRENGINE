using System;
using XREngine.Data.Geometry;

namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer
{
    private int _openXrExternalSwapchainRenderDepth;
    private BoundingRectangle _openXrExternalSwapchainTargetRegion;

    public override bool IsRenderingExternalSwapchainTarget => _openXrExternalSwapchainRenderDepth > 0;

    public override bool TryGetExternalSwapchainTargetRegion(out BoundingRectangle region)
    {
        if (_openXrExternalSwapchainRenderDepth > 0 &&
            _openXrExternalSwapchainTargetRegion.Width > 0 &&
            _openXrExternalSwapchainTargetRegion.Height > 0)
        {
            region = _openXrExternalSwapchainTargetRegion;
            return true;
        }

        region = default;
        return false;
    }

    internal IDisposable EnterOpenXrExternalSwapchainRenderScope(uint width, uint height)
    {
        BoundingRectangle previousRegion = _openXrExternalSwapchainTargetRegion;
        _openXrExternalSwapchainRenderDepth++;
        _openXrExternalSwapchainTargetRegion = new BoundingRectangle(
            0,
            0,
            (int)Math.Min(width, (uint)int.MaxValue),
            (int)Math.Min(height, (uint)int.MaxValue));

        return new OpenXrExternalSwapchainRenderScope(this, previousRegion);
    }

    private sealed class OpenXrExternalSwapchainRenderScope(
        OpenGLRenderer renderer,
        BoundingRectangle previousRegion) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            renderer._openXrExternalSwapchainRenderDepth = Math.Max(0, renderer._openXrExternalSwapchainRenderDepth - 1);
            renderer._openXrExternalSwapchainTargetRegion = previousRegion;
        }
    }
}
