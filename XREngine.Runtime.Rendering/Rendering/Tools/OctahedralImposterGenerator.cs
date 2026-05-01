using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using XREngine.Components.Scene.Mesh;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Diagnostics;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.PostProcessing;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering.Tools;

public enum EOctahedralImposterCaptureMode
{
    Model,
    SelectedSubmeshes,
}

public enum EOctahedralImposterDepthMode
{
    None,
}

/// <summary>
/// Persistent metadata and texture references for a baked directional octahedral billboard.
/// </summary>
public sealed class OctahedralBillboardAsset : XRAsset
{
    public const int CurrentVersion = 1;
    public const string ColorSamplerName = "ImposterViews";

    private int _version = CurrentVersion;
    private XRTexture2DArray? _colorViews;
    private XRTexture2DArray? _depthViews;
    private Vector3[] _captureDirections = [];
    private int[] _sourceSubmeshIndices = [];
    private EOctahedralImposterCaptureMode _captureMode;
    private EOctahedralImposterDepthMode _depthMode = EOctahedralImposterDepthMode.None;
    private uint _captureResolution;
    private float _capturePadding = 1.15f;
    private float _orthographicExtent;
    private Vector3 _captureCenterWorld;
    private Vector3 _captureCenterLocal;
    private AABB _worldBounds;
    private AABB _localBounds;
    private Vector2 _billboardSize = Vector2.One;
    private int _sourceLayer;
    private bool _sourceCastsShadows;
    private string? _sourceModelPath;
    private string? _sourceMaterialMode;
    private string? _rendererBackend;
    private string? _qualityNotes;

    public int Version
    {
        get => _version;
        set => SetField(ref _version, value);
    }

    public XRTexture2DArray? ColorViews
    {
        get => _colorViews;
        set
        {
            if (value is not null)
                ConfigureColorViewTexture(value);
            SetField(ref _colorViews, value);
        }
    }

    public XRTexture2DArray? DepthViews
    {
        get => _depthViews;
        set => SetField(ref _depthViews, value);
    }

    public Vector3[] CaptureDirections
    {
        get => _captureDirections;
        set => SetField(ref _captureDirections, value ?? []);
    }

    public int[] SourceSubmeshIndices
    {
        get => _sourceSubmeshIndices;
        set => SetField(ref _sourceSubmeshIndices, value ?? []);
    }

    public EOctahedralImposterCaptureMode CaptureMode
    {
        get => _captureMode;
        set => SetField(ref _captureMode, value);
    }

    public EOctahedralImposterDepthMode DepthMode
    {
        get => _depthMode;
        set => SetField(ref _depthMode, value);
    }

    public uint CaptureResolution
    {
        get => _captureResolution;
        set => SetField(ref _captureResolution, value);
    }

    public float CapturePadding
    {
        get => _capturePadding;
        set => SetField(ref _capturePadding, value);
    }

    public float OrthographicExtent
    {
        get => _orthographicExtent;
        set => SetField(ref _orthographicExtent, value);
    }

    public Vector3 CaptureCenterWorld
    {
        get => _captureCenterWorld;
        set => SetField(ref _captureCenterWorld, value);
    }

    public Vector3 CaptureCenterLocal
    {
        get => _captureCenterLocal;
        set => SetField(ref _captureCenterLocal, value);
    }

    public AABB WorldBounds
    {
        get => _worldBounds;
        set => SetField(ref _worldBounds, value);
    }

    public AABB LocalBounds
    {
        get => _localBounds;
        set => SetField(ref _localBounds, value);
    }

    public Vector2 BillboardSize
    {
        get => _billboardSize;
        set => SetField(ref _billboardSize, value);
    }

    public int SourceLayer
    {
        get => _sourceLayer;
        set => SetField(ref _sourceLayer, Math.Clamp(value, 0, 31));
    }

    public bool SourceCastsShadows
    {
        get => _sourceCastsShadows;
        set => SetField(ref _sourceCastsShadows, value);
    }

    public string? SourceModelPath
    {
        get => _sourceModelPath;
        set => SetField(ref _sourceModelPath, value);
    }

    public string? SourceMaterialMode
    {
        get => _sourceMaterialMode;
        set => SetField(ref _sourceMaterialMode, value);
    }

