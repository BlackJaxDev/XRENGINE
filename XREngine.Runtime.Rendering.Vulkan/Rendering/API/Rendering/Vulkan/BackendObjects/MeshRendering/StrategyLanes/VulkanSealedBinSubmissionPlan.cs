using XREngine.Data.Rendering;
using XREngine.Rendering.Diagnostics;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable canonical submission package handed to recording workers. Strategy,
/// capacity, diagnostics, exceptions, and output policy are all resolved here.
/// </summary>
internal sealed class VulkanSealedBinSubmissionPlan
{
    private VulkanSealedBinExceptionSnapshot? _orderedExceptions;

    internal void Reset(
        in VulkanRenderBinKey binKey,
        VulkanBinResourceManifest resourceManifest,
        EMeshSubmissionStrategy requestedStrategy,
        EMeshSubmissionStrategy executionRequestedStrategy,
        EMeshSubmissionStrategy resolvedStrategy,
        VulkanSubmissionPlanDowngradeReason downgradeReason,
        in VulkanSubmissionCapacity capacity,
        in VulkanSubmissionOutputPolicy outputPolicy,
        GpuDiagnosticReadbackPlanNode? diagnosticPlan,
        bool cpuSafetyNet,
        VulkanSealedBinExceptionSnapshot orderedExceptions)
    {
        BinKey = binKey;
        ResourceManifest = resourceManifest;
        RequestedStrategy = requestedStrategy;
        ExecutionRequestedStrategy = executionRequestedStrategy;
        ResolvedStrategy = resolvedStrategy;
        DowngradeReason = downgradeReason;
        Capacity = capacity;
        OutputPolicy = outputPolicy;
        DiagnosticPlan = diagnosticPlan;
        OverflowDiagnosticPlan = null;
        CpuSafetyNet = cpuSafetyNet;
        _orderedExceptions = orderedExceptions;
    }

    internal VulkanRenderBinKey BinKey { get; private set; }
    internal VulkanBinResourceManifest ResourceManifest { get; private set; } = null!;
    internal EMeshSubmissionStrategy RequestedStrategy { get; private set; }
    /// <summary>
    /// Exact range-local strategy requested after classifying its geometry
    /// producer. This may be indexed even when the family request is meshlet.
    /// </summary>
    internal EMeshSubmissionStrategy ExecutionRequestedStrategy { get; private set; }
    internal EMeshSubmissionStrategy ResolvedStrategy { get; private set; }
    internal VulkanSubmissionPlanDowngradeReason DowngradeReason { get; private set; }
    internal VulkanSubmissionCapacity Capacity { get; private set; }
    internal VulkanSubmissionOutputPolicy OutputPolicy { get; private set; }
    internal GpuDiagnosticReadbackPlanNode? DiagnosticPlan { get; private set; }
    /// <summary>
    /// Optional global visibility-counter snapshot attached only to the final
    /// instrumented bin, after every range has produced its output.
    /// </summary>
    internal GpuDiagnosticReadbackPlanNode? OverflowDiagnosticPlan { get; private set; }
    internal bool CpuSafetyNet { get; private set; }
    internal ReadOnlySpan<VulkanBinOrderedException> OrderedExceptions
        => _orderedExceptions is null
            ? ReadOnlySpan<VulkanBinOrderedException>.Empty
            : _orderedExceptions.Entries;

    internal void CopyFrom(
        VulkanSealedBinSubmissionPlan source,
        VulkanSealedBinExceptionSnapshot orderedExceptions)
    {
        ArgumentNullException.ThrowIfNull(source);
        Reset(
            source.BinKey,
            source.ResourceManifest,
            source.RequestedStrategy,
            source.ExecutionRequestedStrategy,
            source.ResolvedStrategy,
            source.DowngradeReason,
            source.Capacity,
            source.OutputPolicy,
            source.DiagnosticPlan,
            source.CpuSafetyNet,
            orderedExceptions);
    }

    internal bool IsInstrumented => ResolvedStrategy is
        EMeshSubmissionStrategy.GpuIndirectInstrumented or
        EMeshSubmissionStrategy.GpuMeshletInstrumented;

    internal void AttachOverflowDiagnosticPlan(
        GpuDiagnosticReadbackPlanNode? diagnosticPlan)
    {
        if (diagnosticPlan is { } node &&
            (!IsInstrumented || node.Strategy != ResolvedStrategy ||
             node.Decoder != EGpuDiagnosticReadbackDecoder.SubmissionValidation ||
             node.ByteCount != 64u))
        {
            throw new InvalidOperationException(
                "Visibility overflow diagnostics require the sealed instrumented strategy and exact 16-word counter ABI.");
        }

        OverflowDiagnosticPlan = diagnosticPlan;
    }

    /// <summary>
    /// Clamps an unexpected producer overflow and signals its asynchronous
    /// sidecar. It does not retry, remap buffers, or alter this sealed lane.
    /// </summary>
    internal uint ClampProducedOutputCount(
        uint producedCount,
        VulkanSubmissionOverflowReporter? reportOverflow)
    {
        if (producedCount <= Capacity.OutputCapacity)
            return producedCount;
        reportOverflow?.Invoke(
            new(BinKey, ResolvedStrategy, producedCount, Capacity.OutputCapacity));
        return Capacity.OutputCapacity;
    }
}

/// <summary>
/// One preallocated immutable-for-seal exception image shared by every bin plan
/// in a prepared stream.
/// </summary>
internal sealed class VulkanSealedBinExceptionSnapshot
{
    private readonly VulkanBinOrderedException[] _entries;
    private int _count;

    internal VulkanSealedBinExceptionSnapshot(int capacity)
        => _entries = new VulkanBinOrderedException[capacity];

    internal ReadOnlySpan<VulkanBinOrderedException> Entries
        => _entries.AsSpan(0, _count);

    internal bool TryReset(ReadOnlySpan<VulkanBinOrderedException> source)
    {
        if (source.Length > _entries.Length)
            return false;
        source.CopyTo(_entries);
        _count = source.Length;
        return true;
    }
}

/// <summary>Asynchronous-only observation of an unexpected output overflow.</summary>
internal delegate void VulkanSubmissionOverflowReporter(
    in VulkanSubmissionOverflowReport report);

/// <summary>Fixed overflow evidence emitted without a same-frame retry.</summary>
internal readonly record struct VulkanSubmissionOverflowReport(
    VulkanRenderBinKey BinKey,
    EMeshSubmissionStrategy Strategy,
    uint ProducedCount,
    uint ClampedCount);
