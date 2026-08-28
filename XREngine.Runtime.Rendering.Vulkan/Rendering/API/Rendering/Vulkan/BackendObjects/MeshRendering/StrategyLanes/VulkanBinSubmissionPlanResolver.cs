using XREngine.Data.Rendering;
using XREngine.Rendering.Diagnostics;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Resolves the complete five-lane contract before plan sealing. It consumes
/// only captured values and intentionally has no access to live backend state.
/// </summary>
internal static class VulkanBinSubmissionPlanResolver
{
    internal static bool TrySeal(
        in VulkanRenderBinKey binKey,
        VulkanBinResourceManifest resourceManifest,
        EMeshSubmissionStrategy familyRequestedStrategy,
        EMeshSubmissionStrategy executionRequestedStrategy,
        in VulkanSubmissionLaneCapabilities capabilities,
        in VulkanSubmissionOutputPolicy outputPolicy,
        uint sourceCount,
        uint sourceCapacity,
        uint maxOutputPerSource,
        uint outputCapacity,
        GpuDiagnosticReadbackPlanNode? diagnosticPlan,
        bool requestCpuSafetyNet,
        VulkanSealedBinExceptionSnapshot orderedExceptions,
        VulkanSealedBinSubmissionPlan destination,
        out VulkanSealedBinSubmissionPlan? plan,
        out VulkanSubmissionPlanRejectionReason rejection)
    {
        plan = null;
        rejection = ValidateInputs(
            in binKey,
            resourceManifest,
            in outputPolicy,
            in capabilities,
            diagnosticPlan,
            requestCpuSafetyNet);
        if (rejection != VulkanSubmissionPlanRejectionReason.None)
            return false;
        if (!VulkanSubmissionCapacity.TryCreate(
                sourceCount,
                sourceCapacity,
                maxOutputPerSource,
                outputCapacity,
                out VulkanSubmissionCapacity capacity,
                out rejection))
        {
            return false;
        }

        if (!TryResolveStrategy(
            executionRequestedStrategy,
            in capabilities,
            in outputPolicy,
            sourceCount,
            out EMeshSubmissionStrategy resolved,
            out VulkanSubmissionPlanDowngradeReason downgrade,
            out rejection))
        {
            return false;
        }
        if (downgrade == VulkanSubmissionPlanDowngradeReason.None &&
            familyRequestedStrategy != executionRequestedStrategy)
        {
            downgrade = VulkanSubmissionPlanDowngradeReason.RangeProducerRequiresIndexed;
        }

        bool instrumented = IsInstrumented(resolved);
        if (instrumented && !diagnosticPlan.HasValue)
        {
            rejection = VulkanSubmissionPlanRejectionReason.DiagnosticsNotAllowed;
            return false;
        }
        if (diagnosticPlan is { } resolvedNode &&
            resolvedNode.Strategy != resolved)
        {
            rejection = VulkanSubmissionPlanRejectionReason.DiagnosticsNotAllowed;
            return false;
        }
        if (!instrumented && (diagnosticPlan.HasValue || requestCpuSafetyNet))
        {
            rejection = diagnosticPlan.HasValue
                ? VulkanSubmissionPlanRejectionReason.DiagnosticAttachedToZeroReadback
                : VulkanSubmissionPlanRejectionReason.CpuSafetyNetAttachedToZeroReadback;
            return false;
        }

        GpuDiagnosticReadbackPlanNode? resolvedDiagnostic = instrumented
            ? diagnosticPlan
            : null;
        bool resolvedSafetyNet = instrumented && requestCpuSafetyNet;
        destination.Reset(
            binKey,
            resourceManifest,
            familyRequestedStrategy,
            executionRequestedStrategy,
            resolved,
            downgrade,
            capacity,
            outputPolicy,
            resolvedDiagnostic,
            resolvedSafetyNet,
            orderedExceptions);
        plan = destination;
        return true;
    }

    private static VulkanSubmissionPlanRejectionReason ValidateInputs(
        in VulkanRenderBinKey binKey,
        VulkanBinResourceManifest? resourceManifest,
        in VulkanSubmissionOutputPolicy outputPolicy,
        in VulkanSubmissionLaneCapabilities capabilities,
        GpuDiagnosticReadbackPlanNode? diagnosticPlan,
        bool requestCpuSafetyNet)
    {
        if (!binKey.IsValid)
            return VulkanSubmissionPlanRejectionReason.InvalidBinKey;
        if (resourceManifest is null || resourceManifest.Count == 0)
            return VulkanSubmissionPlanRejectionReason.EmptyResourceManifest;
        if (!outputPolicy.IsValid)
            return VulkanSubmissionPlanRejectionReason.InvalidOutputPolicy;
        if (diagnosticPlan is { } diagnostic)
        {
            if (!capabilities.AllowsInstrumentedDiagnostics ||
                !IsInstrumented(diagnostic.Strategy) ||
                diagnostic.ByteCount == 0u ||
                string.IsNullOrWhiteSpace(diagnostic.DecoderKey))
            {
                return VulkanSubmissionPlanRejectionReason.DiagnosticsNotAllowed;
            }
        }
        if (requestCpuSafetyNet && !capabilities.AllowsInstrumentedCpuSafetyNet)
            return VulkanSubmissionPlanRejectionReason.CpuSafetyNetNotAllowed;
        return VulkanSubmissionPlanRejectionReason.None;
    }

    private static bool TryResolveStrategy(
        EMeshSubmissionStrategy requested,
        in VulkanSubmissionLaneCapabilities capabilities,
        in VulkanSubmissionOutputPolicy outputPolicy,
        uint sourceCount,
        out EMeshSubmissionStrategy resolved,
        out VulkanSubmissionPlanDowngradeReason downgrade,
        out VulkanSubmissionPlanRejectionReason rejection)
    {
        resolved = requested;
        downgrade = VulkanSubmissionPlanDowngradeReason.None;
        rejection = VulkanSubmissionPlanRejectionReason.None;
        if (requested == EMeshSubmissionStrategy.CpuDirect)
            return true;
        if (!outputPolicy.AllowsGpuDrivenSubmission)
        {
            rejection = VulkanSubmissionPlanRejectionReason.GpuDrivenOutputPolicyRejected;
            return false;
        }

        if (requested is EMeshSubmissionStrategy.GpuMeshletZeroReadback or
            EMeshSubmissionStrategy.GpuMeshletInstrumented)
        {
            if (!capabilities.SupportsMeshletIndirectCount)
            {
                rejection = VulkanSubmissionPlanRejectionReason.GpuLaneUnavailable;
                return false;
            }
            if (sourceCount < capabilities.MeshletCrossoverSourceCount)
            {
                rejection = VulkanSubmissionPlanRejectionReason.GpuLaneBelowCrossover;
                return false;
            }
            return true;
        }

        if (!capabilities.SupportsIndirectCount)
        {
            rejection = VulkanSubmissionPlanRejectionReason.GpuLaneUnavailable;
            return false;
        }
        if (sourceCount < capabilities.IndirectCrossoverSourceCount)
        {
            rejection = VulkanSubmissionPlanRejectionReason.GpuLaneBelowCrossover;
            return false;
        }
        return true;
    }

    private static bool IsInstrumented(EMeshSubmissionStrategy strategy)
        => strategy is EMeshSubmissionStrategy.GpuIndirectInstrumented or
        EMeshSubmissionStrategy.GpuMeshletInstrumented;
}
