using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Vulkan;

namespace XREngine.RenderBench;

/// <summary>Runs one bounded, presentationless native-buffer growth probe without frame readbacks.</summary>
internal static class RenderBenchScenarioBufferLane
{
    private const uint CMinusOneCommandCount = 7;
    private const uint CCommandCount = 8;
    private const uint CPlusOneCommandCount = 9;

    /// <summary>
    /// Drives the C-1/C/C+1 capacity boundary, then probes an additional recorded frame.
    /// The post-probe slot drain lets the Vulkan ledger observe deferred old-generation reclamation.
    /// </summary>
    internal static RenderBenchScenarioResult Run(
        RenderBenchOptions options,
        RenderBenchProductionScene scene,
        RenderBenchScenarioResult identity)
    {
        List<string> failures = [];
        RenderBenchNativeBufferStressEvidence evidence = new();
        try
        {
            VulkanExplicitProductionSubmissionReceipt cMinusOneReceipt = SubmitAndWait(scene, options.FixedStepSeconds);
            CapacityObservation cMinusOne = ObserveCapacity(scene, CMinusOneCommandCount, "C-1", failures);
            evidence = evidence with { CMinusOneReceipt = cMinusOneReceipt, CMinusOne = cMinusOne.ToDto() };

            scene.AddCandidate(7, new Vector3(-4.5f, 1.0f, 6.0f), Vector3.One);
            VulkanExplicitProductionSubmissionReceipt cReceipt = SubmitAndWait(scene, options.FixedStepSeconds);
            CapacityObservation c = ObserveCapacity(scene, CCommandCount, "C", failures);
            evidence = evidence with { CReceipt = cReceipt, C = c.ToDto() };

            scene.AddCandidate(8, new Vector3(4.5f, 3.0f, 6.0f), Vector3.One);
            VulkanExplicitProductionSubmissionReceipt cPlusOneReceipt = SubmitAndWait(scene, options.FixedStepSeconds);
            CapacityObservation cPlusOne = ObserveCapacity(scene, CPlusOneCommandCount, "C+1", failures);
            evidence = evidence with { CPlusOneReceipt = cPlusOneReceipt, CPlusOne = cPlusOne.ToDto() };

            XRDataBuffer probeSource = ResolveLateDrawIds(scene, in cPlusOneReceipt, failures);
            VulkanNativeBufferDiagnosticDescription probeOriginalBinding = DescribeNativeBuffer(scene, probeSource, "probe source", failures);
            evidence = evidence with { ProbeSource = "OpaqueDeferred.LateDrawIds", ProbeOriginalBinding = probeOriginalBinding };
            uint requestedByteSize = ComputeRequestedByteSize(probeOriginalBinding, failures);

            XRDataBuffer logicalSealProbeSource = ResolveLogicalSealProbeSource(scene);
            evidence = evidence with
            {
                LogicalSealProbeSource = "ForwardPlusLocalLights",
            };
            VulkanExplicitProductionBufferStressProbeRequest logicalSealRequest = new(
                logicalSealProbeSource,
                EVulkanExplicitProductionBufferStressCheckpoint.AfterLogicalSeal,
                0,
                "ForwardPlusLocalLights");
            try
            {
                _ = scene.SubmitStep(options.FixedStepSeconds, logicalSealRequest);
                failures.Add("AfterLogicalSeal: stale logical packet unexpectedly reached a submission result.");
            }
            catch (Exception exception)
            {
                _ = scene.Host.TryGetLastProductionBufferStressProbeEvidence(
                    out VulkanExplicitProductionBufferStressProbeEvidence? logicalSealProbe);
                evidence = evidence with
                {
                    LogicalSealProbe = logicalSealProbe,
                    LogicalSealProbeOriginalBinding = logicalSealProbe?.OldBinding ?? default,
                };
                if (logicalSealProbe is not
                    {
                        Checkpoint: EVulkanExplicitProductionBufferStressCheckpoint.AfterLogicalSeal,
                        OldBindingFrozenByLogicalPlan: true,
                        GrowthAttempted: true,
                        GrowthObserved: true,
                        LogicalPacketRejectedBeforeAcquire: true,
                        AcquisitionAvoided: true,
                        RetryRequired: true,
                    })
                {
                    failures.Add($"AfterLogicalSeal: expected pre-acquire stale-packet rejection was not evidenced: {exception.Message}");
                }
            }

            VulkanExplicitProductionSubmissionReceipt logicalSealRetryReceipt =
                SubmitAndWait(scene, options.FixedStepSeconds);
            probeOriginalBinding = DescribeNativeBuffer(scene, probeSource, "post-logical-seal retry source", failures);
            requestedByteSize = ComputeRequestedByteSize(probeOriginalBinding, failures);
            VulkanExplicitProductionBufferStressProbeRequest request = new(
                probeSource,
                EVulkanExplicitProductionBufferStressCheckpoint.AfterNativeRecording,
                requestedByteSize);
            VulkanExplicitProductionSubmissionReceipt probeReceipt = scene.SubmitStep(options.FixedStepSeconds, request);
            evidence = evidence with { ProbeReceipt = probeReceipt };
            bool completionQueryAccepted = scene.Host.TryGetProductionSubmissionCompletion(in probeReceipt, out bool completedBeforeWait);
            if (!completionQueryAccepted)
                failures.Add("Probe: renderer rejected the exact submitted-frame receipt.");

            VulkanNativeBufferDiagnosticDescription postProbeBinding = DescribeNativeBuffer(scene, probeSource, "probe", failures);
            RenderBenchScenarioLane.WaitForCompletion(scene.Host, in probeReceipt);
            _ = scene.Host.TryGetLastProductionBufferStressProbeEvidence(out VulkanExplicitProductionBufferStressProbeEvidence? afterCompletion);

            // Descriptor pool retirement can be queued on the first subsequent
            // slot, then release old buffer pins after that buffer slot was
            // already drained. Two complete ordinary slot rotations plus one
            // revisit prove the dependent queues without a forced cleanup.
            int drainSubmissionCount = checked((int)options.FrameSlots) * 2 + 1;
            for (int index = 0; index < drainSubmissionCount; index++)
                _ = SubmitAndWait(scene, options.FixedStepSeconds);

            _ = scene.Host.TryGetLastProductionBufferStressProbeEvidence(out VulkanExplicitProductionBufferStressProbeEvidence? afterSlotDrain);
            evidence = new RenderBenchNativeBufferStressEvidence
            {
                CMinusOneReceipt = cMinusOneReceipt,
                CReceipt = cReceipt,
                CPlusOneReceipt = cPlusOneReceipt,
                LogicalSealRetryReceipt = logicalSealRetryReceipt,
                ProbeReceipt = probeReceipt,
                CMinusOne = cMinusOne.ToDto(),
                C = c.ToDto(),
                CPlusOne = cPlusOne.ToDto(),
                ProbeSource = "OpaqueDeferred.LateDrawIds",
                LogicalSealProbeSource = evidence.LogicalSealProbeSource,
                LogicalSealProbeOriginalBinding = evidence.LogicalSealProbeOriginalBinding,
                ProbeOriginalBinding = probeOriginalBinding,
                PostProbeBinding = postProbeBinding,
                CompletionQueryAccepted = completionQueryAccepted,
                CompletedBeforeWait = completedBeforeWait,
                DrainSubmissionCount = drainSubmissionCount,
                LogicalSealProbe = evidence.LogicalSealProbe,
                ProbeAfterCompletion = afterCompletion,
                ProbeAfterSlotDrain = afterSlotDrain,
            };
            ValidateProbe(evidence, requestedByteSize, failures);
            evidence = evidence with { Failures = [.. failures], Passed = failures.Count == 0 };
        }
        catch (Exception exception)
        {
            failures.Add(exception.ToString());
            _ = scene.Host.TryGetLastProductionBufferStressProbeEvidence(out VulkanExplicitProductionBufferStressProbeEvidence? recoveredProbe);
            evidence = evidence with
            {
                ProbeAfterCompletion = recoveredProbe,
                ProbeAfterSlotDrain = recoveredProbe,
                Failures = [.. failures],
            };
        }

        return identity with
        {
            Status = failures.Count == 0 ? "passed" : "failed",
            Failure = failures.Count == 0 ? null : failures[0],
            Failures = [.. failures],
            InFlightLifetimeProven = evidence.Passed,
            NativeBufferStress = evidence,
        };
    }

