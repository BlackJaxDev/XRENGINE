using System.Numerics;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Browser;

/// <summary>Owns one real scene, its browser GPU resources, clock and batched frame submission.</summary>
public sealed partial class BrowserSceneSession : IDisposable
{
    private const double FixedStep = 1.0 / 60.0;
    private const int MaxStepsPerFrame = 4;
    private const int MaxInstances = 256;
    private static readonly BrowserPipelineQualitySettings LowQuality = new()
    {
        ResolutionScale = 0.75f, MaxDevicePixelRatio = 1, ShadowResolution = 512, ShadowUpdateInterval = 2,
        MaxTextureDimension = 1024
    };
    private static readonly BrowserPipelineQualitySettings BalancedQuality = new()
    {
        MaxDevicePixelRatio = 1.5f, Hdr = true, ToneMap = "reinhard", MaxTextureDimension = 2048
    };
    private static readonly BrowserPipelineQualitySettings HighQuality = new()
    {
        ShadowResolution = 2048, Hdr = true, ToneMap = "reinhard", MaxTextureDimension = 4096
    };

    private readonly RuntimeSceneHost _host;
    private readonly IBrowserRendererHost _renderer;
    private readonly BrowserSceneSnapshot? _snapshot;
    private readonly SceneNode _parent;
    private readonly BrowserRenderPipeline _pipeline = new();
    private readonly BrowserUploadBatch _uploads = new();
    private readonly byte[] _streamedCheckerPixels = new byte[8 * 8 * 4];
    private bool _checkerAlternate;
    private long _frameAttempts;
    private long _submittedFrames;
    private long _lastFrameAllocatedBytes;
    private long _frameAllocatedBytes;
    private int _lastFramePacketBytes;
    private BrowserCollectedRenderable[] _collected = new BrowserCollectedRenderable[64];
    private int _collectedCount;
    private readonly List<BrowserMeshComponent> _renderables = new();
    private readonly List<BrowserMeshComponent> _customRenderables = new();
    private readonly Dictionary<BrowserMeshData, int> _meshes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<BrowserTextureData, int> _textures = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<BrowserMaterialData, int> _materials = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<BrowserMeshData, int> _meshReferences = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<BrowserTextureData, int> _textureReferences = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<BrowserMaterialData, int> _materialReferences = new(ReferenceEqualityComparer.Instance);
    private BrowserMeshData? _cubeMesh;
    private BrowserMeshData? _panelMesh;
    private BrowserMaterialData? _checkerMaterial;
    private BrowserMaterialData? _panelMaterial;
    private BrowserMaterialData? _transparentMaterial;
    private bool _uiEnabled = true;
    private string _qualityPreset = "balanced";
    private BrowserPipelineQualitySettings _quality = BalancedQuality;
    private BrowserPipelineEnvironment _environment = BrowserPipelineEnvironment.Default with
    {
        ShadowViewProjection = Matrix4x4.CreateLookAt(new Vector3(6, 8, 1),
            new Vector3(0, 0, -7), Vector3.UnitY) * Matrix4x4.CreateOrthographic(18, 18, 0.1f, 40),
        LightDirection = Vector3.Normalize(new Vector3(6, 8, 8))
    };
    private BrowserViewport _left;
    private BrowserViewport _right;
    private int _instanceCount = 16;
    private bool _graphicsInitialized;
    private bool _splitView;
    private bool _cullingEnabled = true;
    private BrowserCameraSnapshot? _cameraOverride;
    private double _accumulator;
    private double _variableDeltaSeconds;
    private uint _historyGeneration = 1;
    private bool _frameInProgress;
    private bool _disposed;

