using XREngine.Data.Rendering;

namespace XREngine.Rendering.Diagnostics;

/// <summary>
/// Immutable, renderer-neutral sidecar plan for delayed GPU diagnostics.
/// Production zero-readback strategies are rejected at plan construction, so
/// workers cannot accidentally attach a readback after strategy sealing.
/// </summary>
public readonly record struct GpuDiagnosticReadbackPlan
{
    /// <summary>A plan with no reservations, copies, or decode work.</summary>
    public static GpuDiagnosticReadbackPlan Disabled { get; } = default;

    private GpuDiagnosticReadbackPlan(
        ulong frameIdentity,
        EMeshSubmissionStrategy strategy,
        GpuDiagnosticReadbackPlanNode node)
    {
        FrameIdentity = frameIdentity;
        Strategy = strategy;
        Node = node;
    }

    public ulong FrameIdentity { get; }
    public EMeshSubmissionStrategy Strategy { get; }
    public GpuDiagnosticReadbackPlanNode Node { get; }
    public bool IsEnabled => Node.ByteCount != 0u;

    /// <summary>
    /// Creates a sealed single-node attachment without allocating. Prepared
    /// packages retain nodes in their existing reusable frame storage.
    /// </summary>
    public static GpuDiagnosticReadbackPlan Create(
        ulong frameIdentity,
        EMeshSubmissionStrategy strategy,
        in GpuDiagnosticReadbackPlanNode node)
    {
        if (node.ByteCount == 0u)
            return Disabled;
        if (!IsInstrumented(strategy))
        {
            throw new InvalidOperationException(
                $"The {strategy} strategy requires zero diagnostic readback attachments.");
        }

        if (node.Strategy != strategy)
        {
            throw new InvalidOperationException(
                "A diagnostic readback node must use the prepared frame's sealed submission strategy.");
        }

        node.Validate();
        return new GpuDiagnosticReadbackPlan(frameIdentity, strategy, node);
    }

    public static bool IsInstrumented(EMeshSubmissionStrategy strategy)
        => strategy is EMeshSubmissionStrategy.GpuIndirectInstrumented or
            EMeshSubmissionStrategy.GpuMeshletInstrumented;
}