    public string? RendererBackend
    {
        get => _rendererBackend;
        set => SetField(ref _rendererBackend, value);
    }

    public string? QualityNotes
    {
        get => _qualityNotes;
        set => SetField(ref _qualityNotes, value);
    }

    internal static void ConfigureColorViewTexture(XRTexture2DArray texture)
    {
        texture.Name ??= "OctahedralImpostorViews";
        texture.SamplerName = ColorSamplerName;
        texture.MinFilter = ETexMinFilter.Linear;
        texture.MagFilter = ETexMagFilter.Linear;
        texture.UWrap = ETexWrapMode.ClampToEdge;
        texture.VWrap = ETexWrapMode.ClampToEdge;
        texture.AutoGenerateMipmaps = true;
    }
}

/// <summary>
/// Utility for baking 26-view octahedral impostor textures from an existing <see cref="ModelComponent"/>.
/// </summary>
public sealed class OctahedralImposterGenerator
{
    private const string UnsupportedDepthMessage =
        "Depth output for octahedral billboards is deferred for v1. Captures use a depth buffer for correct rendering, but no depth texture asset is produced.";

    private static readonly Vector3[] s_captureDirections = BuildCaptureDirections();
    private static readonly object s_resourceLock = new();
    private static CaptureResources? s_resources;

    public static IReadOnlyList<Vector3> CaptureDirections => s_captureDirections;

    public sealed record Settings(
        uint SheetSize = 1024,
        float CapturePadding = 1.15f,
        bool CaptureDepth = false);

    public sealed record CaptureProgress(int CompletedViews, int TotalViews, string Message)
    {
        public float Normalized => TotalViews <= 0 ? 0.0f : Math.Clamp((float)CompletedViews / TotalViews, 0.0f, 1.0f);
    }

    public sealed record Result(
        OctahedralBillboardAsset Asset,
        XRTexture2DArray Views,
        XRTexture2DArray? DepthViews,
        AABB LocalBounds,
        AABB WorldBounds,
        Vector3 CaptureCenterWorld,
        Vector3 CaptureCenterLocal,
        float OrthographicExtent,
        IReadOnlyList<Vector3> CaptureDirections,
        IReadOnlyList<int> SourceSubmeshIndices,
        EOctahedralImposterCaptureMode CaptureMode,
        IReadOnlyList<XRTexture> PreviewTextures,
        string? Warning);

    public static Result? Generate(ModelComponent component, Settings settings)
        => Generate(component, settings, null);

    public static Result? Generate(ModelComponent component, Settings settings, IReadOnlyCollection<int>? submeshIndices)
    {
        Result? result = null;
        Exception? exception = null;

        if (Engine.IsRenderThread)
        {
            try
            {
                result = GenerateOnRenderThread(component, settings, submeshIndices, null, CancellationToken.None);
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        }
        else
        {
            using ManualResetEventSlim completionEvent = new(false);
            Engine.EnqueueMainThreadTask(() =>
            {
                try
                {
                    result = GenerateOnRenderThread(component, settings, submeshIndices, null, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    exception = ex;
                }
                finally
                {
                    completionEvent.Set();
                }
            }, "OctahedralImposterGenerator.Generate");
            completionEvent.Wait();
        }

        if (exception is not null)
            Debug.LogException(exception, "Octahedral impostor generation failed.");

        return result;
    }

    public static Task<Result?> GenerateAsync(
        ModelComponent component,
        Settings settings,
        IReadOnlyCollection<int>? submeshIndices = null,
        IProgress<CaptureProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(component);

        TaskCompletionSource<Result?> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Engine.EnqueueMainThreadTask(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                tcs.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                Result? result = GenerateOnRenderThread(component, settings, submeshIndices, progress, cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                    tcs.TrySetCanceled(cancellationToken);
                else
                    tcs.TrySetResult(result);
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetCanceled(cancellationToken);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, "Octahedral impostor generation failed.");
                tcs.TrySetException(ex);
            }
        }, "OctahedralImposterGenerator.GenerateAsync");

        return tcs.Task;
    }

