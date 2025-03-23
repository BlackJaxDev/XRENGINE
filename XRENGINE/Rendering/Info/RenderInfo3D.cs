using System.Numerics;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Trees;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering.Info
{
    public class RenderInfo3D : RenderInfo, IOctreeItem
    {
        public override ITreeNode? TreeNode => OctreeNode;

        public static Func<IRenderable, RenderCommand[], RenderInfo3D>? ConstructorOverride { get; set; } = null;

        private RenderInfo3D(IRenderable owner) : base(owner) { }
        private RenderInfo3D() : base(null) { }

        public static RenderInfo3D New(IRenderable owner)
            => ConstructorOverride?.Invoke(owner, []) ?? new RenderInfo3D(owner);
        public static RenderInfo3D New(IRenderable owner, params RenderCommand[] renderCommands)
            => ConstructorOverride?.Invoke(owner, renderCommands) ?? new RenderInfo3D(owner, renderCommands);

        public static RenderInfo3D New(IRenderable owner, int renderPass, RenderCommandMethod3D.DelRender renderMethod)
            => New(owner, new RenderCommandMethod3D(renderPass, renderMethod));
        public static RenderInfo3D New(IRenderable owner, EDefaultRenderPass renderPass, RenderCommandMethod3D.DelRender renderMethod)
            => New(owner, new RenderCommandMethod3D((int)renderPass, renderMethod));

        public static RenderInfo3D New(IRenderable owner, params (int renderPass, RenderCommandMethod3D.DelRender renderMethod)[] methods)
            => New(owner, methods.Select((x, y) => new RenderCommandMethod3D(x.renderPass, x.renderMethod)).ToArray());
        public static RenderInfo3D New(IRenderable owner, params (EDefaultRenderPass renderPass, RenderCommandMethod3D.DelRender renderMethod)[] methods)
            => New(owner, methods.Select((x, y) => new RenderCommandMethod3D((int)x.renderPass, x.renderMethod)).ToArray());

        public static RenderInfo3D New(IRenderable owner, int renderPass, XRMeshRenderer manager, Matrix4x4 worldMatrix, XRMaterial? materialOverride)
            => New(owner, new RenderCommandMesh3D(renderPass, manager, worldMatrix, materialOverride));
        public static RenderInfo3D New(IRenderable owner, EDefaultRenderPass renderPass, XRMeshRenderer manager, Matrix4x4 worldMatrix, XRMaterial? materialOverride)
            => New(owner, new RenderCommandMesh3D((int)renderPass, manager, worldMatrix, materialOverride));

        public static RenderInfo3D New(IRenderable owner, params (int renderPass, XRMeshRenderer manager, Matrix4x4 worldMatrix, XRMaterial? materialOverride)[] meshes)
            => New(owner, meshes.Select((x, y) => new RenderCommandMesh3D(x.renderPass, x.manager, x.worldMatrix, x.materialOverride)).ToArray());
        public static RenderInfo3D New(IRenderable owner, params (EDefaultRenderPass renderPass, XRMeshRenderer manager, Matrix4x4 worldMatrix, XRMaterial? materialOverride)[] meshes)
            => New(owner, meshes.Select((x, y) => new RenderCommandMesh3D((int)x.renderPass, x.manager, x.worldMatrix, x.materialOverride)).ToArray());

        protected RenderInfo3D(IRenderable owner, params RenderCommand[] renderCommands)
            : base(owner, renderCommands) { }

        private AABB? _localCullingVolume;
        private Matrix4x4 _cullingMatrix = Matrix4x4.Identity;
        private OctreeNodeBase? _octreeNode;
        private bool _receivesShadows = true;
        private bool _castsShadows = true;
        private bool _visibleInLightingProbes = true;
        private bool _hiddenFromOwner = false;
        private bool _visibleToOwnerOnly = false;

        public Matrix4x4 CullingOffsetMatrix
        {
            get => _cullingMatrix;
            set
            {
                //if (!XRMath.MatrixEquals(_cullingMatrix, value))
                    SetField(ref _cullingMatrix, value);
            }
        }

        public bool HiddenFromOwner
        {
            get => _hiddenFromOwner;
            set => SetField(ref _hiddenFromOwner, value);
        }

        public bool VisibleToOwnerOnly
        {
            get => _visibleToOwnerOnly;
            set => SetField(ref _visibleToOwnerOnly, value);
        }

        /// <summary>
        /// The shape the rendering octree will use to determine occlusion and offscreen culling (visibility).
        /// If null, the object will always be rendered.
        /// </summary>
        public AABB? LocalCullingVolume
        {
            get => _localCullingVolume;
            set
            {
                if (!XRMath.VolumeEquals(_localCullingVolume, value))
                    SetField(ref _localCullingVolume, value);
            }
        }

        /// <summary>
        /// The octree bounding box this object is currently located in.
        /// </summary>   
        public OctreeNodeBase? OctreeNode
        {
            get => _octreeNode;
            set => SetField(ref _octreeNode, value);
        }

        /// <summary>
        /// Used to render objects in the same pass in a certain order.
        /// Smaller value means rendered sooner, zero (exactly) means it doesn't matter.
        /// </summary>
        //[Browsable(false)]
        //public float RenderOrder => RenderOrderFunc is null ? 0.0f : RenderOrderFunc();

        public bool ReceivesShadows
        {
            get => _receivesShadows;
            set => SetField(ref _receivesShadows, value);
        }

        public bool CastsShadows
        {    
            get => _castsShadows;
            set => SetField(ref _castsShadows, value);
        }

        public bool VisibleInLightingProbes
        {
            get => _visibleInLightingProbes;
            set => SetField(ref _visibleInLightingProbes, value);
        }

        public virtual bool AllowRender(
            IVolume? cullingVolume,
            RenderCommandCollection passes,
            XRCamera? camera,
            bool containsOnly) => (!passes.IsShadowPass || CastsShadows) && Intersects(cullingVolume, containsOnly);

        public bool Intersects(IVolume? cullingVolume, bool containsOnly)
        {
            var worldCullingVolume = ((IOctreeItem)this).WorldCullingVolume;
            if (worldCullingVolume is null)
                return true;

            var containment = cullingVolume?.ContainsBox(worldCullingVolume.Value) ?? EContainment.Contains;
            return containsOnly ? containment == EContainment.Contains : containment != EContainment.Disjoint;
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(WorldInstance):
                    if (IsVisible)
                    {
                        if (prev is XRWorldInstance prevInstance)
                            prevInstance.VisualScene?.RemoveRenderable(this);
                        if (field is XRWorldInstance newInstance)
                            newInstance.VisualScene?.AddRenderable(this);
                    }
                    break;
                case (nameof(IsVisible)):
                    if (IsVisible)
                        WorldInstance?.VisualScene?.AddRenderable(this);
                    else
                        WorldInstance?.VisualScene?.RemoveRenderable(this);
                    break;
                case nameof(CullingOffsetMatrix):
                case nameof(LocalCullingVolume):
                    //using (var t = Engine.Profiler.Start("QueueItemMoved"))
                    {
                        OctreeNode?.QueueItemMoved(this);
                    }
                    break;
            }
        }
    }
}