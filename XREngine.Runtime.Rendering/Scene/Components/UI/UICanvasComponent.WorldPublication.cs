using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.UI;

namespace XREngine.Components;

public partial class UICanvasComponent
{
    private UIRenderSurfacePool? _offscreenSurfacePool;
    private UIDrawPayloadBuilder? _offscreenQuadPayloadBuilder;
    private RenderCommandMesh2D? _offscreenQuadCaptureCommand;

    private bool TryPrepareOffscreenPublication(out UIRenderSurface? writer,
        out UIDrawPayload? quadPayload, out Matrix4x4 quadWorldMatrix,
        out string? failure)
    {
        writer = null;
        quadPayload = null;
        quadWorldMatrix = Matrix4x4.Identity;
        failure = null;
        EnsureOffscreenResourcesInitialized();
        if (_ownedOffscreenRenderer is null || _offscreenMaterial is null)
        {
            failure = "UI.Canvas.SurfacePending: the quad draw resources are unavailable.";
            return false;
        }
        bool webGpu = OperatingSystem.IsBrowser() ||
            AbstractRenderer.Current?.BackendId == RendererBackendId.WebGPU ||
            RuntimeEngineMaterialConstructionServices.Target == EngineMaterialConstructionTarget.WebGpuCooked;
        _offscreenSurfacePool ??= webGpu
            ? new UIRenderSurfacePool(EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba,
                EPixelType.HalfFloat, 8)
            : new UIRenderSurfacePool(EPixelInternalFormat.Rgba8, EPixelFormat.Rgba,
                EPixelType.UnsignedByte, 4);
        var bounds = CanvasTransform.GetActualBounds();
        float width = Math.Max(1.0f, bounds.Width);
        float height = Math.Max(1.0f, bounds.Height);
        if (width <= 1.0f && height <= 1.0f)
        {
            width = 1920.0f;
            height = 1080.0f;
        }
        if (!float.IsFinite(width) || !float.IsFinite(height) ||
            width > 16384 || height > 16384)
        {
            failure = "UI.Canvas.InvalidSurfaceSize: the canvas extent cannot be captured.";
            return false;
        }
        if (!_offscreenSurfacePool.TryReserve((uint)MathF.Ceiling(width),
                (uint)MathF.Ceiling(height), out writer, out failure))
            return false;
        bool prepared = false;
        try
        {
            _offscreenQuadPayloadBuilder ??= new UIDrawPayloadBuilder();
            _offscreenQuadPayloadBuilder.ProducerLease = writer;
            _offscreenQuadPayloadBuilder.ProducerTextureSlot = 0;
            _offscreenQuadPayloadBuilder.Bounds = new Vector4(0, 0, width, height);
            _offscreenQuadCaptureCommand ??= new RenderCommandMesh2D(
                (int)EDefaultRenderPass.TransparentForward)
            {
                Mesh = _ownedOffscreenRenderer,
                MaterialOverride = _offscreenMaterial,
            };
            if (!_offscreenQuadPayloadBuilder.TryCapture(_offscreenQuadCaptureCommand,
                    allowRendererCallbacks: false, out quadPayload, out failure))
                return false;
            Matrix4x4 world = GetWorldSpaceCanvasMatrix(out Vector2 size);
            quadWorldMatrix = Matrix4x4.CreateScale(size.X, size.Y, 1.0f) * world;
            prepared = true;
            return true;
        }
        finally
        {
            if (!prepared)
            {
                writer!.Release();
                writer = null;
            }
        }
    }

    internal void RenderPublishedWorldCommand(UIPublishedWorldMeshCommand command,
        Action draw)
    {
        UIPublication publication = command.Publication;
        publication.Retain();
        try { draw(); }
        finally { publication.Release(); }
    }
}
