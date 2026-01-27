using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using XREngine.Components;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Trees;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using YamlDotNet.Serialization;
using Vector3 = System.Numerics.Vector3;

namespace XREngine.Scene
{
    /// <summary>
    /// Represents a scene with special optimizations for rendering in 2D.
    /// </summary>
    public class VisualScene2D : VisualScene
    {
        public VisualScene2D() { }

        [YamlIgnore]
        public Quadtree<RenderInfo2D> RenderTree { get; } = new Quadtree<RenderInfo2D>(new BoundingRectangleF());

        public void SetBounds(BoundingRectangleF bounds)
            => RenderTree.Remake(bounds);

        public override void DebugRender(XRCamera? camera, bool onlyContainingItems = false)
            => RenderTree.DebugRender(camera?.GetOrthoCameraBounds(), onlyContainingItems, RenderAABB);

        private void RenderAABB(Vector2 extents, Vector2 center, ColorF4 color)
            => Engine.Rendering.Debug.RenderQuad(new Vector3(center, 0.0f) + AbstractRenderer.UIPositionBias, AbstractRenderer.UIRotation, extents, false, color);

        public override IRenderTree GenericRenderTree => RenderTree;

        public void Raycast(
            Vector2 screenPoint,
            SortedDictionary<float, List<(IRenderable item, object? data)>> items)
            => RenderTree.Raycast(screenPoint, items);

        public override void CollectRenderedItems(
            RenderCommandCollection meshRenderCommands,
            XRCamera? activeCamera,
            bool cullWithFrustum,
            Func<XRCamera>? cullingCameraOverride,
            IVolume? collectionVolumeOverride,
            bool collectMirrors)
        {
            var cullingCamera = cullingCameraOverride?.Invoke() ?? activeCamera;
            CollectRenderedItems(meshRenderCommands, cullWithFrustum ? cullingCamera?.GetOrthoCameraBounds() : null, activeCamera);
        }
        /// <summary>
        /// Collects render commands for all renderables in the scene that intersect with the given volume.
        /// If the volume is null, all renderables are collected.
        /// Typically, the collectionVolume is the camera's frustum.
        /// </summary>
        /// <param name="commands"></param>
        /// <param name="collectionVolume"></param>
        /// <param name="camera"></param>
        public void CollectRenderedItems(RenderCommandCollection commands, BoundingRectangleF? collectionVolume, XRCamera? camera)
        {
            // Ensure pending UI renderables are flushed even when GlobalCollectVisible isn't called
            // (e.g., screen-space UI path).
            ProcessPendingRenderableOperations();

            bool IntersectionTest(RenderInfo2D item, BoundingRectangleF cullingVolume, bool containsOnly)
            {
                if (item.CullingVolume is null)
                    return false;
                
                var contain = cullingVolume.ContainmentOf(item.CullingVolume.Value);
                return containsOnly ? contain == EContainment.Contains : contain != EContainment.Disjoint;
            }
            void AddRenderCommands(ITreeItem item)
            {
                if (item is RenderInfo renderable)
                    renderable.CollectCommands(commands, camera);
            }
            if (collectionVolume is null)
                RenderTree.CollectAll(AddRenderCommands);
            else
                RenderTree.CollectVisible(collectionVolume, false, AddRenderCommands, IntersectionTest);
        }

        public IReadOnlyList<RenderInfo2D> Renderables => _renderables;
        private readonly List<RenderInfo2D> _renderables = [];
        private readonly HashSet<RenderInfo2D> _renderableSet = [];
        private readonly ConcurrentQueue<(RenderInfo2D renderable, bool add)> _pendingRenderableOperations = new(); // staged until PreCollectVisible runs on the collect visible thread

        public void AddRenderable(RenderInfo2D renderable)
            => _pendingRenderableOperations.Enqueue((renderable, true));

        public void RemoveRenderable(RenderInfo2D renderable)
            => _pendingRenderableOperations.Enqueue((renderable, false));

        public override void GlobalCollectVisible()
        {
            base.GlobalCollectVisible();
            ProcessPendingRenderableOperations();
        }

        public override void GlobalPreRender()
        {
            base.GlobalPreRender();
        }

        public override IEnumerator<RenderInfo> GetEnumerator()
        {
            foreach (var renderable in _renderables)
                yield return renderable;
        }

        private void ProcessPendingRenderableOperations()
        {
            while (_pendingRenderableOperations.TryDequeue(out var operation))
            {
                if (operation.add)
                {
                    if (_renderableSet.Add(operation.renderable))
                    {
                        _renderables.Add(operation.renderable);
                        RenderTree.Add(operation.renderable);
                    }
                }
                else if (_renderableSet.Remove(operation.renderable))
                {
                    _renderables.Remove(operation.renderable);
                    RenderTree.Remove(operation.renderable);
                }
            }
        }
    }
}