    private static Result? GenerateOnRenderThread(
        ModelComponent component,
        Settings settings,
        IReadOnlyCollection<int>? submeshIndices,
        IProgress<CaptureProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(component);

        Model? model = component.Model;
        if (model is null)
            return Fail("Cannot generate impostor: no model assigned.");

        IRuntimeRenderWorld? world = component.WorldAs<IRuntimeRenderWorld>();
        if (world is null)
            return Fail("Cannot generate impostor: component has no world.");

        RenderInfo3D[] targets = ResolveTargetRenderInfos(component, submeshIndices, out int[] normalizedIndices, out EOctahedralImposterCaptureMode captureMode);
        if (targets.Length == 0)
            return Fail("Cannot generate impostor: no renderable meshes matched the capture request.");

        AABB worldBounds = CalculateCombinedWorldBounds(component, model, normalizedIndices);
        if (!worldBounds.IsValid)
            return Fail("Cannot generate impostor: model has no valid bounds.");

        AABB localBounds = CalculateCombinedLocalBounds(component, model, normalizedIndices, worldBounds);
        Vector3 captureCenterWorld = worldBounds.Center;
        Vector3 captureCenterLocal = TransformPointToLocal(component, captureCenterWorld);

        float orthographicExtent = CalculateOrthographicExtent(worldBounds, settings.CapturePadding);
        if (!float.IsFinite(orthographicExtent) || orthographicExtent <= 0.0f)
            return Fail("Cannot generate impostor: calculated capture extent is invalid.");

        string? warning = settings.CaptureDepth ? UnsupportedDepthMessage : null;
        if (warning is not null)
            Debug.LogWarning(warning);

        XRTexture2DArray colorArray = CreateColorArray(settings.SheetSize);
        XRRenderBuffer depthBuffer = new(settings.SheetSize, settings.SheetSize, ERenderBufferStorage.Depth24Stencil8)
        {
            Name = "OctahedralImpostorDepthBuffer"
        };

        XRFrameBuffer[] framebuffers = BuildLayerFramebuffers(colorArray, depthBuffer);
        XRTexture[] previewTextures = BuildPreviewTextures(colorArray);
        CaptureResources resources = GetCaptureResources(settings.SheetSize);
        ConfigureCommandCollection(resources);

        try
        {
            progress?.Report(new CaptureProgress(0, s_captureDirections.Length, "Starting octahedral capture."));

            for (int i = 0; i < s_captureDirections.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                bool ok = CaptureViewLayer(
                    resources,
                    world,
                    targets,
                    captureCenterWorld,
                    orthographicExtent,
                    settings.SheetSize,
                    s_captureDirections[i],
                    i,
                    framebuffers[i]);
                if (!ok)
                    return Fail($"Octahedral impostor capture failed for view {i}.");

                SynchronizeCaptureTextureWrites();
                progress?.Report(new CaptureProgress(i + 1, s_captureDirections.Length, $"Captured view {i + 1} of {s_captureDirections.Length}."));
            }

            SynchronizeCaptureTextureWrites();
            PopulateCpuMipmapsFromGpu(colorArray);
        }
        finally
        {
            resources.Viewport.MeshRenderCommandsOverride = null;
        }

        var asset = new OctahedralBillboardAsset
        {
            Name = BuildDefaultAssetName(model, normalizedIndices),
            Version = OctahedralBillboardAsset.CurrentVersion,
            ColorViews = colorArray,
            DepthViews = null,
            CaptureDirections = [.. s_captureDirections],
            SourceSubmeshIndices = normalizedIndices,
            CaptureMode = captureMode,
            DepthMode = EOctahedralImposterDepthMode.None,
            CaptureResolution = settings.SheetSize,
            CapturePadding = settings.CapturePadding,
            OrthographicExtent = orthographicExtent,
            CaptureCenterWorld = captureCenterWorld,
            CaptureCenterLocal = captureCenterLocal,
            WorldBounds = worldBounds,
            LocalBounds = localBounds,
            BillboardSize = new Vector2(orthographicExtent * 2.0f, orthographicExtent * 2.0f),
            SourceLayer = ResolveSourceLayer(targets),
            SourceCastsShadows = ResolveSourceCastsShadows(targets),
            SourceModelPath = model.FilePath,
            SourceMaterialMode = ResolveSourceMaterialMode(targets),
            RendererBackend = AbstractRenderer.Current?.GetType().Name,
            QualityNotes = "V1 uses directional layer blending without depth/parallax reprojection."
        };

        progress?.Report(new CaptureProgress(s_captureDirections.Length, s_captureDirections.Length, "Octahedral capture complete."));

        return new Result(
            asset,
            colorArray,
            null,
            localBounds,
            worldBounds,
            captureCenterWorld,
            captureCenterLocal,
            orthographicExtent,
            s_captureDirections,
            normalizedIndices,
            captureMode,
            previewTextures,
            warning);
    }

