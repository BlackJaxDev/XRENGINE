using System.Numerics;
using XREngine.Components.Scene.Mesh;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models;
using XREngine.Scene;

namespace XREngine.Rendering.Tools;

public sealed partial class OctahedralImposterGenerator
{
    // One full 26-view atlas owns hundreds of MiB at the supported upper extent.
    // Queue concurrent requests before allocating their scene/texture generations.
    private static readonly SemaphoreSlim s_browserCaptureBudget = new(1, 1);
    private const int MaximumBrowserCaptureRequests = 4;
    private static int s_browserCaptureRequests;
    private static Task<Result?> GenerateBrowserAsync(ModelComponent component, Settings settings,
        IReadOnlyCollection<int>? submeshIndices, IProgress<CaptureProgress>? progress, CancellationToken cancellationToken)
        => StartBrowserCaptureAsync(() =>
        {
            Model model = component.Model ?? throw new InvalidOperationException("OctahedralImposter.SourceMissing: no model is assigned.");
            IRuntimeRenderWorld world = component.World.GetRenderWorld()
                ?? throw new InvalidOperationException("OctahedralImposter.WorldMissing: the source is not attached to a render world.");
            RenderInfo3D[] targets = ResolveTargetRenderInfos(component, submeshIndices, out int[] indices, out var mode);
            AABB worldBounds = CalculateCombinedWorldBounds(component, model, indices);
            AABB localBounds = CalculateCombinedLocalBounds(component, model, indices, worldBounds);
            Matrix4x4 matrix = component.Transform.RenderMatrix;
            RenderInfo[] sourceObjects = component.RenderedObjects;
            RenderInfo[] sourceObjectSnapshot = [.. sourceObjects];
            CaptureSnapshot snapshot = new(world, targets, () => !component.IsDestroyed &&
                ReferenceEquals(component.Model, model) && ReferenceEquals(component.World.GetRenderWorld(), world) &&
                ReferenceEquals(component.RenderedObjects, sourceObjects) && SameRenderInfos(sourceObjects, sourceObjectSnapshot) &&
                component.Transform.RenderMatrix == matrix);
            return new CaptureJob(snapshot, settings, model, localBounds, worldBounds,
                TransformPointToLocal(component, worldBounds.Center), indices, mode, progress, cancellationToken);
        }, cancellationToken);

    private static bool SameRenderInfos(RenderInfo[] current, RenderInfo[] expected)
    {
        if (current.Length != expected.Length) return false;
        for (int index = 0; index < current.Length; index++)
            if (!ReferenceEquals(current[index], expected[index])) return false;
        return true;
    }

    /// <summary>Captures an unpublished HLOD proxy without inserting a temporary component into its source world.</summary>
    internal static Task<Result?> GenerateProxyAsync(XRMeshRenderer proxy, IRuntimeRenderWorld world,
        Matrix4x4 worldMatrix, AABB localBounds, Settings settings, Func<bool> isCurrent,
        CancellationToken cancellationToken)
        => StartBrowserCaptureAsync(() =>
        {
            CaptureSnapshot owner = new(world, proxy, worldMatrix, localBounds, isCurrent);
            AABB worldBounds = localBounds.ToBox(worldMatrix).GetAABB(transformed: true);
            return new CaptureJob(owner, settings, null, localBounds, worldBounds, localBounds.Center,
                [], EOctahedralImposterCaptureMode.Model, null, cancellationToken, proxy.Name);
        }, cancellationToken);

