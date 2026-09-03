namespace XREngine.Rendering;

/// <summary>
/// Verifies architectural constraints, single-authority budgets, and zero managed hot-path allocations.
/// </summary>
public static class AdvancedArchitectureBudgetVerifier
{
    public const uint MaxGlobalDescriptorSets = 4u; // Sets 0..3
    public const uint MaxActiveFrameSlots = 4u;

    /// <summary>
    /// Validates that steady-state frame recording incurred zero managed heap allocations.
    /// </summary>
    public static bool VerifyZeroSteadyStateAllocations(long allocatedBytesInFrame)
        => allocatedBytesInFrame == 0L;

    /// <summary>
    /// Validates descriptor set indexing bounds according to the resident architecture contract.
    /// </summary>
    public static bool VerifyDescriptorSetLayout(uint setIndex)
        => setIndex < MaxGlobalDescriptorSets;
}
