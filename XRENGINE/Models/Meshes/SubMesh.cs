using MemoryPack;
using XREngine.Core.Files;
using XREngine.Data.Geometry;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering.Models
{
    /// <summary>
    /// Represents various levels of detail for a mesh that can be rendered.
    /// </summary>
    [MemoryPackable(GenerateType.NoGenerate)]
    public partial class SubMesh : XRAsset
    {
        public SortedSet<SubMeshLOD> LODs { get; } = new(new LODSorter());

        private AABB _bounds;
        private AABB? _cullingVolumeOverride;
        private TransformBase? _rootBone;

        /// <summary>
        /// The true bind-pose bounding box of this mesh.
        /// </summary>
        public AABB Bounds
        {
            get => _bounds;
            set => SetField(ref _bounds, value);
        }

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
            AABB bounds = new();
            foreach (SubMeshLOD lod in LODs)
                if (lod.Mesh != null)
                    bounds = AABB.Union(bounds, lod.Mesh.Bounds);
            return bounds;
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