    private static VulkanExplicitProductionSubmissionReceipt SubmitAndWait(RenderBenchProductionScene scene, double fixedStepSeconds)
    {
        VulkanExplicitProductionSubmissionReceipt receipt = scene.SubmitStep(fixedStepSeconds);
        RenderBenchScenarioLane.WaitForCompletion(scene.Host, in receipt);
        return receipt;
    }

    private static CapacityObservation ObserveCapacity(
        RenderBenchProductionScene scene,
        uint expectedTotalCommandCount,
        string label,
        List<string> failures)
    {
        VulkanNativeBufferDiagnosticDescription binding = DescribeDrawMetadata(scene, label, failures);
        uint totalCommandCount = scene.GPUScene.TotalCommandCount;
        uint allocatedMaxCommandCount = scene.GPUScene.AllocatedMaxCommandCount;
        if (totalCommandCount != expectedTotalCommandCount)
            failures.Add($"{label}: expected {expectedTotalCommandCount} commands, observed {totalCommandCount}.");
        if (allocatedMaxCommandCount < totalCommandCount)
            failures.Add($"{label}: allocated capacity {allocatedMaxCommandCount} is below active count {totalCommandCount}.");
        return new(totalCommandCount, allocatedMaxCommandCount, binding);
    }

    private static VulkanNativeBufferDiagnosticDescription DescribeDrawMetadata(
        RenderBenchProductionScene scene,
        string label,
        List<string> failures)
    {
        return DescribeNativeBuffer(scene, scene.GPUScene.DrawMetadataBuffer, label, failures);
    }