    private static Result? Fail(string message)
    {
        Debug.LogWarning(message);
        return null;
    }

    private static XRTexture2DArray CreateColorArray(uint sheetSize)
    {
        XRTexture2DArray viewArray = XRTexture2DArray.CreateFrameBufferTexture(
            (uint)s_captureDirections.Length,
            sheetSize,
            sheetSize,
            EPixelInternalFormat.Rgba16f,
            EPixelFormat.Rgba,
            EPixelType.Float,
            EFrameBufferAttachment.ColorAttachment0);

        viewArray.Name = "OctahedralImpostorViews";
        viewArray.SizedInternalFormat = viewArray.Textures.Length > 0
            ? viewArray.Textures[0].SizedInternalFormat
            : ESizedInternalFormat.Rgba16f;
        OctahedralBillboardAsset.ConfigureColorViewTexture(viewArray);
        viewArray.PushData();
        return viewArray;
    }

    private static XRFrameBuffer[] BuildLayerFramebuffers(XRTexture2DArray colorArray, XRRenderBuffer depthBuffer)
    {
        XRFrameBuffer[] framebuffers = new XRFrameBuffer[s_captureDirections.Length];
        for (int i = 0; i < framebuffers.Length; i++)
        {
            framebuffers[i] = new XRFrameBuffer(
                (colorArray, EFrameBufferAttachment.ColorAttachment0, 0, i),
                (depthBuffer, EFrameBufferAttachment.DepthStencilAttachment, 0, -1))
            {
                Name = $"OctahedralImpostorView{i}"
            };
        }

        return framebuffers;
    }

    private static XRTexture[] BuildPreviewTextures(XRTexture2DArray colorArray)
    {
        XRTexture[] views = new XRTexture[colorArray.Depth];
        for (uint i = 0; i < colorArray.Depth; i++)
        {
            views[i] = new XRTexture2DArrayView(
                colorArray,
                0u,
                1u,
                i,
                1u,
                colorArray.SizedInternalFormat,
                array: false,
                multisample: false)
            {
                Name = $"OctahedralImpostorView{i}",
                SamplerName = OctahedralBillboardAsset.ColorSamplerName,
                MinFilter = ETexMinFilter.Linear,
                MagFilter = ETexMagFilter.Linear,
                UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge
            };
        }

        return views;
    }

    private static CaptureResources GetCaptureResources(uint resolution)
    {
        lock (s_resourceLock)
        {
            s_resources ??= new CaptureResources();
            s_resources.Configure(resolution);
            return s_resources;
        }
    }

    private static void ConfigureCommandCollection(CaptureResources resources)
    {
        RenderPipeline? pipeline = resources.Viewport.RenderPipeline;
        if (pipeline is null)
            return;

        resources.Commands.SetRenderPasses(pipeline.PassIndicesAndSorters, pipeline.PassMetadata);
        resources.Commands.SetOwnerPipeline(resources.Viewport.RenderPipelineInstance);
    }

