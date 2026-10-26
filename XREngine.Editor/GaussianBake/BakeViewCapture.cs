using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models.Gaussian.Baking;
using XREngine.Rendering.Models.Gaussian.Baking.Views;
using XREngine.Rendering.PostProcessing;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor.GaussianBake;

/// <summary>
/// Captures a list of camera poses on the render thread. It runs as a render-thread coroutine: it first renders
/// the first pose until the renderer has no mesh generation or shader linking left, then renders and reads back
/// one pose for each dispatch. The capture follows the detached-viewport method of the octahedral impostor generator.
/// </summary>
internal sealed class BakeViewCapture
{
    /// <summary>The number of warm-up renders before the capture can start.</summary>
    private const int MinimumWarmupRenders = 8;

    /// <summary>The mesh generation budget for each frame during the capture, in milliseconds.</summary>
    private const double MeshGenerationBudgetMilliseconds = 100.0;

    /// <summary>The smallest near plane distance, in meters.</summary>
    private const float MinimumNearPlane = 0.01f;

    /// <summary>The time after which the capture starts even when the renderer still reports pending work.</summary>
    private static readonly TimeSpan WarmupTimeout = TimeSpan.FromSeconds(120);

    private readonly IRuntimeRenderWorld _world;
    private readonly RenderInfo3D[] _targets;
    private readonly Vector3 _contentCenter;
    private readonly float _contentRadius;
    private readonly IReadOnlyList<BakeCameraPose> _poses;
    private readonly int _width;
    private readonly int _height;
    private readonly CancellationToken _cancellationToken;
    private readonly TaskCompletionSource<float[][]> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly float[][] _views;
    private readonly Stopwatch _warmupTime = new();

    private XRViewport? _viewport;
    private RenderCommandCollection? _commands;
    private XRTexture2D? _colorTexture;
    private XRRenderBuffer? _depthBuffer;
    private XRFrameBuffer? _frameBuffer;
    private int _warmupRenders;
    private ulong _lastWarmupFrameId;
    private bool _warm;
    private int _nextView;

    public BakeViewCapture(
        IRuntimeRenderWorld world,
        RenderInfo3D[] targets,
        AABB contentBounds,
        IReadOnlyList<BakeCameraPose> poses,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        _world = world;
        _targets = targets;
        _contentCenter = contentBounds.Center;
        _contentRadius = MathF.Max(contentBounds.Size.Length() * 0.5f, MinimumNearPlane);
        _poses = poses;
        _width = width;
        _height = height;
        _cancellationToken = cancellationToken;
        _views = new float[poses.Count][];
    }

    /// <summary>Gets the task that completes with one linear RGBA array for each pose, top row first.</summary>
    public Task<float[][]> Completion => _completion.Task;

    /// <summary>Runs one coroutine dispatch on the render thread. Returns true when the capture is complete.</summary>
    public bool Step()
    {
        try
        {
            if (_cancellationToken.IsCancellationRequested)
            {
                _completion.TrySetCanceled(_cancellationToken);
                Release();
                return true;
            }

            AbstractRenderer? renderer = AbstractRenderer.Current;
            if (renderer is null)
                return false;

            if (_viewport is null)
                CreateResources(renderer);

            if (!_warm)
            {
                WarmUp(renderer);
                return false;
            }

            _views[_nextView] = RenderAndRead(renderer, _poses[_nextView], _nextView);
            _nextView++;
            if (_nextView < _poses.Count)
                return false;

            Debug.Out($"[GaussianBake] Captured {_poses.Count} views at {_width}x{_height}.");
            _completion.TrySetResult(_views);
            Release();
            return true;
        }
        catch (Exception ex)
        {
            Exception failure = ex is GaussianBakeException
                ? ex
                : new GaussianBakeException(GaussianBakeFailureCodes.CaptureFailed, $"The view capture failed: {ex.Message}");
            _completion.TrySetException(failure);
            Release();
            return true;
        }
    }

    private void WarmUp(AbstractRenderer renderer)
    {
        // A frame can dispatch the coroutine more than one time. One warm-up render for each frame is sufficient,
        // because mesh generation and shader linking advance between frames.
        ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
        if (_warmupRenders > 0 && frameId == _lastWarmupFrameId)
            return;

        _lastWarmupFrameId = frameId;
        _warmupTime.Start();
        bool rendered = RenderView(_poses[0], 0, requireRender: false);
        _warmupRenders++;
        if (_warmupRenders < MinimumWarmupRenders)
            return;

        bool pending = HasPendingGpuWork(renderer, out int pendingMeshes, out bool pendingPrograms);
        if (rendered && !pending)
        {
            _warm = true;
            Debug.Out($"[GaussianBake] Renderer is ready after {_warmupRenders} warm-up renders ({_warmupTime.Elapsed.TotalSeconds:F1} s).");
            return;
        }

        if (_warmupTime.Elapsed < WarmupTimeout)
            return;

        if (!rendered)
        {
            throw new GaussianBakeException(
                GaussianBakeFailureCodes.CaptureFailed,
                $"The renderer did not accept a capture view in {WarmupTimeout.TotalSeconds:F0} s.");
        }

        _warm = true;
        Debug.LogWarning(
            $"[GaussianBake] The renderer still has pending work after {WarmupTimeout.TotalSeconds:F0} s " +
            $"(meshes={pendingMeshes}, programs={pendingPrograms}). The capture starts now.");
    }

