using System;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Compact compute-planner result. Diagnostic text is materialized only when
/// the cold failure path emits it.
/// </summary>
internal readonly record struct VulkanComputePreparationResult(
    EVulkanComputePreparationOutcome Outcome,
    int OperationIndex,
    int OperationCount,
    string? ProgramName,
    Exception? Exception = null)
{
    public bool Succeeded => Outcome == EVulkanComputePreparationOutcome.Success;
    public bool Pending => Outcome == EVulkanComputePreparationOutcome.PipelinePending;

    public static VulkanComputePreparationResult Success { get; } =
        new(EVulkanComputePreparationOutcome.Success, -1, 0, null);

    public string FormatFailure()
    {
        string programName = ProgramName ?? "UnnamedProgram";
        return Outcome switch
        {
            EVulkanComputePreparationOutcome.PipelinePending =>
                $"Compute pipeline '{programName}' is pending asynchronous preparation before recording" +
                $"{(string.IsNullOrWhiteSpace(Exception?.Message) ? "." : $": {Exception.Message}")}",
            EVulkanComputePreparationOutcome.ProgramLinkFailed =>
                $"Compute program '{programName}' is not linkable before recording.",
            EVulkanComputePreparationOutcome.PipelineUnavailable =>
                $"Compute pipeline '{programName}' is unavailable before recording.",
            EVulkanComputePreparationOutcome.PipelineCreationFailed =>
                $"Compute pipeline '{programName}' preparation failed: " +
                $"{Exception?.GetType().Name ?? "UnknownException"}: {Exception?.Message ?? "no detail"}",
            EVulkanComputePreparationOutcome.DescriptorPreparationFailed =>
                $"Compute descriptor resources for '{programName}' could not be prepared before recording " +
                $"(op {OperationIndex}/{OperationCount}).",
            _ => "Compute preparation failed without a typed failure outcome."
        };
    }
}
