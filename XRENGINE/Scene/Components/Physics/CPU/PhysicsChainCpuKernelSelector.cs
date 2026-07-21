using System.Runtime.Intrinsics.X86;

namespace XREngine.Components;

/// <summary>Capability and topology based CPU-kernel selection.</summary>
public static class PhysicsChainCpuKernelSelector
{
    public const int Avx2BatchWidth = 8;

    public static PhysicsChainCpuKernelFamily Select(
        PhysicsChainTemplate template,
        int compatibleInstanceCount,
        int colliderCount)
    {
        ArgumentNullException.ThrowIfNull(template);
        if ((template.FeatureMask & PhysicsChainTemplateFeatureMask.BranchedTopology) != 0)
            return PhysicsChainCpuKernelFamily.DepthOrderedBranched;
        if (colliderCount == 0 && compatibleInstanceCount >= Avx2BatchWidth && Avx2.IsSupported)
            return PhysicsChainCpuKernelFamily.Avx2LinearBatch;
        return PhysicsChainCpuKernelFamily.ScalarLinear;
    }
}