    private static bool CaptureViewLayer(
        CaptureResources resources,
        IRuntimeRenderWorld world,
        IReadOnlyList<RenderInfo3D> targets,
        Vector3 captureCenter,
        float orthographicExtent,
        uint resolution,
        Vector3 axis,
        int viewIndex,
        XRFrameBuffer fbo)
    {
        XRCamera camera = BuildCaptureCamera(captureCenter, orthographicExtent, axis);
        XRViewport viewport = resources.Viewport;
        RenderCommandCollection commands = resources.Commands;
        viewport.Camera = camera;
        viewport.Resize(resolution, resolution);
        viewport.WorldInstanceOverride = world;
        viewport.MeshRenderCommandsOverride = commands;

        Frustum frustum = camera.WorldFrustum();
        for (int i = 0; i < targets.Count; i++)
        {
            RenderInfo3D target = targets[i];
            if (!target.IsVisible || !target.ShouldRender)
                continue;

            if (target.AllowRender(frustum, commands, camera, containsOnly: false, collectMirrors: false))
                target.CollectCommands(commands, camera);
        }

        viewport.SwapBuffers(commands, allowScreenSpaceUISwap: false);

        if (!fbo.IsLastCheckComplete)
        {
            Debug.LogWarning($"Octahedral impostor capture skipped view {viewIndex}: framebuffer is incomplete.");
            return false;
        }

        using StateObject passScope = VPRCRenderTargetHelpers.PushSceneCapturePass();
        viewport.Render(fbo, world, camera);

        if (!fbo.IsLastCheckComplete)
        {
            Debug.LogWarning($"Octahedral impostor capture failed view {viewIndex}: framebuffer became incomplete during render.");
            return false;
        }

        return true;
    }

    private static XRCamera BuildCaptureCamera(Vector3 captureCenter, float orthographicExtent, Vector3 axis)
    {
        Vector3 normalizedAxis = Vector3.Normalize(axis);
        float eyeDistance = orthographicExtent * 2.0f;
        Vector3 eye = captureCenter + normalizedAxis * eyeDistance;

        Vector3 up = MathF.Abs(Vector3.Dot(normalizedAxis, Vector3.UnitY)) > 0.95f
            ? Vector3.UnitZ
            : Vector3.UnitY;

        Matrix4x4 viewOrientation = Matrix4x4.CreateLookAt(eye, captureCenter, up);
        if (!Matrix4x4.Invert(viewOrientation, out Matrix4x4 invView))
            invView = Matrix4x4.Identity;

        Quaternion rotation = Quaternion.CreateFromRotationMatrix(invView);
        Transform cameraTransform = new(eye, rotation);
        cameraTransform.RecalculateMatrices(true, true);

        XROrthographicCameraParameters cameraParameters = new(
            orthographicExtent * 2.0f,
            orthographicExtent * 2.0f,
            0.01f,
            eyeDistance + orthographicExtent * 2.0f);
        cameraParameters.SetOriginCentered();

        XRCamera camera = new(cameraTransform, cameraParameters)
        {
            PostProcessStates = new CameraPostProcessStateCollection(),
            CullingMask = LayerMask.Everything,
            OutputHDROverride = true,
            AntiAliasingModeOverride = EAntiAliasingMode.None,
            MsaaSampleCountOverride = 1u,
            TsrRenderScaleOverride = 1.0f
        };

        var colorStage = camera.GetPostProcessStageState<ColorGradingSettings>();
        if (colorStage?.TryGetBacking(out ColorGradingSettings? grading) == true && grading is not null)
        {
            grading.AutoExposure = false;
            grading.Exposure = 1.0f;
        }
        else
        {
            colorStage?.SetValue(nameof(ColorGradingSettings.AutoExposure), false);
            colorStage?.SetValue(nameof(ColorGradingSettings.Exposure), 1.0f);
        }

        return camera;
    }

    private static void SynchronizeCaptureTextureWrites()
    {
        AbstractRenderer? renderer = AbstractRenderer.Current;
        if (renderer is null)
            return;

        if (renderer is OpenGLRenderer)
        {
            renderer.MemoryBarrier(
                EMemoryBarrierMask.Framebuffer |
                EMemoryBarrierMask.TextureFetch |
                EMemoryBarrierMask.TextureUpdate);
        }
        else
        {
            renderer.WaitForGpu();
        }
    }