    private static VulkanNativeBufferDiagnosticDescription DescribeNativeBuffer(
        RenderBenchProductionScene scene,
        XRDataBuffer source,
        string label,
        List<string> failures)
    {
        if (!scene.Host.TryDescribeCurrentNativeBuffer(source, out VulkanNativeBufferDiagnosticDescription description) ||
            !IsActualNativeBinding(description))
        {
            failures.Add($"{label}: buffer has no actual native allocation observation.");
        }
        return description;
    }

    private static XRDataBuffer ResolveLateDrawIds(
        RenderBenchProductionScene scene,
        in VulkanExplicitProductionSubmissionReceipt receipt,
        List<string> failures)
    {
        if (!scene.Viewport.RenderPipelineInstance.MeshRenderCommands.TryGetGpuPass(
                (int)EDefaultRenderPass.OpaqueDeferred,
                out GPURenderPassCollection? pass) ||
            pass is null ||
            !pass.TryGetVisibilityDiagnostic(receipt.EngineFrameId, out GpuHiZTwoPassDiagnosticDescriptor diagnostic))
        {
            failures.Add("C+1: no submitted OpaqueDeferred LateDrawIds stream is available for the native stress probe.");
            throw new InvalidOperationException("The probe requires a submitted writable LateDrawIds buffer.");
        }

        return diagnostic.LateDrawIds;
    }

    private static XRDataBuffer ResolveLogicalSealProbeSource(RenderBenchProductionScene scene)
    {
        return scene.Viewport.RenderPipelineInstance.GetBuffer("ForwardPlusLocalLights") ??
            throw new InvalidOperationException(
                "The logical-seal probe requires the pipeline's ForwardPlusLocalLights buffer frozen by the accepted packet.");
    }

    private static uint ComputeRequestedByteSize(
        VulkanNativeBufferDiagnosticDescription binding,
        List<string> failures)
    {
        if (!IsActualNativeBinding(binding) || binding.AllocatedByteSize >= uint.MaxValue)
        {
            failures.Add("C+1: cannot construct a bounded native growth request from the actual allocation.");
            return 0;
        }
        return checked((uint)binding.AllocatedByteSize + 1u);
    }

    private static void ValidateProbe(
        RenderBenchNativeBufferStressEvidence evidence,
        uint requestedByteSize,
        List<string> failures)
    {
        ValidateCapacityBoundary(evidence, failures);
        ValidateReceiptSequence(evidence, failures);
        ValidateLogicalSealProbe(evidence, failures);

        VulkanExplicitProductionBufferStressProbeEvidence? afterCompletion = evidence.ProbeAfterCompletion;
        VulkanExplicitProductionBufferStressProbeEvidence? afterSlotDrain = evidence.ProbeAfterSlotDrain;
        if (!evidence.CompletionQueryAccepted)
            return;
        if (afterCompletion is null)
        {
            failures.Add("Probe: no evidence was available after exact receipt completion.");
            return;
        }
        if (afterSlotDrain is null)
        {
            failures.Add("Probe: no evidence was available after the recorded-frame slot drain.");
            return;
        }

        ValidateProbeSnapshot(afterCompletion, evidence, requestedByteSize, "completion", failures);
        ValidateProbeSnapshot(afterSlotDrain, evidence, requestedByteSize, "slot drain", failures);
        if (!afterCompletion.OldBindingRecordedByFrozenFrame || !afterCompletion.RecordedRetentionProven ||
            !afterCompletion.AfterGrowth.PendingRetirement ||
            (afterCompletion.AfterGrowth.RecordedReferences <= 0 && afterCompletion.AfterGrowth.DescriptorReferences <= 0))
        {
            failures.Add("Probe: recorded-generation retention was not proven by the native lifetime ledger.");
        }
        if (!afterCompletion.GpuOverlapObserved)
            failures.Add("Probe: GPU overlap was not observed; recorded pin retention is not sufficient.");
        if (afterCompletion.PrematureReclamationObserved || afterSlotDrain.PrematureReclamationObserved)
            failures.Add("Probe: the old native generation was reclaimed before GPU completion.");
        if (!afterSlotDrain.ReclamationObservedAfterCompletion)
            failures.Add("Probe: old-generation reclamation was not observed after slot drain and completion.");
        if (afterSlotDrain.Failure is not null)
            failures.Add($"Probe: {afterSlotDrain.Failure}");
    }

