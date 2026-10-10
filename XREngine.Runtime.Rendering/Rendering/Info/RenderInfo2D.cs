using System.Numerics;
using XREngine.Components;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Trees;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using YamlDotNet.Serialization;

namespace XREngine.Rendering.Info
{
    public class RenderInfo2D : RenderInfo, IQuadtreeItem, IComparable<RenderInfo2D>, IRuntimeRenderInfo2DRegistrationItem
    {
        private IRuntimeRenderInfo2DRegistrationTarget? _registeredCanvas;
        private bool _reconcilingCanvasRegistration;

        private int _layerIndex = 0;
        private int _indexWithinLayer = 0;
        private BoundingRectangleF? _cullingVolume;
        private QuadtreeNodeBase? _quadtreeNode;

        public override ITreeNode? TreeNode => QuadtreeNode;

        public static Func<IRenderable, RenderCommand[], RenderInfo2D>? ConstructorOverride { get; set; } = null;

        private RenderInfo2D() : base(null) { }

        public static RenderInfo2D New(IRenderable owner, params RenderCommand[] renderCommands)
            => ConstructorOverride?.Invoke(owner, renderCommands) ?? new RenderInfo2D(owner, renderCommands);

        protected RenderInfo2D(IRenderable? owner, params RenderCommand[] renderCommands)
            : base(owner, renderCommands) { }

        /// <summary>
        /// Used to render objects in the same pass in a certain order.
        /// Smaller value means rendered sooner, zero (exactly) means it doesn't matter.
        /// </summary>
        public int LayerIndex 
        {
            get => _layerIndex;
            set => SetField(ref _layerIndex, value);
        }

        public int IndexWithinLayer
        {
            get => _indexWithinLayer;
            set => SetField(ref _indexWithinLayer, value);
        }

        /// <summary>
        /// The axis-aligned bounding box for this UI component.
        /// </summary>
        public BoundingRectangleF? CullingVolume
        {
            get => _cullingVolume;
            set => SetField(ref _cullingVolume, value);
        }

        [YamlIgnore]
        public QuadtreeNodeBase? QuadtreeNode
        {
            get => _quadtreeNode;
            set => SetField(ref _quadtreeNode, value);
        }

        public bool DeeperThan(RenderInfo2D other)
        {
            if (other is null)
                return true;

            if (LayerIndex > other.LayerIndex)
                return true;
            else if (LayerIndex == other.LayerIndex && IndexWithinLayer > other.IndexWithinLayer)
                return true;

            return false;
        }

        public virtual bool AllowRender(BoundingRectangleF? cullingVolume, RenderCommandCollection passes, IRuntimeCullingCamera? camera)
            => IsVisible && (cullingVolume is null || CullingVolume is null || cullingVolume.Value.Intersects(CullingVolume.Value));

        public bool Intersects(BoundingRectangleF cullingVolume, bool containsOnly)
        {
            if (CullingVolume is null)
                return true;

            return containsOnly
                ? cullingVolume.Contains(CullingVolume.Value)
                : cullingVolume.Intersects(CullingVolume.Value);
        }

        public bool Contains(Vector2 point)
            => CullingVolume?.Contains(point) ?? false;

        public Vector2 ClosestPoint(Vector2 point)
            => Contains(point)
                ? point
                : CullingVolume?.ClosestPoint(point) ?? point;

        public bool DeeperThan(IQuadtreeItem other)
            => other is RenderInfo2D other2D && DeeperThan(other2D);

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            try { base.OnPropertyChanged(propName, prev, field); }
            finally
            {
                if (propName is nameof(UserInterfaceCanvas) or nameof(IsVisible))
                    ReconcileCanvasRegistration(disposing: false);
            }
            switch (propName)
            {
                case nameof(CullingVolume):
                    QuadtreeNode?.QueueItemMoved(this);
                    break;
            }
        }

        protected override void ReleaseCanvasRegistration()
        {
            ReconcileCanvasRegistration(disposing: true);
            ClearCanvasRegistrationField();
        }

        private void ReconcileCanvasRegistration(bool disposing)
        {
            if (_reconcilingCanvasRegistration)
                return;
            _reconcilingCanvasRegistration = true;
            try
            {
                for (int attempt = 0; attempt < 16; attempt++)
                {
                    IRuntimeRenderInfo2DRegistrationTarget? desired =
                        !disposing && !IsDisposalRequested && IsVisible ? UserInterfaceCanvas : null;
                    IRuntimeRenderInfo2DRegistrationTarget? registered = _registeredCanvas;
                    if (ReferenceEquals(registered, desired))
                        return;
                    if (registered is not null)
                    {
                        registered.RemoveRenderable2D(this);
                        _registeredCanvas = null;
                        continue;
                    }
                    if (desired is not null)
                    {
                        // Preserve an attempted add for cleanup if the target
                        // publishes the item and then reports a failure.
                        _registeredCanvas = desired;
                        desired.AddRenderable2D(this);
                    }
                }
                throw new InvalidOperationException("Render info canvas registration did not settle.");
            }
            finally
            {
                _reconcilingCanvasRegistration = false;
            }
        }

        public int CompareTo(RenderInfo2D? other)
        {
            if (other is null)
                return 1;

            float? depth = Owner?.TransformDepth;
            float? otherDepth = other.Owner?.TransformDepth;
            if (depth.HasValue && otherDepth.HasValue)
            {
                if (depth.Value > otherDepth.Value)
                    return 1;
                else if (depth.Value < otherDepth.Value)
                    return -1;
            }
            else if (depth.HasValue)
                return 1;
            else if (otherDepth.HasValue)
                return -1;

            if (LayerIndex > other.LayerIndex)
                return 1;
            else if (LayerIndex < other.LayerIndex)
                return -1;

            if (IndexWithinLayer > other.IndexWithinLayer)
                return 1;
            else if (IndexWithinLayer < other.IndexWithinLayer)
                return -1;

            return 0;
        }

        protected override void RenderCullingVolume()
        {
            var cullingVolume = CullingVolume;
            if (cullingVolume is null)
                return;

            RuntimeRenderingHostServices.DebugDrawing.RenderDebugRect2D(cullingVolume.Value, false, ColorF4.White);
        }
    }
}