    private static void PopulateCpuMipmapsFromGpu(XRTexture2DArray colorArray)
    {
        AbstractRenderer? renderer = AbstractRenderer.Current;
        if (renderer is null)
            return;

        for (int layer = 0; layer < colorArray.Textures.Length; layer++)
        {
            if (!renderer.TryReadTextureMipRgbaFloat(colorArray, 0, layer, out float[]? rgbaFloats, out int width, out int height, out string failure) ||
                rgbaFloats is null ||
                width <= 0 ||
                height <= 0)
            {
                Debug.LogWarning($"Could not read octahedral impostor layer {layer} for asset persistence: {failure}");
                continue;
            }

            XRTexture2D slice = colorArray.Textures[layer];
            slice.Name = $"OctahedralImpostorView{layer}";
            slice.SamplerName = OctahedralBillboardAsset.ColorSamplerName;
            slice.SizedInternalFormat = colorArray.SizedInternalFormat;
            slice.MinFilter = ETexMinFilter.Linear;
            slice.MagFilter = ETexMagFilter.Linear;
            slice.UWrap = ETexWrapMode.ClampToEdge;
            slice.VWrap = ETexWrapMode.ClampToEdge;
            slice.AutoGenerateMipmaps = true;
            slice.Mipmaps =
            [
                new Mipmap2D
                {
                    Width = (uint)width,
                    Height = (uint)height,
                    InternalFormat = EPixelInternalFormat.Rgba16f,
                    PixelFormat = EPixelFormat.Rgba,
                    PixelType = EPixelType.Float,
                    Data = DataSource.FromArray(rgbaFloats)
                }
            ];
        }
    }

    private static RenderInfo3D[] ResolveTargetRenderInfos(
        ModelComponent component,
        IReadOnlyCollection<int>? submeshIndices,
        out int[] normalizedIndices,
        out EOctahedralImposterCaptureMode captureMode)
    {
        if (submeshIndices is { Count: > 0 })
        {
            SortedSet<int> valid = [];
            foreach (int index in submeshIndices)
                if ((uint)index < (uint)component.Meshes.Count)
                    valid.Add(index);

            normalizedIndices = [.. valid];
            captureMode = EOctahedralImposterCaptureMode.SelectedSubmeshes;
            RenderInfo3D[] targets = new RenderInfo3D[normalizedIndices.Length];
            for (int i = 0; i < normalizedIndices.Length; i++)
                targets[i] = component.Meshes[normalizedIndices[i]].RenderInfo;
            return targets;
        }

        normalizedIndices = [];
        captureMode = EOctahedralImposterCaptureMode.Model;
        List<RenderInfo3D> allTargets = [];
        foreach (RenderInfo ri in component.RenderedObjects)
        {
            if (ri is RenderInfo3D ri3d)
                allTargets.Add(ri3d);
        }

        return [.. allTargets];
    }

    private static AABB CalculateCombinedModelBounds(Model model, IReadOnlyList<int> submeshIndices)
    {
        AABB total = new();
        if (submeshIndices.Count > 0)
        {
            for (int i = 0; i < submeshIndices.Count; i++)
            {
                int index = submeshIndices[i];
                if ((uint)index >= (uint)model.Meshes.Count)
                    continue;

                SubMesh mesh = model.Meshes[index];
                total = AABB.Union(total, mesh.CullingBounds ?? mesh.Bounds);
            }

            return total;
        }

        foreach (SubMesh mesh in model.Meshes)
            total = AABB.Union(total, mesh.CullingBounds ?? mesh.Bounds);
        return total;
    }

    private static AABB CalculateCombinedWorldBounds(ModelComponent component, Model model, IReadOnlyList<int> submeshIndices)
    {
        AABB total = new();
        if (submeshIndices.Count > 0)
        {
            for (int i = 0; i < submeshIndices.Count; i++)
            {
                int index = submeshIndices[i];
                if ((uint)index >= (uint)component.Meshes.Count)
                    continue;

                if (component.Meshes[index].TryGetWorldBounds(out AABB worldBounds))
                    total = AABB.Union(total, worldBounds);
            }
        }
        else
        {
            foreach (RenderableMesh mesh in component.Meshes)
            {
                if (mesh.TryGetWorldBounds(out AABB worldBounds))
                    total = AABB.Union(total, worldBounds);
            }
        }

        if (total.IsValid)
            return total;

        AABB modelBounds = CalculateCombinedModelBounds(model, submeshIndices);
        return TransformBoundsToWorld(modelBounds, component.Transform);
    }