    private static void ValidateLogicalSealProbe(
        RenderBenchNativeBufferStressEvidence evidence,
        List<string> failures)
    {
        VulkanExplicitProductionBufferStressProbeEvidence? logical = evidence.LogicalSealProbe;
        if (logical is not
            {
                Checkpoint: EVulkanExplicitProductionBufferStressCheckpoint.AfterLogicalSeal,
                OldBindingFrozenByLogicalPlan: true,
                GrowthAttempted: true,
                GrowthObserved: true,
                LogicalPacketRejectedBeforeAcquire: true,
                AcquisitionAvoided: true,
                RetryRequired: true,
                SubmissionAllowed: false,
                OldBindingRecordedByFrozenFrame: false,
            })
        {
            failures.Add("AfterLogicalSeal: exact frozen binding growth and pre-acquire rejection were not proven.");
            return;
        }

        if (!IsActualNativeBinding(logical.OldBinding) ||
            !IsActualNativeBinding(logical.NewBinding) ||
            !SameBinding(logical.OldBinding, evidence.LogicalSealProbeOriginalBinding) ||
            logical.NewBinding.AllocatedByteSize < logical.RequestedByteSize ||
            (logical.NewBinding.BufferHandle == logical.OldBinding.BufferHandle &&
             logical.NewBinding.PublishedGeneration == logical.OldBinding.PublishedGeneration) ||
            logical.LogicalPlanNativeBufferBindingRevision == 0 ||
            logical.NativeBufferBindingRevisionAfterGrowth ==
                logical.LogicalPlanNativeBufferBindingRevision)
        {
            failures.Add("AfterLogicalSeal: native identity or logical binding revision transition was not exact.");
        }

        if (!evidence.LogicalSealRetryReceipt.IsValid ||
            evidence.LogicalSealRetryReceipt.ExplicitFrameNumber <=
                evidence.CPlusOneReceipt.ExplicitFrameNumber)
        {
            failures.Add("AfterLogicalSeal: fresh retry receipt was not accepted after the stale logical packet rejection.");
        }
    }

    private static void ValidateProbeSnapshot(
        VulkanExplicitProductionBufferStressProbeEvidence probe,
        RenderBenchNativeBufferStressEvidence evidence,
        uint requestedByteSize,
        string snapshotLabel,
        List<string> failures)
    {
        if (probe.Checkpoint != EVulkanExplicitProductionBufferStressCheckpoint.AfterNativeRecording)
            failures.Add("Probe ran at a checkpoint other than AfterNativeRecording.");
        if (probe.RequestedByteSize != requestedByteSize || probe.RequestedByteSize <= evidence.ProbeOriginalBinding.AllocatedByteSize)
            failures.Add("Probe request did not exceed the actual submitted LateDrawIds native allocation capacity.");
        if (!IsActualNativeBinding(probe.OldBinding) || !IsActualNativeBinding(probe.NewBinding))
            failures.Add("Probe did not report actual old and new native bindings.");
        if (!SameBinding(probe.OldBinding, evidence.ProbeOriginalBinding))
            failures.Add("Probe old binding does not match the submitted LateDrawIds native allocation.");
        if (!SameBinding(probe.NewBinding, evidence.PostProbeBinding))
            failures.Add("Probe new binding does not match the post-probe allocation.");
        if (SameBinding(probe.OldBinding, probe.NewBinding))
            failures.Add("Probe did not observe a new native handle or generation.");
        if (!probe.GrowthAttempted || !probe.GrowthObserved || !probe.SubmissionAllowed)
            failures.Add("Native growth was not attempted, observed, and allowed by the real submission path.");
        if (probe.Submission != evidence.ProbeReceipt)
            failures.Add($"Probe {snapshotLabel} evidence is not bound to the exact submitted-frame receipt.");
    }

