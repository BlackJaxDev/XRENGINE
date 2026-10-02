using XREngine.Extensions;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using XREngine.Components;
using XREngine.Data.Geometry;
using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Scene;

namespace XREngine.Components.Scene.Mesh
{
    [Serializable]
    [Category("Rendering")]
    [DisplayName("Model Renderer")]
    [Description("Draws complex 3D model assets with material and sub-mesh support.")]
    [XRComponentEditor("XREngine.Editor.ComponentEditors.ModelComponentEditor")]
    public partial class ModelComponent : RenderableComponent
    {
        private readonly ConcurrentDictionary<SubMesh, RenderableMesh> _meshLinks = new();
        private readonly List<RenderableMesh> _batchedRenderableAdds = [];
        private int _pendingModelMeshAddRangeCount;
        private volatile bool _pendingRuntimeMeshRebuild;
        private readonly object _runtimeMeshRebuildGate = new();
        private readonly List<RenderableMesh> _pendingRuntimeMeshRetirements = [];
        private int _runtimeMeshRebuildInProgress;
        private volatile bool _runtimeMeshRebuildRequested;
        private long _runtimeMeshRebuildGeneration;
        private Model? _subscribedModel;
        private volatile bool _runtimeMeshTeardown;
        private bool RuntimeMeshBuildInProgress => Volatile.Read(ref _runtimeMeshRebuildInProgress) != 0;

        private static readonly AsyncLocal<int> RuntimeMeshBuildSuppressionDepth = new();

        public static IDisposable EnterRuntimeMeshBuildSuppressionScope()
            => new RuntimeMeshBuildSuppressionScope();

        private sealed class RuntimeMeshBuildSuppressionScope : IDisposable
        {
            private bool _disposed;

            public RuntimeMeshBuildSuppressionScope()
                => RuntimeMeshBuildSuppressionDepth.Value++;

            public void Dispose()
            {
                if (_disposed)
                    return;

                RuntimeMeshBuildSuppressionDepth.Value = Math.Max(0, RuntimeMeshBuildSuppressionDepth.Value - 1);
                _disposed = true;
            }
        }

        private Model? _model;
        private Dictionary<int, float> _defaultBlendShapeWeights = [];
        private bool _updateSkinningWhenOffscreen;
        private bool _useSkinnedMotionVectors = true;
        private int _sourceSkinQuality;
        /// <summary>
        /// The 3D model asset containing geometry and materials.
        /// </summary>
        [Category("Model")]
        [DisplayName("Model")]
        [Description("The 3D model asset to render.")]
        public Model? Model
        {
            get => _model;
            set => SetField(ref _model, value);
        }

        /// <summary>
        /// Authored default blendshape weights, indexed exactly as they are stored by the source mesh.
        /// Values use the conventional percentage scale where 100 is full weight.
        /// </summary>
        public Dictionary<int, float> DefaultBlendShapeWeights
        {
            get => _defaultBlendShapeWeights;
            set
            {
                if (SetField(ref _defaultBlendShapeWeights, value ?? []))
                    ApplyDefaultBlendShapeWeights();
            }
        }

        /// <summary>
        /// Requests skinned deformation updates even when the renderer is outside the current view.
        /// </summary>
        public bool UpdateSkinningWhenOffscreen
        {
            get => _updateSkinningWhenOffscreen;
            set => SetField(ref _updateSkinningWhenOffscreen, value);
        }

        /// <summary>
        /// Preserves whether the source renderer authored skinned motion vectors.
        /// </summary>
        public bool UseSkinnedMotionVectors
        {
            get => _useSkinnedMotionVectors;
            set => SetField(ref _useSkinnedMotionVectors, value);
        }

        /// <summary>
        /// Source skin-quality selection retained for renderer diagnostics and backend policy.
        /// </summary>
        public int SourceSkinQuality
        {
            get => _sourceSkinQuality;
            set => SetField(ref _sourceSkinQuality, value);
        }

        private bool _renderBounds = false;
        /// <summary>
        /// When enabled, renders bounding volumes for debugging.
        /// </summary>
        [Category("Debug")]
        [DisplayName("Render Bounds")]
        [Description("When enabled, renders bounding volumes for debugging.")]
        public bool RenderBounds
        {
            get => _renderBounds;
            set => SetField(ref _renderBounds, value);
        }