    private float[] RenderAndRead(AbstractRenderer renderer, BakeCameraPose pose, int viewIndex)
    {
        RenderView(pose, viewIndex, requireRender: true);
        renderer.MemoryBarrier(EMemoryBarrierMask.Framebuffer | EMemoryBarrierMask.TextureFetch | EMemoryBarrierMask.TextureUpdate);

        if (!renderer.TryReadTextureMipRgbaFloat(_colorTexture!, 0, 0, out float[]? rgba, out int width, out int height, out string failure)
            || rgba is null)
        {
            throw new GaussianBakeException(GaussianBakeFailureCodes.CaptureFailed, $"The readback of view {viewIndex} failed: {failure}");
        }

        if (width != _width || height != _height)
        {
            throw new GaussianBakeException(
                GaussianBakeFailureCodes.CaptureFailed,
                $"The readback of view {viewIndex} is {width}x{height}, not {_width}x{_height}.");
        }

        // OpenGL returns the bottom row first. The bake contract puts the top row first.
        FlipRows(rgba, width, height);
        return rgba;
    }

    /// <summary>Renders one pose into the capture framebuffer. Returns true when the renderer accepted the view.</summary>
    private bool RenderView(BakeCameraPose pose, int viewIndex, bool requireRender)
    {
        XRViewport viewport = _viewport!;
        RenderCommandCollection commands = _commands!;
        XRFrameBuffer frameBuffer = _frameBuffer!;

        XRCamera camera = BuildCaptureCamera(pose, viewport.RenderPipeline);
        viewport.Camera = camera;

        // The viewport keeps the size from its constructor. A resize outside the render scope requests pipeline
        // resources for a window target, and the render into the caller framebuffer then discards them as stale.
        viewport.WorldInstanceOverride = _world;
        viewport.MeshRenderCommandsOverride = commands;

        Frustum frustum = camera.WorldFrustum();
        for (int i = 0; i < _targets.Length; i++)
        {
            RenderInfo3D target = _targets[i];
            if (!target.IsVisible || !target.ShouldRender)
                continue;

            if (target.AllowRender(frustum, commands, camera, containsOnly: false, collectMirrors: false))
                target.CollectCommands(commands, camera);
        }

        viewport.SwapBuffers(commands, allowScreenSpaceUISwap: false);

        if (!frameBuffer.IsLastCheckComplete)
            throw new GaussianBakeException(GaussianBakeFailureCodes.CaptureFailed, $"The capture framebuffer is incomplete before view {viewIndex}.");

        bool previousSceneCapturePass = RuntimeEngine.Rendering.State.IsSceneCapturePass;
        RuntimeEngine.Rendering.State.IsSceneCapturePass = true;
        bool rendered;
        try
        {
            rendered = viewport.TryRender(frameBuffer, _world, camera);
        }
        finally
        {
            RuntimeEngine.Rendering.State.IsSceneCapturePass = previousSceneCapturePass;
        }

        if (!frameBuffer.IsLastCheckComplete)
            throw new GaussianBakeException(GaussianBakeFailureCodes.CaptureFailed, $"The capture framebuffer became incomplete during view {viewIndex}.");
        if (requireRender && !rendered)
            throw new GaussianBakeException(GaussianBakeFailureCodes.CaptureFailed, $"The renderer did not accept view {viewIndex}.");
        return rendered;
    }

    private XRCamera BuildCaptureCamera(BakeCameraPose pose, RenderPipeline? pipeline)
    {
        Transform cameraTransform = new(pose.Position, pose.Rotation);
        cameraTransform.RecalculateMatrices(true, true);

        ResolveClipPlanes(pose.Position, out float nearPlane, out float farPlane);
        XRPerspectiveCameraParameters parameters = new(pose.VerticalFovDegrees, (float)_width / _height, nearPlane, farPlane);
        XRCamera camera = new(cameraTransform, parameters)
        {
            PostProcessStates = new CameraPostProcessStateCollection(),
            CullingMask = LayerMask.Everything,
            OutputHDROverride = true,
            AntiAliasingModeOverride = EAntiAliasingMode.None,
            MsaaSampleCountOverride = 1u,
            TsrRenderScaleOverride = 1.0f,
        };

        if (pipeline is not null)
            camera.RenderPipeline = pipeline;

        // The captures must be scene-referred linear values. HDR output skips the tonemap; a fixed exposure of 1
        // keeps every view on the same scale. Contrast is a percent offset: the default 1 scales the color by
        // 1.0201 around 0.5, so 0 makes the grade an identity. Tint, hue, saturation, and brightness default to identity.
        var colorStage = camera.GetPostProcessStageState<ColorGradingSettings>();
        if (colorStage?.TryGetBacking(out ColorGradingSettings? grading) == true && grading is not null)
        {
            grading.AutoExposure = false;
            grading.Exposure = 1.0f;
            grading.Contrast = 0.0f;
        }
        else
        {
            colorStage?.SetValue(nameof(ColorGradingSettings.AutoExposure), false);
            colorStage?.SetValue(nameof(ColorGradingSettings.Exposure), 1.0f);
            colorStage?.SetValue(nameof(ColorGradingSettings.Contrast), 0.0f);
        }

        return camera;
    }

