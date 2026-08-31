namespace XREngine.Rendering.Occlusion
{
    /// <summary>Reason the opt-in CPU software occlusion path did or did not run this frame.</summary>
    public enum ECpuSoftwareOcclusionProfitabilityDecision
    {
        Cold,
        Unmeasured,
        Unprofitable,
        Probing,
        Profitable,
        Forced,
        DebugBypass,
    }
}