        private IReadOnlyDictionary<SubMesh, RenderableMesh> MeshLinks => _meshLinks;

        public bool TryGetSourceSubMesh(RenderableMesh renderable, [NotNullWhen(true)] out SubMesh? subMesh)
        {
            subMesh = null;
            foreach (var kvp in _meshLinks)
            {
                if (ReferenceEquals(kvp.Value, renderable))
                {
                    subMesh = kvp.Key;
                    return true;
                }
            }

            return false;
        }

        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (change)
            {
                switch (propName)
                {
                    case nameof(Model):
                        if (Model != null)
                            UnsubscribeModelMeshEvents(Model);
                        break;
                }
            }
            return change;
        }
        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            if (propName == nameof(World))
                RebuildRuntimeMeshesForResolvedWorld();

            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Model):
                    if (CanBuildRuntimeMeshes())
                    {
                        _pendingRuntimeMeshRebuild = false;
                        OnModelChanged();
                    }
                    else
                    {
                        _pendingRuntimeMeshRebuild = true;
                    }
                    break;
                case nameof(RenderBounds):
                    foreach (RenderableMesh mesh in Meshes)
                        mesh.RenderBounds = RenderBounds;
                    break;
                case nameof(RenderUtilizedBoneDiamonds):
                    SyncBoneDiamondRenderInfoWithWorld();
                    break;
                case nameof(World):
                    SyncBoneDiamondRenderInfoWithWorld();
                    break;
            }
        }

        public event Action? ModelChanged;

        public void RebuildRuntimeMeshes()
            => OnModelChanged();

        private bool CanBuildRuntimeMeshes()
            => RuntimeMeshBuildSuppressionDepth.Value == 0
            && SceneNode is not null
            && !SceneNode.IsTransformNull;

        private void RebuildRuntimeMeshesForResolvedWorld()
        {
            if (World is null || Model is null || !CanBuildRuntimeMeshes())
                return;

            bool rebuild = _pendingRuntimeMeshRebuild;
            for (int index = 0; !rebuild && index < Meshes.Count; index++)
                rebuild = Meshes[index].RequiresSceneTransformRebind();
            if (!rebuild)
                return;

            // YAML model members can be read before their owning parent hierarchy.
            // World attachment resolves those carriers once, before registration;
            // an already-bound instance keeps its renderer and shared geometry.
            _pendingRuntimeMeshRebuild = false;
            OnModelChanged();
        }

        private void OnModelChanged()
        {
            Interlocked.Increment(ref _runtimeMeshRebuildGeneration);
            _runtimeMeshRebuildRequested = true;
            // A callback already holding a wrapper's lifetime gate must not wait
            // for the publication gate while retirement waits for that wrapper.
            // The owning cold transaction observes this request before publishing.
            if (_runtimeMeshTeardown || Interlocked.CompareExchange(ref _runtimeMeshRebuildInProgress, 1, 0) != 0)
                return;
            bool ownsAdmission = true;
            lock (_runtimeMeshRebuildGate)
            {
                using var profile = RuntimeEngine.Profiler.Start("ModelComponent.ModelChanged");
                long start = Stopwatch.GetTimestamp();
                try
                {
                    // Structural callbacks may replace the model during retirement,
                    // binding or registration. Coalesce those requests at this cold
                    // boundary instead of publishing a stale outer construction.
                    for (int attempt = 0; attempt < 8; attempt++)
                    {
                        _runtimeMeshRebuildRequested = false;
                        long generation = _runtimeMeshRebuildGeneration;
                        Model? model = Model;
                        SubMesh[] sources = model is null ? [] : [.. model.Meshes];
                        if (_subscribedModel is not null)
                            UnsubscribeModelMeshEvents(_subscribedModel);

                        RetainCurrentRuntimeMeshesForRetirement();
                        RetirePendingRuntimeMeshes();
                        if (!IsCurrentModelBuild(model, generation, sources))
                            continue;

                        List<(SubMesh Source, RenderableMesh Runtime)> prepared = new(sources.Length);
                        try
                        {
                            foreach (SubMesh source in sources)
                            {
                                RenderableMesh renderable = CreateRenderableMesh(source);
                                prepared.Add((source, renderable));
                                if (!IsCurrentModelBuild(model, generation, sources))
                                    break;
                            }
                        }
                        catch (Exception constructionFailure)
                        {
                            RetainPreparedRuntimeMeshes(prepared);
                            try { RetirePendingRuntimeMeshes(); }
                            catch (Exception cleanupFailure)
                            { constructionFailure.Data["RenderableMeshCleanupFailures"] = cleanupFailure; }
                            _pendingRuntimeMeshRebuild = true;
                            throw;
                        }

                        if (!IsCurrentModelBuild(model, generation, sources))
                        {
                            RetainPreparedRuntimeMeshes(prepared);
                            RetirePendingRuntimeMeshes();
                            continue;
                        }

                        try
                        {
                            List<RenderableMesh> renderables = new(prepared.Count);
                            foreach (var item in prepared)
                                renderables.Add(item.Runtime);
                            AddMeshesWithoutEvents(renderables);
                            if (!IsCurrentModelBuild(model, generation, sources))
                            {
                                RetainCurrentRuntimeMeshesForRetirement();
                                RetirePendingRuntimeMeshes();
                                continue;
                            }
                            foreach (var item in prepared)
                                _meshLinks.TryAdd(item.Source, item.Runtime);
                            if (model is not null)
                                SubscribeModelMeshEvents(model);
                            ApplyDefaultBlendShapeWeights();
                            ModelChanged?.Invoke();
                        }
                        catch (Exception publicationFailure)
                        {
                            RetainPreparedRuntimeMeshes(prepared);
                            RetainCurrentRuntimeMeshesForRetirement();
                            try { RetirePendingRuntimeMeshes(); }
                            catch (Exception cleanupFailure)
                            { publicationFailure.Data["RenderableMeshCleanupFailures"] = cleanupFailure; }
                            _pendingRuntimeMeshRebuild = true;
                            throw;
                        }
                        if (IsCurrentModelBuild(model, generation, sources))
                        {
                            _pendingRuntimeMeshRebuild = false;
                            ModelRenderDiagnostics.LogComponentPublished(this, "ModelChanged", sources.Length, RenderedObjects.Length, start);
                            WarnIfSlowModelPublish("ModelChanged", start, sources.Length, RenderedObjects.Length);
                            // Release admission before checking for a last-moment
                            // request. Either this owner reclaims it within the same
                            // finite budget or the requesting caller becomes owner.
                            Interlocked.Exchange(ref _runtimeMeshRebuildInProgress, 0);
                            ownsAdmission = false;
                            if (IsCurrentModelBuild(model, generation, sources) ||
                                Interlocked.CompareExchange(ref _runtimeMeshRebuildInProgress, 1, 0) != 0)
                                return;
                            ownsAdmission = true;
                        }
                    }

                    RetainCurrentRuntimeMeshesForRetirement();
                    RetirePendingRuntimeMeshes();
                    _pendingRuntimeMeshRebuild = true;
                    throw new InvalidOperationException("ModelComponent.RuntimeMeshPublicationUnstable: structural callbacks changed the model repeatedly; retry at a cold world/model boundary.");
                }
                catch (Exception publicationFailure)
                {
                    _pendingRuntimeMeshRebuild = true;
                    try { RestoreAuthoritativeModelSubscriptionsAfterFailure(); }
                    catch (Exception subscriptionFailure)
                    { publicationFailure.Data["ModelSubscriptionRecoveryFailure"] = subscriptionFailure; }
                    throw;
                }
                finally
                {
                    if (ownsAdmission)
                        Interlocked.Exchange(ref _runtimeMeshRebuildInProgress, 0);
                }
            }
        }

        private bool IsCurrentModelBuild(Model? model, long generation, SubMesh[] sources)
        {
            if (_runtimeMeshTeardown || !ReferenceEquals(Model, model) || _runtimeMeshRebuildGeneration != generation || _runtimeMeshRebuildRequested)
                return false;
            if (model is null)
                return sources.Length == 0;
            if (model.Meshes.Count != sources.Length)
                return false;
            for (int index = 0; index < sources.Length; index++)
                if (!ReferenceEquals(model.Meshes[index], sources[index]))
                    return false;
            return true;
        }

        private void RestoreAuthoritativeModelSubscriptionsAfterFailure()
        {
            // Failed construction has no renderers to publish, but source edits
            // remain a legitimate cold retry boundary for the authoritative model.
            for (int attempt = 0; attempt < 8; attempt++)
            {
                Model? model = Model;
                long generation = Interlocked.Read(ref _runtimeMeshRebuildGeneration);
                if (_subscribedModel is not null && (_runtimeMeshTeardown || !ReferenceEquals(_subscribedModel, model)))
                    UnsubscribeModelMeshEvents(_subscribedModel);
                if (_runtimeMeshTeardown)
                    return;
                if (model is not null)
                    SubscribeModelMeshEvents(model);
                if (ReferenceEquals(Model, model) && generation == Interlocked.Read(ref _runtimeMeshRebuildGeneration))
                    return;
            }
            if (_subscribedModel is not null)
                UnsubscribeModelMeshEvents(_subscribedModel);
            throw new InvalidOperationException("ModelComponent.SubscriptionRecoveryUnstable: authoritative model changed repeatedly while restoring cold retry observers.");
        }

        private void RetainRuntimeMeshRetirement(RenderableMesh mesh)
        {
            lock (_runtimeMeshRebuildGate)
                if (!_pendingRuntimeMeshRetirements.Contains(mesh))
                {
                    _pendingRuntimeMeshRetirements.Add(mesh);
                    _pendingRuntimeMeshRebuild = true;
                }
        }

        private void RetainPreparedRuntimeMeshes(List<(SubMesh Source, RenderableMesh Runtime)> prepared)
        {
            foreach (var item in prepared)
                RetainRuntimeMeshRetirement(item.Runtime);
        }

        private void RetainCurrentRuntimeMeshesForRetirement()
        {
            for (int index = 0; index < Meshes.Count; index++)
                RetainRuntimeMeshRetirement(Meshes[index]);
            foreach (var pair in _meshLinks)
                RetainRuntimeMeshRetirement(pair.Value);
            foreach (RenderableMesh mesh in _pendingRuntimeMeshRetirements)
                mesh.RequestRetirement();
            // Each wrapper now owns its former target identity, including an
            // unregister interrupted after the public WorldInstance value changed.
            using (XRBase.SuppressPropertyNotifications())
                ClearMeshesWithoutEvents();
            _meshLinks.Clear();
            ResetPendingModelMeshAddRange();
        }

        private void RetirePendingRuntimeMeshes()
        {
            List<Exception>? failures = null;
            for (int index = _pendingRuntimeMeshRetirements.Count - 1; index >= 0; index--)
            {
                RenderableMesh mesh = _pendingRuntimeMeshRetirements[index];
                try { mesh.Dispose(); }
                catch (Exception failure) { (failures ??= []).Add(failure); }
                if (mesh.IsFullyRetired)
                    _pendingRuntimeMeshRetirements.RemoveAt(index);
            }
            if (failures is not null)
                throw new AggregateException("ModelComponent.RuntimeMeshRetirementIncomplete: pending runtime ownership is retained for retry.", failures);
        }

        protected override void OnDestroying()
        {
            _runtimeMeshTeardown = true;
            Interlocked.Increment(ref _runtimeMeshRebuildGeneration);
            if (Interlocked.CompareExchange(ref _runtimeMeshRebuildInProgress, 1, 0) != 0)
                throw new InvalidOperationException("ModelComponent.TeardownPendingPublication: retry destruction after the active cold publication retires its staged ownership.");
            lock (_runtimeMeshRebuildGate)
            {
                try
                {
                    if (_subscribedModel is not null)
                        UnsubscribeModelMeshEvents(_subscribedModel);
                    RetainCurrentRuntimeMeshesForRetirement();
                    List<Exception>? failures = null;
                    try { RetirePendingRuntimeMeshes(); }
                    catch (Exception failure) { (failures ??= []).Add(failure); }
                    try { base.OnDestroying(); }
                    catch (Exception failure) { (failures ??= []).Add(failure); }
                    if (failures is not null)
                        throw new AggregateException("ModelComponent.RuntimeMeshTeardownIncomplete: pending runtime ownership is retained for retry.", failures);
                }
                finally { Interlocked.Exchange(ref _runtimeMeshRebuildInProgress, 0); }
            }
        }

        protected override void OwningSceneNodePostDeserialize()
        {
            base.OwningSceneNodePostDeserialize();

            if (Model is not null)
            {
                _pendingRuntimeMeshRebuild = false;
                OnModelChanged();
            }
        }

        protected override void AddedToSceneNode(SceneNode sceneNode)
        {
            base.AddedToSceneNode(sceneNode);

            if (_pendingRuntimeMeshRebuild && Model is not null && !sceneNode.IsTransformNull)
            {
                _pendingRuntimeMeshRebuild = false;
                OnModelChanged();
            }
        }

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
            SyncBoneDiamondRenderInfoWithWorld();
            ModelRenderDiagnostics.LogComponentActivated(this);
        }

        private void AddMesh(SubMesh item)
        {
            if (_runtimeMeshTeardown)
                return;
            if (_pendingRuntimeMeshRebuild || Interlocked.CompareExchange(ref _runtimeMeshRebuildInProgress, 1, 0) != 0)
            {
                OnModelChanged();
                return;
            }
            lock (_runtimeMeshRebuildGate)
            {
                long generation = _runtimeMeshRebuildGeneration;
                Exception? failure = null;
                RenderableMesh? pending = null;
                try
                {
                    if (_runtimeMeshTeardown || Model is not { } model || !model.Meshes.Contains(item) || _meshLinks.ContainsKey(item))
                        return;
                    RenderableMesh mesh = CreateRenderableMesh(item);
                    pending = mesh;
                    if (!ReferenceEquals(Model, model) || generation != _runtimeMeshRebuildGeneration || !model.Meshes.Contains(item))
                    {
                        RetainRuntimeMeshRetirement(mesh);
                        RetirePendingRuntimeMeshes();
                        return;
                    }

                    if (_pendingModelMeshAddRangeCount > 0)
                    {
                        if (Meshes.Add(mesh, reportAdded: false, reportModified: false))
                        {
                            _meshLinks.TryAdd(item, mesh);
                            _batchedRenderableAdds.Add(mesh);
                        }
                        else
                        {
                            mesh.Dispose();
                        }

                        _pendingModelMeshAddRangeCount--;
                        if (_pendingModelMeshAddRangeCount == 0)
                            CompleteModelMeshAddRange();
                        return;
                    }

                    if (!Meshes.Add(mesh))
                    {
                        mesh.Dispose();
                        return;
                    }

                    _meshLinks.TryAdd(item, mesh);
                    ModelChanged?.Invoke();
                }
                catch (Exception operationFailure)
                {
                    failure = operationFailure;
                    if (pending is not null)
                        RetainRuntimeMeshRetirement(pending);
                    RetainCurrentRuntimeMeshesForRetirement();
                    try { RetirePendingRuntimeMeshes(); }
                    catch (Exception cleanupFailure) { operationFailure.Data["RenderableMeshCleanupFailures"] = cleanupFailure; }
                    _pendingRuntimeMeshRebuild = true;
                    try { RestoreAuthoritativeModelSubscriptionsAfterFailure(); }
                    catch (Exception subscriptionFailure) { operationFailure.Data["ModelSubscriptionRecoveryFailure"] = subscriptionFailure; }
                    throw;
                }
                finally
                {
                    Interlocked.Exchange(ref _runtimeMeshRebuildInProgress, 0);
                    if ((_runtimeMeshRebuildRequested || generation != Interlocked.Read(ref _runtimeMeshRebuildGeneration)) && failure is null)
                        OnModelChanged();
                }
            }
        }

        private RenderableMesh CreateRenderableMesh(SubMesh item)
            => new(item, this, RetainRuntimeMeshRetirement)
            {
                //RootTransform = item.RootTransform
            };

        private void BeginModelMeshAddRange(IEnumerable<SubMesh> items)
        {
            if (RuntimeMeshBuildInProgress || _runtimeMeshTeardown)
                return;
            if (!Monitor.TryEnter(_runtimeMeshRebuildGate))
            {
                OnModelChanged();
                return;
            }
            try
            {
                if (_runtimeMeshTeardown || RuntimeMeshBuildInProgress || !ReferenceEquals(Model, _subscribedModel))
                    return;
                int count = CountItems(items);
                if (count <= 1)
                    return;

                if (_pendingModelMeshAddRangeCount == 0)
                    _batchedRenderableAdds.Clear();

                _pendingModelMeshAddRangeCount += count;
            }
            finally { Monitor.Exit(_runtimeMeshRebuildGate); }
        }

        private void CompleteModelMeshAddRange()
        {
            using var t = RuntimeEngine.Profiler.Start("ModelComponent.AddMeshRange");
            long start = Stopwatch.GetTimestamp();
            int batchedCount = _batchedRenderableAdds.Count;

            if (_batchedRenderableAdds.Count > 0)
                AppendRenderedObjects(_batchedRenderableAdds);

            _batchedRenderableAdds.Clear();
            ModelChanged?.Invoke();
            ModelRenderDiagnostics.LogComponentPublished(
                this,
                "CompleteModelMeshAddRange",
                batchedCount,
                RenderedObjects.Length,
                start);
            WarnIfSlowModelPublish(
                "CompleteModelMeshAddRange",
                start,
                batchedCount,
                RenderedObjects.Length);
        }

        private void ResetPendingModelMeshAddRange()
        {
            _pendingModelMeshAddRangeCount = 0;
            _batchedRenderableAdds.Clear();
        }

        private void SubscribeModelMeshEvents(Model model)
        {
            _subscribedModel = model;
            model.Meshes.PostAddedRange -= BeginModelMeshAddRange;
            model.Meshes.PostAnythingAdded -= AddMesh;
            model.Meshes.PostAnythingRemoved -= RemoveMesh;

            model.Meshes.PostAddedRange += BeginModelMeshAddRange;
            model.Meshes.PostAnythingAdded += AddMesh;
            model.Meshes.PostAnythingRemoved += RemoveMesh;
        }

        private void UnsubscribeModelMeshEvents(Model model)
        {
            if (ReferenceEquals(_subscribedModel, model))
                _subscribedModel = null;
            model.Meshes.PostAddedRange -= BeginModelMeshAddRange;
            model.Meshes.PostAnythingAdded -= AddMesh;
            model.Meshes.PostAnythingRemoved -= RemoveMesh;
            ResetPendingModelMeshAddRange();
        }

        private static int CountItems(IEnumerable<SubMesh> items)
        {
            if (items is ICollection<SubMesh> collection)
                return collection.Count;
            if (items is IReadOnlyCollection<SubMesh> readOnlyCollection)
                return readOnlyCollection.Count;

            int count = 0;
            foreach (SubMesh _ in items)
                count++;
            return count;
        }

        private static void WarnIfSlowModelPublish(string operation, long startTimestamp, int sourceMeshCount, int renderedObjectCount)
        {
            double elapsedMs = (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;
            if (elapsedMs <= 8.0)
                return;

            Debug.RenderingWarningEvery(
                $"ModelComponent.{operation}.Slow",
                TimeSpan.FromSeconds(2.0),
                "[ModelComponent] Slow model publish path: operation={0}, sourceMeshes={1}, renderedObjects={2}, elapsedMs={3:F2}.",
                operation,
                sourceMeshCount,
                renderedObjectCount,
                elapsedMs);
        }
        private void RemoveMesh(SubMesh item)
        {
            if (_runtimeMeshTeardown)
                return;
            if (_pendingRuntimeMeshRebuild || Interlocked.CompareExchange(ref _runtimeMeshRebuildInProgress, 1, 0) != 0)
            {
                OnModelChanged();
                return;
            }
            lock (_runtimeMeshRebuildGate)
            {
                long generation = Interlocked.Read(ref _runtimeMeshRebuildGeneration);
                Exception? failure = null;
                try
                {
                    if (_runtimeMeshTeardown || Model?.Meshes.Contains(item) == true || !_meshLinks.TryRemove(item, out RenderableMesh? mesh))
                        return;
                    RetainRuntimeMeshRetirement(mesh);
                    mesh.RequestRetirement();
                    Meshes.Remove(mesh);
                    RetirePendingRuntimeMeshes();
                    if (generation == Interlocked.Read(ref _runtimeMeshRebuildGeneration) && !_runtimeMeshRebuildRequested)
                        _pendingRuntimeMeshRebuild = false;
                    ModelChanged?.Invoke();
                }
                catch (Exception removalFailure)
                {
                    failure = removalFailure;
                    try { RetirePendingRuntimeMeshes(); }
                    catch (Exception cleanupFailure) { removalFailure.Data["RenderableMeshCleanupFailures"] = cleanupFailure; }
                    _pendingRuntimeMeshRebuild = true;
                    try { RestoreAuthoritativeModelSubscriptionsAfterFailure(); }
                    catch (Exception subscriptionFailure) { removalFailure.Data["ModelSubscriptionRecoveryFailure"] = subscriptionFailure; }
                    throw;
                }
                finally
                {
                    Interlocked.Exchange(ref _runtimeMeshRebuildInProgress, 0);
                    if ((_runtimeMeshRebuildRequested || generation != Interlocked.Read(ref _runtimeMeshRebuildGeneration)) && failure is null)
                        OnModelChanged();
                }
            }
        }

        [RequiresDynamicCode("")]
        public float? Intersect(Segment segment, out Triangle? triangle)
        {
            triangle = null;
            float? closest = null;
            foreach (RenderableMesh mesh in Meshes)
            {
                var m = mesh.CurrentLODMesh;
                if (m is null)
                    continue;

                float? distance = mesh.Intersect(mesh.GetLocalSegment(segment, m.HasSkinning), out triangle);
                if (distance.HasValue && (!closest.HasValue || distance < closest))
                    closest = distance;
            }
            return closest;
        }

        public IEnumerable<XRMeshRenderer> GetAllRenderersWhere(Predicate<XRMeshRenderer> predicate)
        {
            List<XRMeshRenderer> renderers = [];
            foreach (RenderableMesh mesh in Meshes)
                foreach (RenderableMesh.RenderableLOD lod in mesh.GetLodSnapshot())
                {
                    XRMeshRenderer renderer = lod.Renderer;
                    if (predicate(renderer))
                        renderers.Add(renderer);
                }

            return renderers;
        }

        public void SetBlendShapeWeight(string blendshapeName, float percentage, StringComparison comp = StringComparison.InvariantCultureIgnoreCase)
        {
            //Debug.Out($"SetBlendShapeWeight: {blendshapeName} {percentage}");

            bool HasMatchingBlendshape(XRMeshRenderer x)
                => (x?.Mesh?.HasBlendshapes ?? false) && x.Mesh!.BlendshapeNames.Contains(blendshapeName, comp);
            var rends = GetAllRenderersWhere(HasMatchingBlendshape);
            //if (!rends.Any())
            //{
            //    Debug.LogWarning($"No renderers found with blendshape {blendshapeName}");
            //    return;
            //}
            rends.ForEach(x => x.SetBlendshapeWeight(blendshapeName, percentage));
        }

        /// <summary>
        /// Sets and persists an authored default blendshape weight by source index.
        /// </summary>
        public void SetDefaultBlendShapeWeight(int index, float percentage)
        {
            if (index < 0 || !float.IsFinite(percentage))
                return;

            _defaultBlendShapeWeights[index] = percentage;
            foreach (RenderableMesh mesh in Meshes)
                foreach (RenderableMesh.RenderableLOD lod in mesh.GetLodSnapshot())
                    lod.Renderer.SetBlendshapeWeight((uint)index, percentage);
        }

        private void ApplyDefaultBlendShapeWeights()
        {
            if (_defaultBlendShapeWeights.Count == 0)
                return;

            foreach ((int index, float percentage) in _defaultBlendShapeWeights)
            {
                if (index < 0 || !float.IsFinite(percentage))
                    continue;

                foreach (RenderableMesh mesh in Meshes)
                    foreach (RenderableMesh.RenderableLOD lod in mesh.GetLodSnapshot())
                        lod.Renderer.SetBlendshapeWeight((uint)index, percentage);
            }
        }
        public void SetBlendShapeWeightNormalized(string blendshapeName, float weight, StringComparison comp = StringComparison.InvariantCultureIgnoreCase)
        {
            //Debug.Out($"SetBlendShapeWeightNormalized: {blendshapeName} {weight}");

            bool HasMatchingBlendshape(XRMeshRenderer x)
                => (x?.Mesh?.HasBlendshapes ?? false) && x.Mesh!.BlendshapeNames.Contains(blendshapeName, comp);
            var rends = GetAllRenderersWhere(HasMatchingBlendshape);
            //if (!rends.Any())
            //{
            //    Debug.LogWarning($"No renderers found with blendshape {blendshapeName}");
            //    return;
            //}
            rends.ForEach(x => x.SetBlendshapeWeightNormalized(blendshapeName, weight));
        }
    }
}
