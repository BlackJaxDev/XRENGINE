using MemoryPack;
using System.ComponentModel;
using System.Numerics;
using XREngine.Core.Files;
using XREngine.Data.Geometry;
using XREngine.Rendering.Meshlets;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering.Models
{
    /// <summary>
    /// Represents various levels of detail for a mesh that can be rendered.
    /// </summary>
    [XRAssetInspector("XREngine.Editor.AssetEditors.SubMeshInspector")]
    [MemoryPackable(GenerateType.NoGenerate)]
    public partial class SubMesh : XRAsset
    {
        private SortedSet<SubMeshLOD> _lods = new(new LODSorter());
        private MeshOptimizerSubMeshSettings _meshOptimizer = new();

        [MemoryPackIgnore]
        public SortedSet<SubMeshLOD> LODs
        {
            get => _lods;
            set => ReplaceLODs(value);
        }

        /// <summary>
        /// Cooked-binary serialization bridge for the runtime sorted set. The cooked collection
        /// modules intentionally support list payloads, while YAML continues to expose <see cref="LODs"/>.
        /// </summary>
        private List<SubMeshLOD> CookedBinaryLODs
        {
            get => [.. _lods];
            set => ReplaceLODs(value);
        }

        private void ReplaceLODs(IEnumerable<SubMeshLOD>? lods)
        {
            SortedSet<SubMeshLOD> orderedLODs = new(new LODSorter());
            if (lods is not null)
                foreach (SubMeshLOD lod in lods)
                    orderedLODs.Add(lod);

            SetField(ref _lods, orderedLODs, nameof(LODs));
        }

        public MeshOptimizerSubMeshSettings MeshOptimizer
        {
            get => _meshOptimizer;
            set => SetField(ref _meshOptimizer, value ?? new MeshOptimizerSubMeshSettings());
        }

        private AABB _bounds;
        private AABB? _cullingVolumeOverride;
        private TransformBase? _rootBone;
        private bool _useGpuMeshBvh = true;
        private bool _realtimeGpuMeshBvhForSkinnedMeshes = true;

        /// <summary>
        /// The true bind-pose bounding box of this mesh.
        /// </summary>
        public AABB Bounds
        {
            get => _bounds;
            set => SetField(ref _bounds, value);
        }

        [YamlTransformReference]
        public TransformBase? RootBone
        {
            get => _rootBone;
            set => SetField(ref _rootBone, value);
        }

        /// <summary>
        /// The user-set culing bounds for this mesh.
        /// </summary>
        public AABB? CullingBounds
        {
            get => _cullingVolumeOverride;
            set => SetField(ref _cullingVolumeOverride, value);
        }

        [Category("BVH")]
        [DisplayName("Use GPU Mesh BVH")]
        [Description("Builds a GPU triangle BVH for this submesh when editor preview or interaction requests it.")]
        public bool UseGpuMeshBvh
        {
            get => _useGpuMeshBvh;
            set => SetField(ref _useGpuMeshBvh, value);
        }

        [Category("BVH")]
        [DisplayName("Realtime Skinned GPU BVH")]
        [Description("When enabled, skinned instances refit their GPU triangle BVH from compute-skinned vertex positions when requested.")]
        public bool RealtimeGpuMeshBvhForSkinnedMeshes
        {
            get => _realtimeGpuMeshBvhForSkinnedMeshes;
            set => SetField(ref _realtimeGpuMeshBvhForSkinnedMeshes, value);
        }

        [YamlTransformReference]
        public TransformBase? RootTransform { get; set; }

        public void DetermineRootBone()
        {
            RootBone = TransformBase.FindCommonAncestor(
                [.. LODs.SelectMany(x => x.Mesh?.UtilizedBones ?? [])
                    .Select(x => x.tfm)
                    .Distinct()]);
        }

        public SubMesh() { }

        public SubMesh(XRMesh? primitives, XRMaterial? material)
            : this(new SubMeshLOD(material, primitives, 0.0f)) { }

        public SubMesh(params SubMeshLOD[] lods)
            : this((IEnumerable<SubMeshLOD>)lods) { }

        public SubMesh(IEnumerable<SubMeshLOD> lods)
        {
            foreach (SubMeshLOD lod in lods)
                LODs.Add(lod);
            Bounds = CalculateBoundingBox();
            DetermineRootBone();
        }

        /// <summary>
        /// Calculates the fully-encompassing aabb for this model based on each child mesh's aabb.
        /// </summary>
        public AABB CalculateBoundingBox()
        {
            AABB? bounds = null;
            foreach (SubMeshLOD lod in LODs)
                if (lod.Mesh != null)
                    bounds = bounds?.ExpandedToInclude(lod.Mesh.Bounds) ?? lod.Mesh.Bounds;
            return bounds ?? new AABB(Vector3.Zero, Vector3.Zero);
        }
    }

    public class LODSorter : IComparer<SubMeshLOD>
    {
        public int Compare(SubMeshLOD? x, SubMeshLOD? y)
        {
            if (x is null && y is null)
                return 0;
            if (x is null)
                return -1;
            if (y is null)
                return 1;

            return x.MaxVisibleDistance.CompareTo(y.MaxVisibleDistance);
        }
    }
}