    private static void ValidateCapacityBoundary(RenderBenchNativeBufferStressEvidence evidence, List<string> failures)
    {
        if (evidence.CMinusOne.AllocatedMaxCommandCount != CCommandCount ||
            evidence.C.AllocatedMaxCommandCount != CCommandCount ||
            evidence.CPlusOne.AllocatedMaxCommandCount != CCommandCount * 2)
        {
            failures.Add(
                $"Capacity boundary was not the expected {CCommandCount}/{CCommandCount}/{CCommandCount * 2} transition. " +
                $"Observed {evidence.CMinusOne.AllocatedMaxCommandCount}/{evidence.C.AllocatedMaxCommandCount}/{evidence.CPlusOne.AllocatedMaxCommandCount}.");
        }

        if (!SameBinding(evidence.CMinusOne.Binding, evidence.C.Binding))
            failures.Add("C-1 and C did not retain the same native DrawMetadata allocation.");
        if (SameBinding(evidence.C.Binding, evidence.CPlusOne.Binding) ||
            evidence.CPlusOne.Binding.AllocatedByteSize <= evidence.C.Binding.AllocatedByteSize)
        {
            failures.Add("C+1 did not publish a larger, new native DrawMetadata allocation at the 8-to-16 capacity boundary.");
        }
    }

    private static void ValidateReceiptSequence(RenderBenchNativeBufferStressEvidence evidence, List<string> failures)
    {
        VulkanExplicitProductionSubmissionReceipt cMinusOne = evidence.CMinusOneReceipt;
        VulkanExplicitProductionSubmissionReceipt c = evidence.CReceipt;
        VulkanExplicitProductionSubmissionReceipt cPlusOne = evidence.CPlusOneReceipt;
        VulkanExplicitProductionSubmissionReceipt probe = evidence.ProbeReceipt;
        if (!cMinusOne.IsValid || !c.IsValid || !cPlusOne.IsValid || !probe.IsValid)
        {
            failures.Add("The C-1/C/C+1/probe sequence did not return four valid production submission receipts.");
            return;
        }

        if (!SameReceiptOwner(in cMinusOne, in c) || !SameReceiptOwner(in cMinusOne, in cPlusOne) ||
            !SameReceiptOwner(in cMinusOne, in probe))
        {
            failures.Add("The C-1/C/C+1/probe receipts do not share the same renderer host, backend, device, and target generation.");
        }

        if (!StrictlyPrecedes(in cMinusOne, in c) || !StrictlyPrecedes(in c, in cPlusOne) ||
            !StrictlyPrecedes(in cPlusOne, in probe))
        {
            failures.Add("The C-1/C/C+1/probe receipts are not strictly monotonic by explicit frame, engine frame, and graphics timeline signal.");
        }
    }

    private static bool SameReceiptOwner(
        in VulkanExplicitProductionSubmissionReceipt first,
        in VulkanExplicitProductionSubmissionReceipt second)
        => first.OwnerIdentity == second.OwnerIdentity &&
           first.BackendGeneration == second.BackendGeneration &&
           first.DeviceHandle == second.DeviceHandle &&
           first.TargetGeneration == second.TargetGeneration;

    private static bool StrictlyPrecedes(
        in VulkanExplicitProductionSubmissionReceipt first,
        in VulkanExplicitProductionSubmissionReceipt second)
        => first.ExplicitFrameNumber < second.ExplicitFrameNumber &&
           first.EngineFrameId < second.EngineFrameId &&
           first.GraphicsTimelineSignal < second.GraphicsTimelineSignal;

    private static bool IsActualNativeBinding(VulkanNativeBufferDiagnosticDescription binding)
        => binding.IsGenerated && binding.IsDeviceOperational && binding.BufferHandle != 0 &&
           binding.PublishedGeneration != 0 && binding.AllocatedByteSize != 0;

    private static bool SameBinding(VulkanNativeBufferDiagnosticDescription first, VulkanNativeBufferDiagnosticDescription second)
        => first.BufferHandle == second.BufferHandle && first.PublishedGeneration == second.PublishedGeneration;

    private readonly record struct CapacityObservation(
        uint TotalCommandCount,
        uint AllocatedMaxCommandCount,
        VulkanNativeBufferDiagnosticDescription Binding)
    {
        public RenderBenchNativeBufferStressCapacity ToDto()
            => new()
            {
                TotalCommandCount = TotalCommandCount,
                AllocatedMaxCommandCount = AllocatedMaxCommandCount,
                Binding = Binding,
            };
    }
}
