namespace XREngine.Rendering.Occlusion
{
    /// <summary>One frame's CPU software occlusion profitability admission result.</summary>
    internal readonly struct CpuSoftwareOcclusionProfitabilityAdmission(
        ECpuSoftwareOcclusionProfitabilityDecision decision,
        bool runSoc)
    {
        public readonly ECpuSoftwareOcclusionProfitabilityDecision Decision = decision;
        public readonly bool RunSoc = runSoc;
    }
}
