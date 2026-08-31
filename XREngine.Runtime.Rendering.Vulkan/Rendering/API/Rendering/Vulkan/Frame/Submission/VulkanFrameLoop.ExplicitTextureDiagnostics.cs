namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameLoop
{
    private bool _exclusiveExplicitDiagnostic;

    /// <summary>
    /// Reserves cold diagnostic ownership against frame admission and teardown.
    /// The authentic completed receipt is checked after admission is excluded.
    /// </summary>
    internal bool TryEnterExplicitTextureDiagnostic(
        in VulkanExplicitProductionSubmissionReceipt receipt)
    {
        lock (_retirementGate)
        {
            if (IsQuiescing || _activeFrameExecutions != 0 || _exclusiveExplicitDiagnostic)
                return false;
            _exclusiveExplicitDiagnostic = true;
            _activeFrameExecutions++;
        }
        bool admitted = false;
        try
        {
            admitted = IsCurrentExplicitProductionReceipt(in receipt) &&
                TryGetExplicitProductionSubmissionCompletion(in receipt, out bool completed) && completed;
            return admitted;
        }
        finally
        {
            if (!admitted)
                ExitExplicitTextureDiagnostic();
        }
    }

    internal void ExitExplicitTextureDiagnostic()
    {
        lock (_retirementGate)
        {
            if (!_exclusiveExplicitDiagnostic)
                throw new InvalidOperationException("Explicit texture diagnostic ownership is unbalanced.");
            _exclusiveExplicitDiagnostic = false;
            ExitFrameExecutionNoLock();
        }
    }
}
