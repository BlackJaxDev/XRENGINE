using XREngine.Rendering.Residency;

namespace XREngine.Rendering.Models
{
    /// <summary>
    /// Maps the LODs of a sub-mesh to the residency tiers. The LODs sort from the finest (index 0) to the coarsest (the last
    /// index). The coarsest LOD is T0, the proxy. LOD 0 is T2, the target. Each LOD between them is T1.
    /// A sub-mesh with one LOD has only LOD 0: it is the full mesh, so its tier is T2.
    /// </summary>
    public static class SubMeshLodTiers
    {
        /// <summary>Gets the tier of a LOD.</summary>
        /// <param name="lodIndex">The index of the LOD in sort order.</param>
        /// <param name="lodCount">The number of LODs.</param>
        public static EResidencyLevel GetTier(int lodIndex, int lodCount)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lodCount);
            ArgumentOutOfRangeException.ThrowIfNegative(lodIndex);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(lodIndex, lodCount);

            if (lodIndex == 0)
                return EResidencyLevel.Target;
            return lodIndex == lodCount - 1 ? EResidencyLevel.Proxy : EResidencyLevel.Coarse;
        }
    }
}