    public BrowserSceneSession(int id, string canvasId, BrowserSceneSnapshot? snapshot = null)
    {
        Id = id;
        _snapshot = snapshot;
        Target = new BrowserCanvasRenderTarget(canvasId);
        _renderer = BrowserRendererComposition.CreateRequired(Target);
        _host = new RuntimeSceneHost();
        try
        {
            if (snapshot is not null)
            {
                _parent = new SceneNode("ImportedBrowserScene",
                    BrowserStaticRegistrations.CreateRequiredTransform(BrowserStaticRegistrations.TransformId));
                _host.RootNodes.Add(_parent);
                for (int i = 0; i < snapshot.Instances.Count; i++)
                {
                    BrowserSceneInstance instance = snapshot.Instances[i];
                    AddRenderableCore(snapshot.Meshes[instance.MeshIndex], snapshot.Materials[instance.MaterialIndex],
                        ImportTransform(instance.ModelMatrix), _renderables);
                }
                _host.Start();
                ReservePacket();
                return;
            }

            _cubeMesh = CreateCubeMesh();
            _panelMesh = BrowserStaticRegistrations.CreateMesh(BrowserStaticRegistrations.MeshId,
                [-0.43f, -0.43f, 0, 0, 1, 0.43f, -0.43f, 0, 1, 1,
                    0.43f, 0.43f, 0, 1, 0, -0.43f, 0.43f, 0, 0, 0],
                [0, 1, 2, 0, 2, 3]);
            byte[] pixels = new byte[8 * 8 * 4];
            FillDemoChecker(pixels, alternate: false);
            _checkerMaterial = new BrowserMaterialData(
                new Vector4(0.65f, 0.83f, 1, 1),
                BrowserStaticRegistrations.CreateTexture(BrowserStaticRegistrations.TextureId, 8, 8, pixels),
                shading: "lambert");
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                    pixels[(y * 8 + x) * 4 + 3] = ((x ^ y) & 1) == 0 ? (byte)255 : (byte)0;
            _panelMaterial = new BrowserMaterialData(new Vector4(1, 0.37f, 0.2f, 1),
                BrowserStaticRegistrations.CreateTexture(BrowserStaticRegistrations.TextureId, 8, 8, pixels),
                alphaMode: "masked", shading: "lambert");
            _transparentMaterial = new BrowserMaterialData(new Vector4(0.2f, 1, 0.7f, 0.45f),
                alphaMode: "transparent", shading: "lambert", castShadow: false);

            Transform parentTransform = (Transform)BrowserStaticRegistrations.CreateRequiredTransform(
                BrowserStaticRegistrations.TransformId);
            parentTransform.Translation = new Vector3(0, 0, -2.5f);
            _parent = new SceneNode("BrowserInstances", parentTransform);
            _host.RootNodes.Add(_parent);
            BrowserSpinComponent spin = (BrowserSpinComponent)BrowserStaticRegistrations.AddRequiredComponent(
                BrowserStaticRegistrations.SpinComponentId, _parent);
            spin.Target = Target;
            for (int i = 0; i < _instanceCount; i++)
                AddDemoInstance(i);
            Transform backdrop = (Transform)BrowserStaticRegistrations.CreateRequiredTransform(
                BrowserStaticRegistrations.TransformId);
            backdrop.Translation = new Vector3(0, 0, -5.8f);
            backdrop.Scale = new Vector3(10, 10, 1);
            AddRenderableCore(_panelMesh, new BrowserMaterialData(new Vector4(0.55f, 0.58f, 0.65f, 1),
                shading: "lambert", castShadow: false), backdrop, _customRenderables);
            LayoutInstances();
            _host.Start();
        }
        catch
        {
            try
            {
                _host.Dispose();
                Data.Core.XRObjectBase.ProcessPendingDestructions();
            }
            finally
            {
                try { _renderer.Dispose(); }
                finally
                {
                    _pipeline.Dispose();
                    _uploads.Dispose();
                }
            }
            throw;
        }
    }

    public int Id { get; }
    public BrowserCanvasRenderTarget Target { get; }
    /// <summary>Whether conservative per-viewport frustum rejection is enabled.</summary>
    public bool CullingEnabled => _cullingEnabled;
    /// <summary>Renderable and viewport pairs inspected in the most recent submitted frame.</summary>
    public int VisibilityCandidates { get; private set; }
    /// <summary>Renderable and viewport pairs rejected by the frustum in the most recent submitted frame.</summary>
    public int VisibilityCulled { get; private set; }
    /// <summary>Draws submitted in the most recent frame.</summary>
    public int VisibilityDrawn { get; private set; }
    public int RetainedMeshCount => _meshes.Count;
    public int RetainedMaterialCount => _materials.Count;
    public int RetainedTextureCount => _textures.Count;
    /// <summary>Elapsed render cadence, clamped independently of the fixed simulation steps.</summary>
    public double VariableDeltaSeconds => _variableDeltaSeconds;
    /// <summary>Changes whenever any future temporal consumer must discard its previous frame.</summary>
    public uint HistoryGeneration => _historyGeneration;
    public bool UiEnabled => _uiEnabled;
    public string QualityPreset => _qualityPreset;

    /// <summary>Applies an explicit mobile profile at a frame boundary, or defers it until graphics startup.</summary>
    public void SetQualityPreset(string preset)
    {
        ThrowIfFrameBusy();
        BrowserPipelineQualitySettings quality = preset switch
        {
            "low" => LowQuality,
            "balanced" => BalancedQuality,
            "high" => HighQuality,
            _ => throw new NotSupportedException("Browser quality preset must be low, balanced, or high.")
        };
        quality = quality with { UiEnabled = _uiEnabled };
        if (_graphicsInitialized)
            _renderer.ConfigurePipeline(quality);
        _qualityPreset = preset;
        _quality = quality;
        InvalidateHistory();
    }

    /// <summary>Defines the scene's directional shadow volume and ambient/sky lighting before collection.</summary>
    public void SetEnvironment(BrowserPipelineEnvironment environment)
    {
        ThrowIfFrameBusy();
        _environment = environment;
        InvalidateHistory();
    }

    /// <summary>Controls the portable GPU overlay; DOM text entry remains owned by the browser host.</summary>
    public void SetUiEnabled(bool enabled)
    {
        ThrowIfFrameBusy();
        BrowserPipelineQualitySettings quality = _quality with { UiEnabled = enabled };
        if (_graphicsInitialized)
            _renderer.ConfigurePipeline(quality);
        _quality = quality;
        _uiEnabled = enabled;
    }