    private static async Task<Result?> StartBrowserCaptureAsync(Func<CaptureJob> create, CancellationToken cancellationToken)
    {
        // Retain the session's actual queue before waiting for the atlas budget.
        // A later session must never adopt startup work admitted by this one.
        JobManager dispatcher = XREngine.Execution.RuntimeWorkScheduler.CaptureCallerThreadJobs();
        if (Interlocked.Increment(ref s_browserCaptureRequests) > MaximumBrowserCaptureRequests)
        {
            Interlocked.Decrement(ref s_browserCaptureRequests);
            throw new InvalidOperationException("OctahedralImposter.CaptureQueueFull: at most four active or queued capture requests may retain source ownership.");
        }
        bool admitted = false;
        try
        {
            await s_browserCaptureBudget.WaitAsync(cancellationToken);
            admitted = true;
            TaskCompletionSource<Result?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int startState = 0;
            using CancellationTokenRegistration cancellation = cancellationToken.Register(() =>
            {
                if (Interlocked.CompareExchange(ref startState, 2, 0) == 0)
                    completion.TrySetCanceled(cancellationToken);
            });
            JobHandle dispatch = dispatcher.Schedule(new LabeledActionJob(() =>
            {
                if (Interlocked.CompareExchange(ref startState, 1, 0) != 0) return;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AbstractRenderer renderer = AbstractRenderer.Current ??
                        RuntimeRenderingHostServices.FrameTiming.CurrentRenderer as AbstractRenderer ??
                        throw new InvalidOperationException("OctahedralImposter.RendererMissing: capture requires an active renderer owner.");
                    using var scope = AbstractRenderer.EnterThreadCurrentScope(renderer);
                    CaptureJob job = create();
                    _ = FinishBrowserCaptureAsync(job, completion);
                }
                catch (OperationCanceledException) { completion.TrySetCanceled(cancellationToken); }
                catch (Exception error) { completion.TrySetException(error); }
            }, "OctahedralImposterGenerator.Capture"), JobPriority.Normal, JobAffinity.RenderThread);
            _ = ObserveDispatchAsync(dispatch.WaitAsync());
            return await completion.Task;

            async Task ObserveDispatchAsync(Task dispatched)
            {
                try { await dispatched; }
                catch (OperationCanceledException)
                {
                    if (Interlocked.CompareExchange(ref startState, 2, 0) == 0)
                        completion.TrySetCanceled();
                }
                catch (Exception error)
                {
                    if (Interlocked.CompareExchange(ref startState, 2, 0) == 0)
                        completion.TrySetException(error);
                }
            }
        }
        finally
        {
            if (admitted) s_browserCaptureBudget.Release();
            Interlocked.Decrement(ref s_browserCaptureRequests);
        }
    }

    private static async Task FinishBrowserCaptureAsync(CaptureJob job, TaskCompletionSource<Result?> completion)
    {
        Result? result = null;
        Exception? failure = null;
        try { result = await job.RunAsync(); }
        catch (Exception error) { failure = error; }
        try { job.Dispose(); }
        catch (Exception error) { failure ??= error; }
        if (failure is not null && result is not null)
        {
            try { new GeneratedImposterResources(result).Dispose(); }
            catch (Exception cleanupError) { failure = new AggregateException(failure, cleanupError); }
        }
        if (failure is OperationCanceledException) completion.TrySetCanceled();
        else if (failure is not null) completion.TrySetException(failure);
        else completion.TrySetResult(result);
    }

    private sealed class CaptureSnapshot : IRenderable, IDisposable
    {
        private readonly Func<bool> _sourceCurrent;
        private readonly List<Func<bool>> _checks = [];
        private readonly List<(XRBase Source, XRPropertyChangedEventHandler Callback)> _subscriptions = [];
        private readonly HashSet<XRMeshRenderer> _observedRenderers = [];
        private readonly HashSet<XRMaterial> _observedMaterials = [];
        private readonly HashSet<XRTexture> _observedTextures = [];
        private readonly List<(XRTexture Texture, Action Callback)> _textureSubscriptions = [];
        private readonly List<RenderInfo3D> _ownedInfos = [];
        private bool _changed, _disposed;
        internal readonly IRuntimeRenderWorld SourceWorld;
        internal RenderInfo3D[] Infos { get; } = [];
        public RenderInfo[] RenderedObjects { get; } = [];

        internal CaptureSnapshot(IRuntimeRenderWorld world, RenderInfo3D[] sources, Func<bool> isCurrent)
        {
            SourceWorld = world;
            _sourceCurrent = isCurrent;
            try
            {
                foreach (RenderInfo3D source in sources)
                {
                    bool visible = source.IsVisible, shouldRender = source.ShouldRender;
                    var sourceCommands = source.RenderCommands;
                    RenderCommand[] sourceCommandSnapshot = [.. sourceCommands];
                    _checks.Add(() => source.IsVisible == visible && source.ShouldRender == shouldRender &&
                        ReferenceEquals(source.RenderCommands, sourceCommands) && sourceCommands.Count == sourceCommandSnapshot.Length);
                    for (int index = 0; index < sourceCommandSnapshot.Length; index++)
                    {
                        int slot = index;
                        RenderCommand command = sourceCommandSnapshot[index];
                        bool enabled = command.Enabled;
                        _checks.Add(() => sourceCommands.Count == sourceCommandSnapshot.Length &&
                            ReferenceEquals(sourceCommands[slot], command) && command.Enabled == enabled);
                        if (command is RenderCommandMesh3D sourceMesh)
                        {
                            XRMeshRenderer? renderer = sourceMesh.Mesh;
                            _checks.Add(() => ReferenceEquals(sourceMesh.Mesh, renderer));
                        }
                    }
                    if (!source.IsVisible || !source.ShouldRender) continue;
                    List<RenderCommand> commands = [];
                    foreach (RenderCommand command in source.RenderCommands)
                        if (command is RenderCommandMesh3D mesh && mesh.Mesh is not null && mesh.Enabled)
                        {
                            ObserveRenderer(mesh.Mesh);
                            if (mesh.MaterialOverride is { } material) ObserveMaterial(material);
                            if (mesh.RenderOptionsOverride is { } options) ObserveChanges(options);
                            RenderCommandMesh3D captured = new(mesh.RenderPass)
                            {
                                Mesh = mesh.Mesh, WorldMatrix = mesh.WorldMatrix,
                                WorldMatrixIsModelMatrix = mesh.WorldMatrixIsModelMatrix,
                                MaterialOverride = mesh.MaterialOverride, RenderOptionsOverride = mesh.RenderOptionsOverride,
                                Instances = mesh.Instances, ForceCpuRendering = mesh.ForceCpuRendering,
                            };
                            commands.Add(captured);
                            _checks.Add(() => mesh.Enabled && mesh.RenderPass == captured.RenderPass &&
                                ReferenceEquals(mesh.Mesh, captured.Mesh) && ReferenceEquals(mesh.MaterialOverride, captured.MaterialOverride) &&
                                ReferenceEquals(mesh.RenderOptionsOverride, captured.RenderOptionsOverride) && mesh.WorldMatrix == captured.WorldMatrix &&
                                mesh.WorldMatrixIsModelMatrix == captured.WorldMatrixIsModelMatrix && mesh.Instances == captured.Instances &&
                                mesh.ForceCpuRendering == captured.ForceCpuRendering);
                        }
                    if (commands.Count == 0) continue;
                    RenderInfo3D info = RenderInfo3D.New(source.Owner ?? this, [.. commands]);
                    _ownedInfos.Add(info);
                    info.LocalCullingVolume = source.LocalCullingVolume;
                    info.CullingOffsetMatrix = source.CullingOffsetMatrix;
                    info.Layer = source.Layer;
                    info.CastsShadows = source.CastsShadows;
                    info.ReceivesShadows = source.ReceivesShadows;
                    info.HiddenFromOwner = source.HiddenFromOwner;
                    info.VisibleToOwnerOnly = source.VisibleToOwnerOnly;
                    _checks.Add(() => source.IsVisible && source.ShouldRender && source.Layer == info.Layer &&
                        source.CastsShadows == info.CastsShadows && source.ReceivesShadows == info.ReceivesShadows &&
                        source.HiddenFromOwner == info.HiddenFromOwner && source.VisibleToOwnerOnly == info.VisibleToOwnerOnly);
                }
                Infos = [.. _ownedInfos];
                RenderedObjects = Infos;
                ObserveLighting();
            }
            catch { Dispose(); throw; }
        }

        internal CaptureSnapshot(IRuntimeRenderWorld world, XRMeshRenderer proxy, Matrix4x4 matrix,
            AABB bounds, Func<bool> isCurrent)
        {
            SourceWorld = world;
            _sourceCurrent = isCurrent;
            try
            {
                ObserveRenderer(proxy);
                int pass = proxy.Material?.RenderPass ?? proxy.Submeshes[0].Material!.RenderPass;
                RenderCommandMesh3D command = new(pass) { Mesh = proxy, WorldMatrix = matrix, WorldMatrixIsModelMatrix = true, Instances = 1 };
                RenderInfo3D info = RenderInfo3D.New(this, command);
                _ownedInfos.Add(info);
                info.LocalCullingVolume = bounds;
                info.CullingOffsetMatrix = matrix;
                Infos = [info];
                RenderedObjects = Infos;
                ObserveLighting();
            }
            catch { Dispose(); throw; }
        }

        private void ObserveRenderer(XRMeshRenderer renderer)
        {
            if (!_observedRenderers.Add(renderer)) return;
            SceneCaptureSourceGeometry.ObserveRenderer(renderer, _checks);
            if (renderer.Material is { } primaryMaterial) ObserveMaterial(primaryMaterial);
            foreach (var submesh in renderer.Submeshes)
                if (submesh.Material is { } material) ObserveMaterial(material);
        }

        private void ObserveMaterial(XRMaterial material)
        {
            if (!_observedMaterials.Add(material)) return;
            ulong values = material.BindingValueVersion, resources = material.BindingResourceVersion;
            long shaders = material.ShaderStateRevision;
            _checks.Add(() => !material.IsDestroyed && material.BindingValueVersion == values &&
                material.BindingResourceVersion == resources && material.ShaderStateRevision == shaders);
            ObserveChanges(material.RenderOptions);
            foreach (XRTexture? texture in material.Textures)
                if (texture is not null) ObserveTexture(texture);
        }

        private void ObserveTexture(XRTexture texture)
        {
            if (!_observedTextures.Add(texture)) return;
            ObserveChanges(texture);
            Action callback = () => _changed = true;
            texture.PushDataRequested += callback;
            _textureSubscriptions.Add((texture, callback));
            switch (texture)
            {
                case XRTextureViewBase view:
                    ObserveTexture(view.GetViewedTexture());
                    break;
                case XRTexture2DArray array:
                    foreach (XRTexture2D slice in array.Textures) ObserveTexture(slice);
                    break;
                case XRTexture2D image:
                    foreach (Mipmap2D mip in image.Mipmaps) ObserveChanges(mip);
                    break;
            }
        }

        private void ObserveLighting()
        {
            var lights = SourceWorld.Lights;
            int directional = lights.DynamicDirectionalLights.Count, point = lights.DynamicPointLights.Count, spot = lights.DynamicSpotLights.Count;
            _checks.Add(() => ReferenceEquals(SourceWorld.Lights, lights) && lights.DynamicDirectionalLights.Count == directional && lights.DynamicPointLights.Count == point && lights.DynamicSpotLights.Count == spot);
            for (int index = 0; index < directional; index++)
            {
                int slot = index;
                var light = lights.DynamicDirectionalLights[index];
                _checks.Add(() => ReferenceEquals(lights.DynamicDirectionalLights[slot], light));
                ObserveChanges(light); ObserveTransform(light.Transform);
            }
            for (int index = 0; index < point; index++)
            {
                int slot = index;
                var light = lights.DynamicPointLights[index];
                _checks.Add(() => ReferenceEquals(lights.DynamicPointLights[slot], light));
                ObserveChanges(light); ObserveTransform(light.Transform);
            }
            for (int index = 0; index < spot; index++)
            {
                int slot = index;
                var light = lights.DynamicSpotLights[index];
                _checks.Add(() => ReferenceEquals(lights.DynamicSpotLights[slot], light));
                ObserveChanges(light); ObserveTransform(light.Transform);
            }
            var ambient = SourceWorld.GetEffectiveAmbientColor();
            object? world = SourceWorld.TargetWorldObject;
            _checks.Add(() => ReferenceEquals(SourceWorld.TargetWorldObject, world) && SourceWorld.GetEffectiveAmbientColor().Equals(ambient));
            int probeVersion = lights.LightProbeBatchCompletedVersion;
            int probeCount = lights.LightProbes.Count;
            _checks.Add(() => !lights.LightProbeBatchCaptureActive && lights.LightProbeBatchCompletedVersion == probeVersion && lights.LightProbes.Count == probeCount);
            for (int index = 0; index < probeCount; index++)
            {
                int slot = index;
                var probe = lights.LightProbes[index];
                uint version = probe.CaptureVersion;
                _checks.Add(() => ReferenceEquals(lights.LightProbes[slot], probe) && probe.CaptureVersion == version);
                ObserveChanges(probe);
                if (probe.IrradianceTexture is { } irradiance) ObserveTexture(irradiance);
                if (probe.PrefilterTexture is { } prefilter) ObserveTexture(prefilter);
            }

        }

        private void ObserveTransform(XREngine.Scene.Transforms.TransformBase transform)
        {
            Matrix4x4 matrix = transform.RenderMatrix;
            _checks.Add(() => transform.RenderMatrix == matrix);
        }

        private void ObserveChanges(XRBase source)
        {
            XRPropertyChangedEventHandler callback = (_, change) =>
            {
                // Frozen shadow copies own the accepted content/projection; normal
                // main-output relevance and producer telemetry may keep advancing.
                if (source is LightComponent && change.PropertyName is "LastShadowRelevanceFrame" or
                    "ShadowFrustumRelevant" or "LastShadowRenderFrame" or "LastRenderedShadowFaceMask" or "ShadowMap")
                    return;
                _changed = true;
            };
            source.PropertyChanged += callback;
            _subscriptions.Add((source, callback));
        }

        internal bool IsCurrent()
        {
            if (_changed || !_sourceCurrent()) return false;
            foreach (Func<bool> check in _checks) if (!check()) return false;
            return true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var (source, callback) in _subscriptions) source.PropertyChanged -= callback;
            _subscriptions.Clear();
            foreach (var (texture, callback) in _textureSubscriptions) texture.PushDataRequested -= callback;
            _textureSubscriptions.Clear();
            List<Exception>? failures = null;
            foreach (RenderInfo3D info in _ownedInfos)
            {
                try { info.Dispose(); }
                catch (Exception error) { (failures ??= []).Add(error); }
            }
            _ownedInfos.Clear();
            if (failures is not null) throw new AggregateException("Capture snapshot cleanup failed.", failures);
        }
    }

    private sealed class CaptureJob : IDisposable
    {
        private readonly CaptureSnapshot _snapshot;
        private readonly Settings _settings;
        private readonly string _assetName;
        private readonly string? _modelPath;
        private readonly AABB _localBounds, _worldBounds;
        private readonly Vector3 _centerLocal;
        private readonly int[] _indices;
        private readonly EOctahedralImposterCaptureMode _mode;
        private readonly IProgress<CaptureProgress>? _progress;
        private readonly CancellationTokenSource _cancellation;
        private readonly AbstractRenderer _renderer;
        private readonly IAsyncSceneCaptureBackendCapability _backend;
        private readonly long _backendGeneration;
        private float _elapsedTime;
        private readonly IRuntimeRenderSchedulingServices _scheduling;
        private readonly SceneCaptureWorld _world;
        private readonly XRViewport _viewport;
        private readonly XRTexture2DArray _color;
        private readonly XRRenderBuffer _depth;
        private readonly XRFrameBuffer[] _framebuffers;
        private readonly DataSource?[] _pixels = new DataSource?[26];
        private readonly SceneCaptureReadback[] _receipts = new SceneCaptureReadback[26];
        private readonly List<ObjectCacheOwnership> _cameraOwnership = new(26);
        private readonly float _extent;
        private ulong _preparedFrame;
        private ulong _publishedFrame;
        private bool _active, _published, _disposed, _captureSceneRetired;
        private Exception? _phaseFailure;
        private SceneCaptureLightingSnapshot? _lighting;
        private RenderPipeline? _capturedPipeline;
        private ulong _pipelineCommandGeneration;

        internal CaptureJob(CaptureSnapshot snapshot, Settings settings, Model? model, AABB localBounds,
            AABB worldBounds, Vector3 centerLocal, int[] indices, EOctahedralImposterCaptureMode mode,
            IProgress<CaptureProgress>? progress, CancellationToken cancellationToken, string? sourceName = null)
        {
            _snapshot = snapshot; _settings = settings; _localBounds = localBounds; _worldBounds = worldBounds;
            _assetName = model is null ? $"{sourceName ?? "Model"}_OctahedralBillboard" : BuildDefaultAssetName(model, indices);
            _modelPath = model?.FilePath;
            _centerLocal = centerLocal; _indices = indices; _mode = mode; _progress = progress;
            try
            {
                if (settings.SheetSize == 0 || settings.SheetSize > 1024 || !worldBounds.IsValid || snapshot.Infos.Length == 0)
                    throw new ArgumentException("OctahedralImposter.InvalidCapture: valid source bounds and a resolution from 1 through 1024 are required by the bounded atlas memory budget.");
                _renderer = AbstractRenderer.Current ?? throw new InvalidOperationException("OctahedralImposter.RendererMissing");
                _backend = _renderer as IAsyncSceneCaptureBackendCapability ??
                    throw new NotSupportedException("OctahedralImposter.AsyncBackendMissing: the renderer has no asynchronous scene-output capability.");
                _backendGeneration = _renderer.BackendGeneration;
                _elapsedTime = RuntimeRenderingHostServices.FrameTiming.ElapsedTime;
                _extent = CalculateOrthographicExtent(worldBounds, settings.CapturePadding);
                if (!float.IsFinite(_extent) || _extent <= 0)
                    throw new ArgumentException("OctahedralImposter.InvalidExtent: source bounds and padding must produce a finite positive capture extent.");
                _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _world = new SceneCaptureWorld(snapshot.SourceWorld, snapshot.Infos, worldBounds);
                _viewport = new XRViewport(null, settings.SheetSize, settings.SheetSize)
                {
                    PipelineRequest = RenderPipelineRequest.OffscreenCapture(), SetRenderPipelineFromCamera = false,
                    AllowUIRender = false, AutomaticallyCollectVisible = false, AutomaticallySwapBuffers = false,
                    CullWithFrustum = true, RenderPipeline = settings.Pipeline ?? RuntimeEngine.Rendering.NewOffscreenCaptureRenderPipeline(),
                    WorldInstanceOverride = _world, MeshSubmissionStrategyOverride = settings.SubmissionStrategy,
                };
                foreach (RenderInfo3D info in snapshot.Infos)
                    foreach (RenderCommand command in info.RenderCommands)
                        if (!_viewport.RenderPipeline!.PassIndicesAndSorters.ContainsKey(command.RenderPass))
                            throw new NotSupportedException($"OctahedralImposter.ScenePassMissing: the authored pipeline has no route for source pass {command.RenderPass}.");
                _color = CreateColorArray(settings.SheetSize, initializeGpu: false);
                _color.AutoGenerateMipmaps = false;
                int mipCount = 1 + BitOperations.Log2(settings.SheetSize);
                foreach (XRTexture2D slice in _color.Textures)
                {
                    slice.AutoGenerateMipmaps = false;
                    Mipmap2D[] mips = new Mipmap2D[mipCount];
                    for (int mip = 0; mip < mipCount; mip++)
                        mips[mip] = new Mipmap2D(Math.Max(1u, settings.SheetSize >> mip), Math.Max(1u, settings.SheetSize >> mip),
                            EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.Float, allocateData: false);
                    slice.Mipmaps = mips;
                }
                _depth = new(settings.SheetSize, settings.SheetSize, ERenderBufferStorage.Depth24Stencil8);
                _framebuffers = BuildLayerFramebuffers(_color, _depth);
                _scheduling = RuntimeRenderingHostServices.Scheduling;
                _scheduling.SubscribeViewportCollectVisible(Collect);
                _scheduling.SubscribeWorldSwapBuffers(Publish);
                _scheduling.SubscribeViewportSwapBuffers(Swap);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private bool IsCurrent() => !_disposed && !_cancellation.IsCancellationRequested &&
            _renderer.BackendGeneration == _backendGeneration && _snapshot.IsCurrent() &&
            (_capturedPipeline is null || (_captureSceneRetired || ReferenceEquals(_viewport.RenderPipeline, _capturedPipeline)) &&
                _capturedPipeline.CommandGeneration == _pipelineCommandGeneration);

        private void Collect() => RunPhase(0);
        private void Publish() => RunPhase(1);
        private void Swap() => RunPhase(2);

        private void RunPhase(int phase)
        {
            if (!_active || _disposed) return;
            try
            {
                if (!IsCurrent()) throw new OperationCanceledException("OctahedralImposter.SourceChanged: the capture snapshot changed.");
                using var scope = AbstractRenderer.EnterThreadCurrentScope(_renderer);
                switch (phase)
                {
                    case 0:
                        _world.GlobalPreCollectVisible();
                        using (RenderWorldSnapshotPublication.EnterIsolatedScene(_world.VisualScene))
                            _viewport.CollectVisible(collectMirrors: false, allowScreenSpaceUICollectVisible: false);
                        break;
                    case 1:
                        _publishedFrame = RuntimeRenderingHostServices.FrameTiming.CollectFrameId;
                        _world.Publish(_publishedFrame);
                        break;
                    case 2:
                        _viewport.SwapBuffers(allowScreenSpaceUISwap: false);
                        _preparedFrame = _publishedFrame;
                        break;
                }
            }
            catch (Exception error) { _phaseFailure = error; _cancellation.Cancel(); }
        }

        internal async Task<Result?> RunAsync()
        {
            _progress?.Report(new(0, 26, "Starting octahedral capture."));
            Task<SceneCaptureLightingSnapshot> lightingTask;
            using (AbstractRenderer.EnterThreadCurrentScope(_renderer))
                lightingTask = _backend.CaptureSceneLightingAsync(_snapshot.SourceWorld, IsCurrent, _cancellation.Token);
            _lighting = await lightingTask;
            _elapsedTime = _lighting.ElapsedTime;
            _world.Lighting = _lighting;
            for (int layer = 0; layer < s_captureDirections.Length; layer++)
            {
                _cancellation.Token.ThrowIfCancellationRequested();
                if (!IsCurrent()) throw new OperationCanceledException("OctahedralImposter.SourceChanged: the capture snapshot changed.");
                _preparedFrame = 0;
                XRCamera camera;
                using (ObjectCachePublicationScope cameraPublication = XRObjectBase.BeginIndependentObjectCachePublication())
                {
                    camera = BuildCaptureCamera(_worldBounds.Center, _extent, s_captureDirections[layer], _viewport.RenderPipeline!);
                    _cameraOwnership.Add(cameraPublication.CompleteWithOwnership());
                }
                _viewport.Camera = camera;
                _active = true;
                Task<SceneCaptureReadback> task;
                using (AbstractRenderer.EnterThreadCurrentScope(_renderer))
                {
                    task = _backend.CaptureSceneLayerAsync(new(_viewport, _framebuffers[layer], _color, layer,
                        _world, camera, IsCurrent, () => _preparedFrame != 0 && _preparedFrame == RuntimeEngine.Rendering.State.RenderFrameId,
                        _elapsedTime, _lighting, NormalizeAtlasOrigin: true), _cancellation.Token);
                    _capturedPipeline ??= _viewport.RenderPipeline;
                    _pipelineCommandGeneration = _capturedPipeline!.CommandGeneration;
                }
                SceneCaptureReadback result;
                try { result = await task; }
                catch when (_phaseFailure is not null) { throw new InvalidOperationException("OctahedralImposter.ScenePublicationFailed", _phaseFailure); }
                _active = false;
                if (result.ArrayLayer != layer || result.BackendGeneration != _backendGeneration || !IsCurrent())
                    throw new OperationCanceledException("OctahedralImposter.ObsoleteResult: the captured layer no longer belongs to this request.");
                _pixels[layer] = DataSource.FromArray(result.Rgba);
                _receipts[layer] = result.WithoutPixels();
                _progress?.Report(new(layer + 1, 26, $"Captured view {layer + 1} of 26."));
            }
            Task finalization;
            _progress?.Report(new(26, 26, "Finalizing octahedral texture mips."));
            using (AbstractRenderer.EnterThreadCurrentScope(_renderer))
            {
                if (!IsCurrent()) throw new OperationCanceledException("OctahedralImposter.SourceChanged: publication was canceled.");
                RetireCaptureScene();
                for (int layer = 0; layer < _pixels.Length; layer++)
                {
                    XRTexture2D slice = _color.Textures[layer];
                    slice.Name = $"OctahedralImpostorView{layer}";
                    slice.SamplerName = OctahedralBillboardAsset.ColorSamplerName;
                    slice.SizedInternalFormat = _color.SizedInternalFormat;
                    slice.MinFilter = ETexMinFilter.Linear;
                    slice.MagFilter = ETexMagFilter.Linear;
                    slice.UWrap = slice.VWrap = ETexWrapMode.ClampToEdge;
                    slice.Mipmaps = [new Mipmap2D(_settings.SheetSize, _settings.SheetSize, EPixelInternalFormat.Rgba16f,
                        EPixelFormat.Rgba, EPixelType.Float, allocateData: false) { Data = _pixels[layer] }];
                    slice.AutoGenerateMipmaps = true;
                }
                _color.AutoGenerateMipmaps = true;
                finalization = _backend.FinalizeSceneCaptureAsync(new(_color, _receipts, IsCurrent), _cancellation.Token);
            }
            await finalization;
            using var scope = AbstractRenderer.EnterThreadCurrentScope(_renderer);
            _progress?.Report(new(26, 26, "Octahedral capture complete."));
            if (!IsCurrent()) throw new OperationCanceledException("OctahedralImposter.SourceChanged: finalization was canceled.");
            using ObjectCachePublicationScope resultPublication = XRObjectBase.BeginIndependentObjectCachePublication();
            OctahedralBillboardAsset asset = new()
            {
                Name = _assetName, ColorViews = _color, CaptureDirections = [.. s_captureDirections],
                SourceSubmeshIndices = _indices, CaptureMode = _mode, CaptureResolution = _settings.SheetSize,
                CapturePadding = _settings.CapturePadding, OrthographicExtent = _extent,
                CaptureCenterWorld = _worldBounds.Center, CaptureCenterLocal = _centerLocal,
                WorldBounds = _worldBounds, LocalBounds = _localBounds, BillboardSize = new(_extent * 2, _extent * 2),
                SourceLayer = ResolveSourceLayer(_snapshot.Infos), SourceCastsShadows = ResolveSourceCastsShadows(_snapshot.Infos),
                SourceModelPath = _modelPath, SourceMaterialMode = ResolveSourceMaterialMode(_snapshot.Infos),
                RendererBackend = _renderer.GetType().Name,
                QualityNotes = "Directional layer blending without depth/parallax reprojection."
            };
            XRTexture[] previews = BuildPreviewTextures(_color);
            resultPublication.Complete();
            _published = true;
            return new(asset, _color, null, _localBounds, _worldBounds, _worldBounds.Center, _centerLocal,
                _extent, s_captureDirections, _indices, _mode, previews, _settings.CaptureDepth ? UnsupportedDepthMessage : null);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _active = false;
            using var scope = AbstractRenderer.EnterThreadCurrentScope(_renderer);
            List<Exception>? failures = null;
            void Release(Action release)
            {
                try { release(); }
                catch (Exception error) { (failures ??= []).Add(error); }
            }
            Release(RetireCaptureScene);
            Release(_snapshot.Dispose);
            if (_lighting is not null) Release(_lighting.Dispose);
            if (!_published)
            {
                if (_color is not null) Release(() => _color.Destroy(now: true));
                foreach (DataSource? pixels in _pixels)
                    if (pixels is not null) Release(pixels.Dispose);
                if (_color is not null)
                    foreach (XRTexture2D slice in _color.Textures) Release(() => slice.Destroy(now: true));
            }
            Array.Clear(_pixels);
            Array.Clear(_receipts);
            _cancellation?.Dispose();
            if (failures is not null) throw new AggregateException("Octahedral capture cleanup failed.", failures);
        }

        private void RetireCaptureScene()
        {
            if (_captureSceneRetired) return;
            _captureSceneRetired = true;
            _active = false;
            List<Exception>? failures = null;
            void Release(Action release)
            {
                try { release(); }
                catch (Exception error) { (failures ??= []).Add(error); }
            }
            if (_scheduling is not null)
            {
                Release(() => _scheduling.UnsubscribeViewportCollectVisible(Collect));
                Release(() => _scheduling.UnsubscribeWorldSwapBuffers(Publish));
                Release(() => _scheduling.UnsubscribeViewportSwapBuffers(Swap));
            }
            if (_viewport is not null) Release(_viewport.Destroy);
            if (_world is not null) Release(_world.Dispose);
            foreach (ObjectCacheOwnership camera in _cameraOwnership) Release(camera.Dispose);
            _cameraOwnership.Clear();
            if (_framebuffers is not null)
                foreach (XRFrameBuffer framebuffer in _framebuffers) Release(() => framebuffer.Destroy(now: true));
            if (_depth is not null) Release(() => _depth.Destroy(now: true));
            if (failures is not null) throw new AggregateException("Octahedral capture scene retirement failed.", failures);
        }
    }
}