    /// <summary>
    /// Fits the clip planes to the bounding sphere of the content. A camera inside the sphere uses the smallest near plane.
    /// </summary>
    private void ResolveClipPlanes(Vector3 cameraPosition, out float nearPlane, out float farPlane)
    {
        float distance = Vector3.Distance(cameraPosition, _contentCenter);
        farPlane = distance + _contentRadius * 2.0f + 1.0f;
        nearPlane = distance > _contentRadius
            ? MathF.Max(MinimumNearPlane, (distance - _contentRadius) * 0.5f)
            : MinimumNearPlane;
    }

    private void CreateResources(AbstractRenderer renderer)
    {
        if (((IRuntimeRendererHost)renderer).TryGetBackendCapability<IRendererStartupWarmupBackendCapability>(out var warmup) && warmup is not null)
            warmup.BoostMeshGenerationBudgetUntilDrained(MeshGenerationBudgetMilliseconds);

        _colorTexture = XRTexture2D.CreateFrameBufferTexture(
            (uint)_width,
            (uint)_height,
            EPixelInternalFormat.Rgba16f,
            EPixelFormat.Rgba,
            EPixelType.Float,
            EFrameBufferAttachment.ColorAttachment0);
        _colorTexture.Name = "GaussianBakeCaptureColor";
        _colorTexture.SizedInternalFormat = ESizedInternalFormat.Rgba16f;

        _depthBuffer = new XRRenderBuffer((uint)_width, (uint)_height, ERenderBufferStorage.Depth24Stencil8)
        {
            Name = "GaussianBakeCaptureDepth",
        };

        _frameBuffer = new XRFrameBuffer(
            (_colorTexture, EFrameBufferAttachment.ColorAttachment0, 0, -1),
            (_depthBuffer, EFrameBufferAttachment.DepthStencilAttachment, 0, -1))
        {
            Name = "GaussianBakeCapture",
        };

        _viewport = new XRViewport(null, (uint)_width, (uint)_height)
        {
            PipelineRequest = RenderPipelineRequest.OffscreenCapture(),
            SetRenderPipelineFromCamera = false,
            AllowUIRender = false,
            AutomaticallyCollectVisible = false,
            AutomaticallySwapBuffers = false,
            AllowAutomaticInternalResolution = false,
            CullWithFrustum = true,
            RenderPipeline = RuntimeEngine.Rendering.NewOffscreenCaptureRenderPipeline(),
        };

        // The pipeline instance gives its mesh command collection the passes and the owner of the assigned
        // pipeline. This is the configuration that the impostor generator applies to its own collection.
        _commands = _viewport.RenderPipelineInstance.MeshRenderCommands;
    }

    private bool HasPendingGpuWork(AbstractRenderer renderer, out int pendingMeshes, out bool pendingPrograms)
    {
        pendingMeshes = 0;
        pendingPrograms = false;
        if (!((IRuntimeRendererHost)renderer).TryGetBackendCapability<IRendererStartupWarmupBackendCapability>(out var warmup) || warmup is null)
            return false;

        pendingMeshes = warmup.PendingStartupWorkCount;
        pendingPrograms = warmup.HasPendingAsyncPrograms;
        return pendingMeshes > 0 || pendingPrograms;
    }

    private static void FlipRows(float[] rgba, int width, int height)
    {
        int rowLength = width * 4;
        float[] row = ArrayPool<float>.Shared.Rent(rowLength);
        try
        {
            Span<float> scratch = row.AsSpan(0, rowLength);
            for (int top = 0, bottom = height - 1; top < bottom; top++, bottom--)
            {
                Span<float> topRow = rgba.AsSpan(top * rowLength, rowLength);
                Span<float> bottomRow = rgba.AsSpan(bottom * rowLength, rowLength);
                topRow.CopyTo(scratch);
                bottomRow.CopyTo(topRow);
                scratch.CopyTo(bottomRow);
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(row);
        }
    }

    private void Release()
    {
        if (_viewport is not null)
        {
            _viewport.MeshRenderCommandsOverride = null;
            _viewport.WorldInstanceOverride = null;
            _viewport.Destroy();
            _viewport = null;
        }

        _frameBuffer?.Destroy();
        _frameBuffer = null;
        _colorTexture?.Destroy();
        _colorTexture = null;
        _depthBuffer?.Destroy();
        _depthBuffer = null;
        _commands = null;
    }
}
