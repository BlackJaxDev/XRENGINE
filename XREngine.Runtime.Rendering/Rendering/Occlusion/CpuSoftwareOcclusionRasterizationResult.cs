namespace XREngine.Rendering.Occlusion
{
    /// <summary>Bounded rasterization result used to account input work separately from coverage.</summary>
    internal readonly struct CpuSoftwareOcclusionRasterizationResult(int trianglesInspected, bool wroteCoverage)
    {
        public readonly int TrianglesInspected = trianglesInspected;
        public readonly bool WroteCoverage = wroteCoverage;
    }
}