    /// <summary>Allocates a counter snapshot only when explicitly requested by the host.</summary>
    public BrowserBridgeStatistics CaptureBridgeStatistics() => new(
        _frameAttempts, _submittedFrames, _lastFrameAllocatedBytes, _frameAllocatedBytes, _lastFramePacketBytes,
        _pipeline.Packet.DrawCapacity, _pipeline.Packet.GrowthCount, _uploads.CommandCapacity, _uploads.PayloadCapacity,
        _uploads.CommandGrowthCount, _uploads.PayloadGrowthCount);

    /// <summary>Streams pixels into a live texture without replacing its immutable resource descriptor.</summary>
    public void UploadTextureRegion(BrowserTextureData texture, int x, int y, int width, int height, ReadOnlySpan<byte> rgba)
    {
        ThrowIfFrameBusy();
        ArgumentNullException.ThrowIfNull(texture);
        if (!_graphicsInitialized || !_textures.TryGetValue(texture, out int handle))
            throw new InvalidOperationException("Texture uploads require a live texture owned by this scene.");
        if (x < 0 || y < 0 || width <= 0 || height <= 0 ||
            (long)x + width > texture.Width || (long)y + height > texture.Height ||
            (long)width * height * 4 != rgba.Length)
            throw new ArgumentOutOfRangeException(nameof(rgba), "Upload pixels must exactly cover a rectangle within the texture.");
        _uploads.EnsureCapacity(1, rgba.Length);
        _uploads.Begin(Id);
        try
        {
            _uploads.AddTexture(BrowserResourceHandle.FromPacked(handle), x, y, width, height, rgba);
            _uploads.Seal();
            _renderer.SubmitUploads(_uploads);
        }
        catch
        {
            _uploads.Abort();
            throw;
        }
    }

    /// <summary>Replaces the shared demo texture through the streaming lane while preserving its orientation markers.</summary>
    public void StreamDemoTexture()
    {
        ThrowIfFrameBusy();
        if (_snapshot is not null || _checkerMaterial?.Texture is not BrowserTextureData texture)
            throw new InvalidOperationException("The checker texture belongs to the built-in demo.");
        bool alternate = !_checkerAlternate;
        FillDemoChecker(_streamedCheckerPixels, alternate);
        UploadTextureRegion(texture, 0, 0, 8, 8, _streamedCheckerPixels);
        _checkerAlternate = alternate;
    }