    private static AABB CalculateCombinedLocalBounds(ModelComponent component, Model model, IReadOnlyList<int> submeshIndices, AABB worldBounds)
    {
        AABB modelBounds = CalculateCombinedModelBounds(model, submeshIndices);
        if (modelBounds.IsValid)
            return modelBounds;

        if (!Matrix4x4.Invert(component.Transform.RenderMatrix, out Matrix4x4 worldToLocal))
            return worldBounds;

        return worldBounds.Transformed(point => Vector3.Transform(point, worldToLocal));
    }

    private static AABB TransformBoundsToWorld(AABB bounds, TransformBase transform)
    {
        Matrix4x4 renderMatrix = transform.RenderMatrix;
        return bounds.Transformed(point => Vector3.Transform(point, renderMatrix));
    }

    private static Vector3 TransformPointToLocal(ModelComponent component, Vector3 point)
    {
        if (!Matrix4x4.Invert(component.Transform.RenderMatrix, out Matrix4x4 worldToLocal))
            return Vector3.Zero;

        return Vector3.Transform(point, worldToLocal);
    }

    private static float CalculateOrthographicExtent(AABB bounds, float padding)
    {
        Vector3 halfExtents = bounds.Size * 0.5f;
        float maxHalfExtent = MathF.Max(halfExtents.X, MathF.Max(halfExtents.Y, halfExtents.Z));
        return MathF.Max(0.01f, maxHalfExtent * MathF.Max(1.0f, padding));
    }

    private static int ResolveSourceLayer(IReadOnlyList<RenderInfo3D> targets)
        => targets.Count > 0 ? targets[0].Layer : 0;

    private static bool ResolveSourceCastsShadows(IReadOnlyList<RenderInfo3D> targets)
    {
        for (int i = 0; i < targets.Count; i++)
            if (targets[i].CastsShadows)
                return true;
        return false;
    }

    private static string ResolveSourceMaterialMode(IReadOnlyList<RenderInfo3D> targets)
        => targets.Count == 1
            ? "Single source renderable material commands"
            : "Multiple source renderable material commands";

    private static string BuildDefaultAssetName(Model model, IReadOnlyList<int> submeshIndices)
    {
        string baseName = string.IsNullOrWhiteSpace(model.Name)
            ? "Model"
            : model.Name!;

        return submeshIndices.Count == 0
            ? $"{baseName}_OctahedralBillboard"
            : $"{baseName}_Submeshes_{string.Join("_", submeshIndices)}_OctahedralBillboard";
    }

    private static Vector3[] BuildCaptureDirections()
    {
        List<Vector3> directions = new(26)
        {
            Vector3.UnitX,
            -Vector3.UnitX,
            Vector3.UnitY,
            -Vector3.UnitY,
            Vector3.UnitZ,
            -Vector3.UnitZ
        };

        Vector3[] edgeSamples =
        [
            new(1, 1, 0),
            new(1, -1, 0),
            new(-1, 1, 0),
            new(-1, -1, 0),
            new(1, 0, 1),
            new(1, 0, -1),
            new(-1, 0, 1),
            new(-1, 0, -1),
            new(0, 1, 1),
            new(0, 1, -1),
            new(0, -1, 1),
            new(0, -1, -1)
        ];

        foreach (Vector3 raw in edgeSamples)
            directions.Add(Vector3.Normalize(raw));

        for (int xSign = -1; xSign <= 1; xSign += 2)
            for (int ySign = -1; ySign <= 1; ySign += 2)
                for (int zSign = -1; zSign <= 1; zSign += 2)
                    directions.Add(Vector3.Normalize(new Vector3(xSign, ySign, zSign)));

        return [.. directions];
    }

    private sealed class CaptureResources
    {
        public XRViewport Viewport { get; } = new(null, 1u, 1u)
        {
            SetRenderPipelineFromCamera = false,
            AllowUIRender = false,
            AutomaticallyCollectVisible = false,
            AutomaticallySwapBuffers = false,
            CullWithFrustum = true,
            RenderPipeline = Engine.Rendering.NewRenderPipeline()
        };

        public RenderCommandCollection Commands { get; } = new();

        public void Configure(uint resolution)
        {
            uint safeResolution = Math.Max(1u, resolution);
            if (Viewport.Width != safeResolution || Viewport.Height != safeResolution)
                Viewport.Resize(safeResolution, safeResolution);

            Viewport.RenderPipeline ??= Engine.Rendering.NewRenderPipeline();
        }
    }
}
