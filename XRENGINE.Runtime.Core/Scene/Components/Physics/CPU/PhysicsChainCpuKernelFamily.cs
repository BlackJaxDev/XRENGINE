namespace XREngine.Components;

/// <summary>Observable CPU solver family selected for the latest step.</summary>
public enum PhysicsChainCpuKernelFamily : byte
{
    ScalarLinear,
    Avx2LinearBatch,
    DepthOrderedBranched,
}