    private static void FillDemoChecker(Span<byte> pixels, bool alternate)
    {
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                int offset = (y * 8 + x) * 4;
                byte shade = ((x ^ y) & 1) == 0 ? (byte)245 : (byte)48;
                pixels[offset] = alternate ? (byte)(255 - shade) : shade;
                pixels[offset + 1] = shade;
                pixels[offset + 2] = shade;
                pixels[offset + 3] = 255;
                // Distinct corners expose X/Y flips through mesh UVs and UI sampling.
                if ((x < 2 || x >= 6) && (y < 2 || y >= 6))
                {
                    bool right = x >= 6;
                    bool bottom = y >= 6;
                    pixels[offset] = right == bottom ? (byte)255 : (byte)0;
                    pixels[offset + 1] = right ? (byte)255 : (byte)0;
                    pixels[offset + 2] = bottom && !right ? (byte)255 : (byte)0;
                }
            }
    }

    public void SetCullingEnabled(bool enabled)
    {
        ThrowIfFrameBusy();
        _cullingEnabled = enabled;
    }

    /// <summary>Replaces the cached camera used by the scene; both matrices use row vectors and zero-to-one clip depth.</summary>
    public void SetCamera(BrowserCameraSnapshot camera)
    {
        ThrowIfFrameBusy();
        if (!IsFinite(camera.View) || !IsFinite(camera.Projection))
            throw new ArgumentException("Browser camera matrices must be finite.", nameof(camera));
        _cameraOverride = camera;
        RebuildViews();
    }

    /// <summary>Updates one main renderable's tint while retaining its existing texture descriptor.</summary>
    public void SetRenderableTint(int index, Vector4 tint)
    {
        ThrowIfFrameBusy();
        if ((uint)index >= (uint)_renderables.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        BrowserMeshComponent component = _renderables[index];
        BrowserMaterialData material = component.Material!;
        tint.W = material.Tint.W;
        ReplaceRenderableResources(component, component.Mesh!, material.WithTint(tint));
    }

    /// <summary>Changes a scene-owned component's descriptors after acquiring any required GPU resources.</summary>
    public void ReplaceRenderableResources(BrowserMeshComponent component, BrowserMeshData mesh, BrowserMaterialData material)
    {
        ThrowIfFrameBusy();
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(material);
        if (!_renderables.Contains(component) && !_customRenderables.Contains(component))
            throw new ArgumentException("Renderable does not belong to this scene.", nameof(component));
        BrowserMeshData oldMesh = component.Mesh!;
        BrowserMaterialData oldMaterial = component.Material!;
        if (ReferenceEquals(mesh, oldMesh) && ReferenceEquals(material, oldMaterial))
            return;
        DiscardCollection();
        int meshHandle = 0;
        int materialHandle = 0;
        if (_graphicsInitialized)
        {
            meshHandle = AcquireMesh(mesh);
            try { materialHandle = AcquireMaterial(material); }
            catch { ReleaseMesh(mesh); throw; }
        }
        component.Mesh = mesh;
        component.Material = material;
        component.MeshHandle = meshHandle;
        component.MaterialHandle = materialHandle;
        if (_graphicsInitialized)
        {
            try { ReleaseMaterial(oldMaterial); }
            finally { ReleaseMesh(oldMesh); }
        }
    }

    /// <summary>Stops submitting a scene-owned renderable and releases its shared GPU references.</summary>
    public void RemoveRenderable(BrowserMeshComponent component)
    {
        ThrowIfFrameBusy();
        ArgumentNullException.ThrowIfNull(component);
        bool custom = _customRenderables.Remove(component);
        bool main = !custom && _renderables.Remove(component);
        if (!custom && !main)
            throw new ArgumentException("Renderable does not belong to this scene.", nameof(component));
        DiscardCollection();
        if (main && _snapshot is null)
        {
            _instanceCount = Math.Min(_instanceCount, _renderables.Count);
            if (_instanceCount > 0)
                LayoutInstances();
        }
        ReservePacket();
        component.RenderEnabled = false;
        component.MeshHandle = 0;
        component.MaterialHandle = 0;
        try
        {
            if (_graphicsInitialized)
            {
                try { ReleaseMaterial(component.Material!); }
                finally { ReleaseMesh(component.Mesh!); }
            }
        }
        finally { component.SceneNode.Destroy(); }
    }

    /// <summary>Adopts a transform into this scene and reuses uploads for identical descriptor objects.</summary>
    public BrowserMeshComponent AddRenderable(BrowserMeshData mesh, BrowserMaterialData material, Transform transform)
        => AddRenderableCore(mesh, material, transform, _customRenderables);

    private BrowserMeshComponent AddRenderableCore(BrowserMeshData mesh, BrowserMaterialData material,
        Transform transform, List<BrowserMeshComponent> destination)
    {
        ThrowIfFrameBusy();
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(transform);
        if (_renderables.Count + _customRenderables.Count >= 2048)
            throw new InvalidOperationException("Browser scene renderable capacity is exhausted.");

        // Acquire before adopting the node so upload failures leave no scene object behind.
        int meshHandle = 0;
        int materialHandle = 0;
        if (_graphicsInitialized)
        {
            meshHandle = AcquireMesh(mesh);
            try { materialHandle = AcquireMaterial(material); }
            catch { ReleaseMesh(mesh); throw; }
        }
        SceneNode? node = null;
        BrowserMeshComponent? created = null;
        try
        {
            node = new SceneNode(_parent, "BrowserRenderable", transform);
            BrowserMeshComponent component = (BrowserMeshComponent)BrowserStaticRegistrations.AddRequiredComponent(
                BrowserStaticRegistrations.MeshComponentId, node);
            created = component;
            component.Mesh = mesh;
            component.Material = material;
            component.MeshHandle = meshHandle;
            component.MaterialHandle = materialHandle;
            destination.Add(component);
            ReservePacket();
            return component;
        }
        catch
        {
            if (created is not null)
                destination.Remove(created);
            node?.Destroy();
            if (_graphicsInitialized)
            {
                try { ReleaseMaterial(material); }
                finally { ReleaseMesh(mesh); }
            }
            throw;
        }
    }

    /// <summary>Maps the already-created scene to its renderer and uploads every unique descriptor once.</summary>
    public void InitializeGraphics()
    {
        ThrowIfFrameBusy();
        if (_graphicsInitialized)
            return;
        try
        {
            _renderer.MarkReady(Id);
            _renderer.ConfigurePipeline(_quality);
            for (int i = 0; i < _renderables.Count; i++)
            {
                BrowserMeshComponent component = _renderables[i];
                component.MeshHandle = AcquireMesh(component.Mesh!);
                component.MaterialHandle = AcquireMaterial(component.Material!);
            }
            for (int i = 0; i < _customRenderables.Count; i++)
            {
                BrowserMeshComponent component = _customRenderables[i];
                component.MeshHandle = AcquireMesh(component.Mesh!);
                component.MaterialHandle = AcquireMaterial(component.Material!);
            }
            _graphicsInitialized = true;
        }
        catch
        {
            ReleaseResources();
            throw;
        }
    }

    public void SetInstanceCount(int count)
    {
        ThrowIfFrameBusy();
        if (_snapshot is not null)
            throw new NotSupportedException("Imported scenes have a fixed set of instances.");
        if (count is < 1 or > MaxInstances)
            throw new ArgumentOutOfRangeException(nameof(count), "Instance count must be from 1 to 256.");
        for (int i = _renderables.Count; i < count; i++)
            AddDemoInstance(i);
        _instanceCount = count;
        LayoutInstances();
        ReservePacket();
    }

    public void Resize(RuntimeSurfaceState surface)
    {
        ThrowIfFrameBusy();
        RuntimeSurfaceState previous = Target.Surface;
        Target.UpdateSurface(surface);
        if (surface.Generation != previous.Generation || surface.CanRender != previous.CanRender)
            InvalidateHistory();
        if (surface.PhysicalWidth != previous.PhysicalWidth || surface.PhysicalHeight != previous.PhysicalHeight)
            RebuildViews();
    }

    public void Input(RuntimeInputState input)
    {
        ThrowIfFrameBusy();
        Target.UpdateInput(input);
    }

    public void SetSplitView(bool split)
    {
        ThrowIfFrameBusy();
        if (_snapshot is not null && split)
            throw new NotSupportedException("Imported camera projections require a single viewport.");
        if (_splitView == split)
            return;
        _splitView = split;
        InvalidateHistory();
        RebuildViews();
        ReservePacket();
    }

    public void ResetClock()
    {
        ThrowIfFrameBusy();
        Target.ResetFrameClock();
        InvalidateHistory();
    }

    private void InvalidateHistory()
    {
        if (_historyGeneration == uint.MaxValue)
            throw new InvalidOperationException("Temporal history generation is exhausted; create a new scene session.");
        _accumulator = 0;
        _variableDeltaSeconds = 0;
        _historyGeneration++;
    }

    public void Frame(double timestampMilliseconds)
    {
        ThrowIfDisposed();
        if (_cookedFailed)
            throw new InvalidOperationException("Cooked content failed; dispose this scene session before rendering again.");
        if (_frameInProgress)
            throw new InvalidOperationException("Browser scene frames cannot overlap.");
        if (!_graphicsInitialized)
            throw new InvalidOperationException("Browser graphics have not been initialized.");
        if (!Target.TryBeginFrame(timestampMilliseconds, 0, out double elapsed))
            return;

        // Render cadence is variable, but simulation alone receives fixed steps. Resume and
        // long stalls start a new history rather than replaying stale accumulated time.
        if (elapsed > 0.25)
        {
            InvalidateHistory();
            elapsed = 0;
        }
        _variableDeltaSeconds = Math.Min(elapsed, FixedStep * MaxStepsPerFrame);
        _accumulator = Math.Min(_accumulator + _variableDeltaSeconds, FixedStep * MaxStepsPerFrame);
        _frameInProgress = true;
        long allocationStart = GC.GetAllocatedBytesForCurrentThread();
        _frameAttempts++;
        try
        {
            int steps = 0;
            while (_accumulator >= FixedStep && steps < MaxStepsPerFrame)
            {
                _host.Advance((float)FixedStep, publishRenderBuffers: false);
                _accumulator -= FixedStep;
                steps++;
            }
            Data.Core.XRObjectBase.ProcessPendingDestructions();

            _host.SwapBuffers();
            CollectRenderables();

            if (!_renderer.TryDescribeFrameOutput(out RenderFrameOutputDescription output))
                return;
            VisibilityCandidates = 0;
            VisibilityCulled = 0;
            VisibilityDrawn = 0;
            BrowserPipelineEnvironment environment = _environment with
            {
                ViewProjection = _left.ViewProjection
            };
            _pipeline.Begin(Id, Target.Surface.Generation,
                checked((int)output.Properties.Width), checked((int)output.Properties.Height), in environment,
                updateShadow: _submittedFrames % _quality.ShadowUpdateInterval == 0);
            try
            {
                AddViewDraws(_left);
                if (_splitView)
                    AddViewDraws(_right, includeShadows: false);
                if (_uiEnabled)
                    AddUiOverlay();
                _pipeline.Submit(_renderer);
                _submittedFrames++;
                _lastFramePacketBytes = _pipeline.Packet.ByteLength;
            }
            catch
            {
                _pipeline.Abort();
                throw;
            }
        }
        finally
        {
            _frameInProgress = false;
            Data.Core.XRObjectBase.ProcessPendingDestructions();
            _lastFrameAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
            _frameAllocatedBytes += _lastFrameAllocatedBytes;
        }
    }

    private void CollectRenderables()
    {
        int previousCount = _collectedCount;
        _collectedCount = 0;
        int mainCount = _snapshot is null ? _instanceCount : _renderables.Count;
        for (int i = 0; i < mainCount; i++)
            Collect(_renderables[i]);
        for (int i = 0; i < _customRenderables.Count; i++)
            Collect(_customRenderables[i]);
        if (_collectedCount < previousCount)
            Array.Clear(_collected, _collectedCount, previousCount - _collectedCount);
    }

    private void DiscardCollection()
    {
        // Cold scene changes must not retain the preceding frame's mesh payloads.
        Array.Clear(_collected, 0, _collectedCount);
        _collectedCount = 0;
    }

    private void Collect(BrowserMeshComponent component)
    {
        if (!component.IsRenderable)
            return;
        if (_collectedCount >= _collected.Length)
            throw new InvalidOperationException("Visible collection capacity must be reserved before the frame.");
        _collected[_collectedCount++] = new BrowserCollectedRenderable(
            component.Mesh!, component.Material!, BrowserResourceHandle.FromPacked(component.MeshHandle),
            BrowserResourceHandle.FromPacked(component.MaterialHandle),
            component.SceneNode.Transform.RenderMatrix);
    }

    private void AddViewDraws(BrowserViewport viewport, bool includeShadows = true)
    {
        if (viewport.Width <= 0 || viewport.Height <= 0)
            return;
        for (int i = 0; i < _collectedCount; i++)
            AddComponentDraw(in _collected[i], viewport, includeShadows);
    }

    private void AddComponentDraw(in BrowserCollectedRenderable component, BrowserViewport viewport, bool includeShadows)
    {
        // System.Numerics row vectors become WGSL column vectors when row-major
        // fields are read as columns by the browser renderer.
        Matrix4x4 matrix = component.ModelMatrix * viewport.ViewProjection;
        VisibilityCandidates++;
        bool colorVisible = !_cullingEnabled || BrowserFrustumVisibility.Intersects(component.Mesh, in matrix);
        if (!colorVisible)
        {
            VisibilityCulled++;
            if (!includeShadows || !component.Material.CastShadow)
                return;
        }
        Vector3 center = (component.Mesh.BoundsMinimum + component.Mesh.BoundsMaximum) * 0.5f;
        Vector3 viewCenter = Vector3.Transform(Vector3.Transform(center, component.ModelMatrix), viewport.View);
        BrowserPipelineDraw draw = new(component.MeshHandle, component.MaterialHandle, component.Material.AlphaMode,
            viewport.X, viewport.Y, viewport.Width, viewport.Height, 0, component.Mesh.IndexCount,
            component.ModelMatrix, matrix, -viewCenter.Z, includeShadows && component.Material.CastShadow, !colorVisible);
        _pipeline.AddDraw(in draw);
        if (colorVisible)
            VisibilityDrawn++;
    }

    private void AddUiOverlay()
    {
        float width = Target.Surface.PhysicalWidth;
        float height = Target.Surface.PhysicalHeight;
        float scale = MathF.Min(1, MathF.Min(width / 300, height / 90));
        Vector4 clip = new(0, 0, width, height);
        BrowserPipelineUiQuad panel = new(default, new Vector4(12, 12, 260, 56) * scale,
            new Vector4(0, 0, 1, 1), new Vector4(0.025f, 0.035f, 0.055f, 0.88f), clip);
        _pipeline.AddUi(in panel);
        for (int i = 0; i < 3; i++)
        {
            Vector4 tint = i switch
            {
                0 => new Vector4(0.65f, 0.83f, 1, 1),
                1 => new Vector4(1, 0.37f, 0.2f, 1),
                _ => new Vector4(0.2f, 1, 0.7f, 0.6f)
            };
            BrowserPipelineUiQuad swatch = new(default, new Vector4(22 + i * 32, 22, 24, 24) * scale,
                new Vector4(0, 0, 1, 1), tint, clip);
            _pipeline.AddUi(in swatch);
        }
        if (_checkerMaterial?.Texture is BrowserTextureData texture && _textures.TryGetValue(texture, out int handle))
        {
            BrowserPipelineUiQuad image = new(BrowserResourceHandle.FromPacked(handle),
                new Vector4(226, 22, 32, 32) * scale, new Vector4(0, 0, 1, 1), Vector4.One, clip);
            _pipeline.AddUi(in image);
        }
        float visibility = VisibilityCandidates == 0 ? 0 : (float)VisibilityDrawn / VisibilityCandidates;
        BrowserPipelineUiQuad bar = new(default, new Vector4(22, 54, 192 * visibility, 4) * scale,
            new Vector4(0, 0, 1, 1), new Vector4(0.4f, 0.8f, 1, 1), clip);
        if (visibility > 0)
            _pipeline.AddUi(in bar);
    }

    private void RebuildViews()
    {
        RuntimeSurfaceState surface = Target.Surface;
        int width = surface.PhysicalWidth;
        int height = surface.PhysicalHeight;
        if (_cameraOverride is BrowserCameraSnapshot camera)
        {
            Matrix4x4 viewProjection = camera.ViewProjection;
            if (!_splitView)
            {
                _left = new BrowserViewport(0, 0, width, height, viewProjection, camera.View);
                _right = default;
            }
            else
            {
                int half = width / 2;
                _left = new BrowserViewport(0, 0, half, height, viewProjection, camera.View);
                _right = new BrowserViewport(half, 0, width - half, height, viewProjection, camera.View);
            }
            return;
        }
        if (_snapshot is not null)
        {
            // The captured projection is used unchanged across canvas sizes; the viewport
            // stretches its original aspect ratio when the canvas aspect differs.
            _left = new BrowserViewport(0, 0, width, height, _snapshot.Camera.ViewProjection, _snapshot.Camera.View);
            _right = default;
            return;
        }
        if (!_splitView)
        {
            _left = BrowserViewport.Create(0, 0, width, height, Matrix4x4.Identity);
            _right = default;
            return;
        }

        int leftWidth = width / 2;
        _left = BrowserViewport.Create(0, 0, leftWidth, height, Matrix4x4.Identity);
        _right = BrowserViewport.Create(leftWidth, 0, width - leftWidth, height,
            Matrix4x4.CreateLookAt(new Vector3(1.5f, 0.8f, 0), new Vector3(0, 0, -2.5f), Vector3.UnitY));
    }

    private void ReservePacket()
    {
        int count = (_snapshot is null ? _instanceCount : _renderables.Count) + _customRenderables.Count;
        _pipeline.EnsureCapacity(count * (_splitView ? 2 : 1), 8);
        if (count <= _collected.Length)
            return;
        int capacity = _collected.Length;
        while (capacity < count)
            capacity *= 2;
        Array.Resize(ref _collected, capacity);
    }

    private void AddDemoInstance(int index)
    {
        bool cube = index % 3 == 0;
        AddRenderableCore(cube ? _cubeMesh! : _panelMesh!,
            cube ? _checkerMaterial! : index % 3 == 1 ? _panelMaterial! : _transparentMaterial!,
            (Transform)BrowserStaticRegistrations.CreateRequiredTransform(BrowserStaticRegistrations.TransformId),
            _renderables);
    }

    private void LayoutInstances()
    {
        int columns = (int)MathF.Ceiling(MathF.Sqrt(_instanceCount));
        int rows = (_instanceCount + columns - 1) / columns;
        float spacing = MathF.Min(1.05f, 7.2f / Math.Max(columns, rows));
        for (int i = 0; i < _instanceCount; i++)
        {
            int x = i % columns;
            int y = i / columns;
            Transform transform = (Transform)_renderables[i].SceneNode.Transform;
            transform.Translation = new Vector3((x - (columns - 1) * 0.5f) * spacing,
                ((rows - 1) * 0.5f - y) * spacing, -4.5f + (i % 3) * 0.13f);
            transform.Scale = new Vector3(spacing * 0.72f);
            transform.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (i % 5) * 0.24f);
        }
    }

    private int AcquireMesh(BrowserMeshData mesh)
    {
        if (_meshes.TryGetValue(mesh, out int handle))
        {
            _meshReferences[mesh]++;
            return handle;
        }
        handle = _renderer.CreateMesh(mesh);
        if (handle <= 0)
            throw new InvalidOperationException("Browser renderer rejected a mesh upload.");
        _meshes.Add(mesh, handle);
        _meshReferences.Add(mesh, 1);
        return handle;
    }

    private int AcquireTexture(BrowserTextureData texture)
    {
        if (_textures.TryGetValue(texture, out int handle))
        {
            _textureReferences[texture]++;
            return handle;
        }
        handle = _renderer.CreateTexture(texture);
        if (handle <= 0)
            throw new InvalidOperationException("Browser renderer rejected a texture upload.");
        _textures.Add(texture, handle);
        _textureReferences.Add(texture, 1);
        return handle;
    }

    private int AcquireMaterial(BrowserMaterialData material)
    {
        if (_materials.TryGetValue(material, out int handle))
        {
            _materialReferences[material]++;
            return handle;
        }
        int textureHandle = 0;
        if (material.Texture is BrowserTextureData texture)
            textureHandle = AcquireTexture(texture);
        try
        {
            handle = _renderer.CreateMaterial(material, textureHandle);
            if (handle <= 0)
                throw new InvalidOperationException("Browser renderer rejected a material upload.");
        }
        catch
        {
            if (material.Texture is BrowserTextureData acquiredTexture)
                ReleaseTexture(acquiredTexture);
            throw;
        }
        _materials.Add(material, handle);
        _materialReferences.Add(material, 1);
        return handle;
    }

    private void ReleaseMesh(BrowserMeshData mesh)
    {
        if (--_meshReferences[mesh] != 0)
            return;
        _meshReferences.Remove(mesh);
        int handle = _meshes[mesh];
        _meshes.Remove(mesh);
        _renderer.DestroyResource(handle);
    }

    private void ReleaseMaterial(BrowserMaterialData material)
    {
        if (--_materialReferences[material] != 0)
            return;
        _materialReferences.Remove(material);
        int handle = _materials[material];
        _materials.Remove(material);
        try { _renderer.DestroyResource(handle); }
        finally
        {
            if (material.Texture is BrowserTextureData texture)
                ReleaseTexture(texture);
        }
    }

    private void ReleaseTexture(BrowserTextureData texture)
    {
        if (--_textureReferences[texture] != 0)
            return;
        _textureReferences.Remove(texture);
        int handle = _textures[texture];
        _textures.Remove(texture);
        _renderer.DestroyResource(handle);
    }

    private void ReleaseResources()
    {
        // Materials retain texture handles, so release them before textures and meshes.
        Exception? error = null;
        foreach (int handle in _materials.Values)
            TryDestroy(handle, ref error);
        _materials.Clear();
        _materialReferences.Clear();
        foreach (int handle in _textures.Values)
            TryDestroy(handle, ref error);
        _textures.Clear();
        _textureReferences.Clear();
        foreach (int handle in _meshes.Values)
            TryDestroy(handle, ref error);
        _meshes.Clear();
        _meshReferences.Clear();
        if (error is not null)
            throw error;
    }

    private void TryDestroy(int handle, ref Exception? firstError)
    {
        try
        {
            _renderer.DestroyResource(handle);
        }
        catch (Exception exception)
        {
            firstError ??= exception;
        }
    }

    private static Transform ImportTransform(Matrix4x4 matrix)
    {
        ValidateImportTransform(matrix, out Vector3 scale, out Quaternion rotation, out Vector3 translation);
        Transform transform = (Transform)BrowserStaticRegistrations.CreateRequiredTransform(
            BrowserStaticRegistrations.TransformId);
        transform.Scale = scale;
        transform.Translation = translation;
        transform.Rotation = rotation;
        return transform;
    }

    /// <summary>Rejects unsupported transforms before replacing an active imported scene.</summary>
    internal static void ValidateImportSnapshot(BrowserSceneSnapshot snapshot)
    {
        for (int i = 0; i < snapshot.Instances.Count; i++)
            ValidateImportTransform(snapshot.Instances[i].ModelMatrix, out _, out _, out _);
    }

    private static void ValidateImportTransform(Matrix4x4 matrix,
        out Vector3 scale, out Quaternion rotation, out Vector3 translation)
    {
        if (!Matrix4x4.Decompose(matrix, out scale, out rotation, out translation))
            throw new NotSupportedException("Imported model transform cannot be represented as scale, rotation, and translation.");
        Matrix4x4 reconstructed = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) *
            Matrix4x4.CreateTranslation(translation);
        if (!ApproximatelyEqual(matrix, reconstructed))
            throw new NotSupportedException("Imported model transform contains shear or perspective.");
    }

    private static bool ApproximatelyEqual(Matrix4x4 actual, Matrix4x4 expected)
    {
        Span<float> a = stackalloc float[16]
        {
            actual.M11, actual.M12, actual.M13, actual.M14, actual.M21, actual.M22, actual.M23, actual.M24,
            actual.M31, actual.M32, actual.M33, actual.M34, actual.M41, actual.M42, actual.M43, actual.M44
        };
        Span<float> b = stackalloc float[16]
        {
            expected.M11, expected.M12, expected.M13, expected.M14, expected.M21, expected.M22, expected.M23, expected.M24,
            expected.M31, expected.M32, expected.M33, expected.M34, expected.M41, expected.M42, expected.M43, expected.M44
        };
        for (int i = 0; i < a.Length; i++)
            if (MathF.Abs(a[i] - b[i]) > 0.0001f * MathF.Max(1.0f, MathF.Abs(a[i])))
                return false;
        return true;
    }

    private static bool IsFinite(in Matrix4x4 m)
        => float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M13) && float.IsFinite(m.M14) &&
           float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M23) && float.IsFinite(m.M24) &&
           float.IsFinite(m.M31) && float.IsFinite(m.M32) && float.IsFinite(m.M33) && float.IsFinite(m.M34) &&
           float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43) && float.IsFinite(m.M44);

    private static BrowserMeshData CreateCubeMesh()
    {
        List<float> vertices = new(24 * 5);
        List<uint> indices = new(36);
        static void Face(List<float> vertices, List<uint> indices, Vector3 center, Vector3 right, Vector3 up)
        {
            uint start = (uint)(vertices.Count / 5);
            Vector3 a = center - right - up;
            Vector3 b = center + right - up;
            Vector3 c = center + right + up;
            Vector3 d = center - right + up;
            static void Vertex(List<float> vertices, Vector3 p, float u, float v)
            {
                vertices.Add(p.X); vertices.Add(p.Y); vertices.Add(p.Z); vertices.Add(u); vertices.Add(v);
            }
            Vertex(vertices, a, 0, 1); Vertex(vertices, b, 1, 1);
            Vertex(vertices, c, 1, 0); Vertex(vertices, d, 0, 0);
            indices.Add(start); indices.Add(start + 1); indices.Add(start + 2);
            indices.Add(start); indices.Add(start + 2); indices.Add(start + 3);
        }
        Face(vertices, indices, new Vector3(0, 0, 0.4f), new Vector3(0.4f, 0, 0), new Vector3(0, 0.4f, 0));
        Face(vertices, indices, new Vector3(0, 0, -0.4f), new Vector3(-0.4f, 0, 0), new Vector3(0, 0.4f, 0));
        Face(vertices, indices, new Vector3(0.4f, 0, 0), new Vector3(0, 0, -0.4f), new Vector3(0, 0.4f, 0));
        Face(vertices, indices, new Vector3(-0.4f, 0, 0), new Vector3(0, 0, 0.4f), new Vector3(0, 0.4f, 0));
        Face(vertices, indices, new Vector3(0, 0.4f, 0), new Vector3(0.4f, 0, 0), new Vector3(0, 0, -0.4f));
        Face(vertices, indices, new Vector3(0, -0.4f, 0), new Vector3(0.4f, 0, 0), new Vector3(0, 0, 0.4f));
        return BrowserStaticRegistrations.CreateMesh(BrowserStaticRegistrations.MeshId,
            vertices.ToArray(), indices.ToArray());
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private void ThrowIfFrameBusy()
    {
        ThrowIfDisposed();
        if (_frameInProgress)
            throw new InvalidOperationException("Scene mutation must wait until the browser frame completes.");
    }

    /// <summary>Marks this scene's renderer unusable after browser initialization or device loss.</summary>
    public void RendererFailed(bool deviceLost)
    {
        ThrowIfFrameBusy();
        _renderer.MarkFailed(deviceLost);
        ResetClock();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        if (_frameInProgress)
            throw new InvalidOperationException("A browser scene cannot be disposed during its frame.");
        _disposed = true;
        DiscardCollection();
        ClearCookedContent();
        try
        {
            ReleaseResources();
        }
        finally
        {
            try
            {
                _host.Dispose();
                Data.Core.XRObjectBase.ProcessPendingDestructions();
            }
            finally
            {
                try { _renderer.Dispose(); }
                finally
                {
                    _pipeline.Abort();
                    _pipeline.Dispose();
                    _uploads.Dispose();
                }
            }
        }
    }
}
