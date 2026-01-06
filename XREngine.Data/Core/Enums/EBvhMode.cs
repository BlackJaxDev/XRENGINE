namespace XREngine
{
    /// <summary>
    /// Selects how the BVH is constructed on the GPU.
    /// </summary>
    public enum EBvhMode
    {
        /// <summary>
        /// Use only Morton code ordering when building the BVH.
        /// </summary>
        Morton,

        /// <summary>
        /// Apply surface-area heuristics (SAH) refinement after Morton ordering.
        /// </summary>
        Sah
    }
}
