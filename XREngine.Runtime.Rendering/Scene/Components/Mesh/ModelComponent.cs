using XREngine.Extensions;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using XREngine.Components;
using XREngine.Data.Geometry;
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
        private bool _pendingRuntimeMeshRebuild;

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

        private void OnModelChanged()
        {
            using var t = RuntimeEngine.Profiler.Start("ModelComponent.ModelChanged");
            long start = Stopwatch.GetTimestamp();

            Model? model = Model;
            int modelMeshCount = model?.Meshes.Count ?? 0;

            if (model is not null)
                UnsubscribeModelMeshEvents(model);

            // Dispose any remaining renderable meshes not already cleaned up in
            // OnPropertyChanging. This unsubscribes bone transform events and destroys
            // GPU buffers, preventing leaked subscriptions and stale SSBO references.
            RenderableMesh[] oldMeshes = Meshes.Count == 0 ? [] : Meshes.ToArray();
            ClearMeshesWithoutEvents();
            foreach (RenderableMesh mesh in oldMeshes)
                mesh.Dispose();
            _meshLinks.Clear();
            ResetPendingModelMeshAddRange();

            if (model is null)
            {
                ModelChanged?.Invoke();
                return;
            }

            List<RenderableMesh> renderableMeshes = new(model.Meshes.Count);
            foreach (SubMesh mesh in model.Meshes)
            {
                RenderableMesh rendMesh = CreateRenderableMesh(mesh);
                renderableMeshes.Add(rendMesh);
                _meshLinks.TryAdd(mesh, rendMesh);
            }

            AddMeshesWithoutEvents(renderableMeshes);
            SubscribeModelMeshEvents(model);

            ModelChanged?.Invoke();
            ModelRenderDiagnostics.LogComponentPublished(
                this,
                "ModelChanged",
                modelMeshCount,
                RenderedObjects.Length,
                start);
            WarnIfSlowModelPublish(
                "ModelChanged",
                start,
                modelMeshCount,
                RenderedObjects.Length);
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
            RenderableMesh mesh = CreateRenderableMesh(item);

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

        private RenderableMesh CreateRenderableMesh(SubMesh item)
            => new(item, this)
            {
                //RootTransform = item.RootTransform
            };

        private void BeginModelMeshAddRange(IEnumerable<SubMesh> items)
        {
            int count = CountItems(items);
            if (count <= 1)
                return;

            if (_pendingModelMeshAddRangeCount == 0)
                _batchedRenderableAdds.Clear();

            _pendingModelMeshAddRangeCount += count;
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
            model.Meshes.PostAddedRange -= BeginModelMeshAddRange;
            model.Meshes.PostAnythingAdded -= AddMesh;
            model.Meshes.PostAnythingRemoved -= RemoveMesh;

            model.Meshes.PostAddedRange += BeginModelMeshAddRange;
            model.Meshes.PostAnythingAdded += AddMesh;
            model.Meshes.PostAnythingRemoved += RemoveMesh;
        }

        private void UnsubscribeModelMeshEvents(Model model)
        {
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
            if (_meshLinks.TryRemove(item, out RenderableMesh? mesh))
            {
                Meshes.Remove(mesh);
                mesh.Dispose();
                ModelChanged?.Invoke();
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
