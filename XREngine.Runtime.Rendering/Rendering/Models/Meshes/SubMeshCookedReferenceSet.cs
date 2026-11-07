using XREngine.Rendering.Meshlets;

namespace XREngine.Rendering.Models
{
    /// <summary>
    /// The object values of a cooked <see cref="SubMesh"/> payload of codec version 2 that keep the generic cooked encoding
    /// of the older payload: the mesh optimizer settings and the LOD materials. One generic payload holds all of them, so
    /// that a material that more than one LOD uses stays one object after the load. Each LOD descriptor refers to its
    /// material by its index in <see cref="Materials"/>.
    /// </summary>
    internal sealed class SubMeshCookedReferenceSet
    {
        /// <summary>The mesh optimizer settings of the sub-mesh.</summary>
        public MeshOptimizerSubMeshSettings? MeshOptimizer { get; set; }

        /// <summary>The distinct LOD materials, in the order of their first LOD.</summary>
        public List<XRMaterial> Materials { get; set; } = [];
    }
}
