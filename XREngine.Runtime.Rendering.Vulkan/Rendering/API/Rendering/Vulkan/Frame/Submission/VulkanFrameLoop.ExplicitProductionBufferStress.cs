using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameLoop
{
    private VulkanExplicitProductionBufferStressProbeEvidence? _lastExplicitProductionBufferStressProbeEvidence;

    internal bool TryGetLastExplicitProductionBufferStressProbeEvidence(out VulkanExplicitProductionBufferStressProbeEvidence? evidence)
    {
        RefreshExplicitProductionBufferStressEvidence();
        evidence = _lastExplicitProductionBufferStressProbeEvidence;
        return evidence is not null;
    }

    private void ExecuteAfterLogicalSealBufferStressProbe(
        VulkanExplicitProductionBufferStressProbeRequest probe,
        VulkanAcceptedFramePlan acceptedPlan)
    {
        _lastExplicitProductionBufferStressProbeEvidence = new()
        {
            Checkpoint = probe.Checkpoint,
            RequestedByteSize = probe.RequestedByteSize,
        };
        try
        {
            XRDataBuffer probeBuffer = ResolveAfterLogicalSealProbeBuffer(
                probe,
                acceptedPlan.LogicalPlan);
            if (!RuntimeEngine.IsRenderThread || probeBuffer.ElementSize == 0 ||
                probe.RequestedByteSize > 16 * 1024 * 1024)
            {
                throw new InvalidOperationException(
                    "The probe requires a render-thread buffer with nonzero element size and a growth request bounded to 16 MiB.");
            }
            if (!TryDescribeCurrentNativeBuffer(probeBuffer, out VulkanNativeBufferDiagnosticDescription oldBinding) ||
                (probe.RequestedByteSize != 0 && probe.RequestedByteSize <= oldBinding.AllocatedByteSize))
            {
                throw new InvalidOperationException(
                    "The probe must exceed an observed current native allocation capacity.");
            }

            uint requestedByteSize = probe.RequestedByteSize == 0
                ? checked((uint)oldBinding.AllocatedByteSize + 1u)
                : probe.RequestedByteSize;
            if (requestedByteSize > 16 * 1024 * 1024)
                throw new InvalidOperationException("The exact sealed buffer exceeds the probe's 16 MiB growth bound.");

            if (!TryFindFrozenLogicalBufferBinding(
                    acceptedPlan.LogicalPlan,
                    in oldBinding,
                    out ulong logicalRevision,
                    out int contextPlanCount,
                    out int matchingBarrierCount,
                    out string frozenBindings))
            {
                throw new InvalidOperationException(
                    $"The probe buffer is not an exact frozen native barrier binding of the " +
                    $"accepted logical packet's recorded context plans. contexts={contextPlanCount} " +
                    $"matches={matchingBarrierCount} handle=0x{oldBinding.BufferHandle:X} " +
                    $"generation={oldBinding.PublishedGeneration}. frozen={frozenBindings}");
            }

            _lastExplicitProductionBufferStressProbeEvidence =
                _lastExplicitProductionBufferStressProbeEvidence with
                {
                    OldBinding = oldBinding,
                    RequestedByteSize = requestedByteSize,
                    OldBindingFrozenByLogicalPlan = true,
                    LogicalPlanNativeBufferBindingRevision = logicalRevision,
                    GrowthAttempted = true,
                };

            uint elements = checked((uint)(((ulong)requestedByteSize +
                probeBuffer.ElementSize - 1) / probeBuffer.ElementSize));
            if (!probeBuffer.Resize(elements))
                throw new InvalidOperationException("The probe's normal buffer resize was declined.");
            probeBuffer.PushData();

            if (!TryDescribeCurrentNativeBuffer(probeBuffer, out VulkanNativeBufferDiagnosticDescription newBinding))
                throw new InvalidOperationException("The resized buffer did not publish a current native allocation.");

            ulong revisionAfterGrowth = _resourceRuntime.NativeBufferBindingRevision;
            bool grew = newBinding.AllocatedByteSize >= requestedByteSize &&
                (newBinding.BufferHandle != oldBinding.BufferHandle ||
                 newBinding.PublishedGeneration != oldBinding.PublishedGeneration);
            _lastExplicitProductionBufferStressProbeEvidence =
                _lastExplicitProductionBufferStressProbeEvidence with
                {
                    NewBinding = newBinding,
                    NativeBufferBindingRevisionAfterGrowth = revisionAfterGrowth,
                    GrowthObserved = grew,
                    Failure = !grew ? "Native capacity/generation did not change." :
                        revisionAfterGrowth == logicalRevision
                            ? "Native buffer binding revision did not change after growth."
                            : null,
                };
            if (!grew || revisionAfterGrowth == logicalRevision)
                throw new InvalidOperationException(
                    _lastExplicitProductionBufferStressProbeEvidence.Failure);
        }
        catch (Exception exception)
        {
            _lastExplicitProductionBufferStressProbeEvidence =
                _lastExplicitProductionBufferStressProbeEvidence with
                {
                    Failure = exception.Message,
                };
            throw;
        }
    }

    private XRDataBuffer ResolveAfterLogicalSealProbeBuffer(
        VulkanExplicitProductionBufferStressProbeRequest probe,
        FramePlan logicalPlan)
    {
        if (string.IsNullOrWhiteSpace(probe.LogicalResourceName))
            return probe.Buffer;

        ReadOnlySpan<FrameOpContext> contexts = logicalPlan.StaticPlannerContexts;
        ReadOnlySpan<VulkanRenderGraphPlan> plans = logicalPlan.StaticPlannerContextPlans;
        if (contexts.Length != plans.Length)
            throw new InvalidOperationException(
                "The accepted packet has mismatched sealed context and render-graph plan counts.");

        XRDataBuffer? resolved = null;
        for (int contextIndex = 0; contextIndex < contexts.Length; contextIndex++)
        {
            VulkanBarrierPlan barriers = plans[contextIndex].Barriers;
            ReadOnlySpan<VulkanFrozenBufferBarrier> frozen = barriers.BufferBarriers;
            for (int barrierIndex = 0; barrierIndex < frozen.Length; barrierIndex++)
            {
                VulkanFrozenBufferBarrier barrier = frozen[barrierIndex];
                if (!string.Equals(
                        barrier.LogicalResourceName,
                        probe.LogicalResourceName,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                FrameOpContext context = contexts[contextIndex];
                XRDataBuffer? candidate = ResolveContextBuffer(
                    in context,
                    probe.LogicalResourceName);
                if (candidate is null ||
                    !TryDescribeCurrentNativeBuffer(candidate, out VulkanNativeBufferDiagnosticDescription binding) ||
                    binding.BufferHandle != barrier.NativeBuffer.Handle ||
                    binding.PublishedGeneration != barrier.NativeGeneration)
                {
                    continue;
                }

                if (resolved is not null && !ReferenceEquals(resolved, candidate))
                {
                    throw new InvalidOperationException(
                        $"The accepted packet has ambiguous exact owners for logical buffer " +
                        $"'{probe.LogicalResourceName}'.");
                }

                resolved = candidate;
            }
        }

        return resolved ?? throw new InvalidOperationException(
            $"The accepted packet has no exact sealed-context owner for logical buffer " +
            $"'{probe.LogicalResourceName}'.");
    }

    private static XRDataBuffer? ResolveContextBuffer(
        in FrameOpContext context,
        string logicalResourceName)
    {
        if (context.ResourceRegistry?.TryGetBuffer(logicalResourceName, out XRDataBuffer? buffer) == true)
            return buffer;
        return context.PipelineInstance?.GetBuffer(logicalResourceName);
    }

    private static bool TryFindFrozenLogicalBufferBinding(
        FramePlan logicalPlan,
        in VulkanNativeBufferDiagnosticDescription binding,
        out ulong logicalRevision,
        out int contextPlanCount,
        out int matchingBarrierCount,
        out string frozenBindings)
    {
        ReadOnlySpan<VulkanRenderGraphPlan> contextPlans =
            logicalPlan.StaticPlannerContextPlans;
        matchingBarrierCount = 0;
        ulong firstMatchingRevision = 0;
        List<string> descriptions = [];
        for (int planIndex = 0; planIndex < contextPlans.Length; planIndex++)
        {
            VulkanBarrierPlan barriers = contextPlans[planIndex].Barriers;
            ReadOnlySpan<VulkanFrozenBufferBarrier> frozen = barriers.BufferBarriers;
            for (int barrierIndex = 0; barrierIndex < frozen.Length; barrierIndex++)
            {
                VulkanFrozenBufferBarrier candidate = frozen[barrierIndex];
                if (descriptions.Count < 16)
                {
                    descriptions.Add($"{candidate.LogicalResourceName}@0x{candidate.NativeBuffer.Handle:X}" +
                        $"/g{candidate.NativeGeneration}");
                }
                if (candidate.NativeBuffer.Handle != binding.BufferHandle ||
                    candidate.NativeGeneration != binding.PublishedGeneration)
                {
                    continue;
                }

                matchingBarrierCount++;
                if (firstMatchingRevision == 0)
                    firstMatchingRevision = barriers.NativeBufferBindingRevision;
            }
        }

        logicalRevision = firstMatchingRevision;
        contextPlanCount = contextPlans.Length;
        frozenBindings = descriptions.Count == 0 ? "none" : string.Join(",", descriptions);
        return matchingBarrierCount != 0;
    }

    private void MarkAfterLogicalSealBufferStressProbeRejectedBeforeAcquire(
        VulkanNativeBufferBindingSupersededException exception)
    {
        if (_lastExplicitProductionBufferStressProbeEvidence is not
            {
                Checkpoint: EVulkanExplicitProductionBufferStressCheckpoint.AfterLogicalSeal,
                OldBindingFrozenByLogicalPlan: true,
                GrowthObserved: true,
            } evidence)
        {
            return;
        }

        _lastExplicitProductionBufferStressProbeEvidence = evidence with
        {
            LogicalPacketRejectedBeforeAcquire = true,
            AcquisitionAvoided = true,
            RetryRequired = true,
            Failure = exception.Message,
        };
    }

    private void ExecuteAfterNativeRecordingBufferStressProbe(
        VulkanExplicitProductionBufferStressProbeRequest probe, CommandBuffer commandBuffer, SealedSubmissionContract? recordedContract)
    {
        _lastExplicitProductionBufferStressProbeEvidence = new()
        {
            Checkpoint = probe.Checkpoint, RequestedByteSize = probe.RequestedByteSize,
        };
        try
        {
            if (!RuntimeEngine.IsRenderThread || probe.Buffer.ElementSize == 0 ||
                probe.RequestedByteSize > 16 * 1024 * 1024)
                throw new InvalidOperationException("The probe requires a render-thread buffer with nonzero element size and a growth request bounded to 16 MiB.");
            if (!TryDescribeCurrentNativeBuffer(probe.Buffer, out VulkanNativeBufferDiagnosticDescription oldBinding) ||
                probe.RequestedByteSize <= oldBinding.AllocatedByteSize)
                throw new InvalidOperationException("The probe must exceed an observed current native allocation capacity.");
            VulkanResourceLifetimeKey key = new(ObjectType.Buffer, oldBinding.BufferHandle);
            VulkanResourceLifetimeTracker tracker = _resourceRuntime.Lifetime.Tracker;
            lock (tracker.SyncRoot)
            {
                if (recordedContract?.CommandBufferHandle != unchecked((ulong)(nuint)commandBuffer.Handle) ||
                    !TryFindRecordedBufferGeneration(recordedContract, key, out ulong generation) || generation != oldBinding.PublishedGeneration)
                    throw new InvalidOperationException("The probe buffer is not an exact recorded dependency of this production frame.");
            }
            VulkanNativeBufferLifetimeDiagnostic before = DescribeBufferLifetime(in oldBinding);
            _lastExplicitProductionBufferStressProbeEvidence = _lastExplicitProductionBufferStressProbeEvidence with
            {
                OldBinding = oldBinding, OldBindingRecordedByFrozenFrame = true, BeforeGrowth = before,
                OldDescriptorOwners = DescribeBufferDescriptorOwners(in oldBinding),
                RecordedCommandBufferHandle = unchecked((ulong)(nuint)commandBuffer.Handle),
                GrowthAttempted = true,
            };
            uint elements = checked((uint)(((ulong)probe.RequestedByteSize + probe.Buffer.ElementSize - 1) / probe.Buffer.ElementSize));
            if (!probe.Buffer.Resize(elements))
                throw new InvalidOperationException("The probe's normal buffer resize was declined.");
            probe.Buffer.PushData();
            if (!TryDescribeCurrentNativeBuffer(probe.Buffer, out VulkanNativeBufferDiagnosticDescription newBinding))
                throw new InvalidOperationException("The resized buffer did not publish a current native allocation.");
            VulkanNativeBufferLifetimeDiagnostic after = DescribeBufferLifetime(in oldBinding);
            bool grew = newBinding.AllocatedByteSize >= probe.RequestedByteSize &&
                (newBinding.BufferHandle != oldBinding.BufferHandle || newBinding.PublishedGeneration != oldBinding.PublishedGeneration);
            bool retained = before.Found && (before.RecordedReferences > 0 || before.DescriptorReferences > 0) && after.Found &&
                after.PendingRetirement && !after.Destroyed &&
                (after.RecordedReferences > 0 || after.DescriptorReferences > 0) && !after.RetirementReady;
            _lastExplicitProductionBufferStressProbeEvidence = _lastExplicitProductionBufferStressProbeEvidence with
            {
                NewBinding = newBinding, AfterGrowth = after, LatestLifetime = after,
                GrowthObserved = grew, RecordedRetentionProven = retained,
                Failure = !grew ? "Native capacity/generation did not change." : !retained ? "Recorded-generation retention was not proven." : null,
            };
            if (!grew || !retained)
                throw new InvalidOperationException(_lastExplicitProductionBufferStressProbeEvidence.Failure);
        }
        catch (Exception exception)
        {
            _lastExplicitProductionBufferStressProbeEvidence = _lastExplicitProductionBufferStressProbeEvidence with { Failure = exception.Message };
            throw;
        }
    }

    private VulkanNativeBufferLifetimeDiagnostic DescribeBufferLifetime(in VulkanNativeBufferDiagnosticDescription binding)
    {
        VulkanResourceLifetimeTracker tracker = _resourceRuntime.Lifetime.Tracker;
        lock (tracker.SyncRoot)
        {
            if (!tracker.TryResolveResourceGenerationNoLock(new(ObjectType.Buffer, binding.BufferHandle), binding.PublishedGeneration,
                    out VulkanResourceLifetimeRecord resource))
                return default;
            bool ready = tracker.IsRetirementReadyNoLock(resource.RetirementTicket) &&
                resource.Pins.IsRetirementReady(tracker.CompletedGraphicsSequence, tracker.CompletedTransferSequence, tracker.CompletedOtherSequence);
            return new(true,
                (resource.State & EVulkanResourceLifetimeState.PendingRetirement) != 0,
                (resource.State & EVulkanResourceLifetimeState.Destroyed) != 0,
                ready, resource.Pins.RecordedReferenceCount, resource.Pins.DescriptorReferenceCount,
                resource.Pins.TemplateReferenceCount, resource.Pins.QueuedReferenceCount,
                resource.Pins.LastGraphicsSequence, resource.Pins.LastTransferSequence, tracker.CompletedGraphicsSequence);
        }
    }

    private VulkanNativeBufferDescriptorOwnerDiagnostic[] DescribeBufferDescriptorOwners(
        in VulkanNativeBufferDiagnosticDescription binding)
    {
        VulkanResourceLifetimeTracker tracker = _resourceRuntime.Lifetime.Tracker;
        lock (tracker.SyncRoot)
        {
            VulkanResourceLifetimeKey key = new(ObjectType.Buffer, binding.BufferHandle);
            List<VulkanNativeBufferDescriptorOwnerDiagnostic>? owners = null;
            foreach ((ulong descriptorSetHandle, VulkanDescriptorSetLifetimeRecord state)
                     in tracker.DescriptorSetLifetimes)
            {
                if (!state.PinnedReferences.TryGetValue(key, out ulong generation) ||
                    generation != binding.PublishedGeneration)
                {
                    continue;
                }

                (owners ??= []).Add(new VulkanNativeBufferDescriptorOwnerDiagnostic(
                    descriptorSetHandle,
                    state.Generation,
                    state.Pool.Handle,
                    state.Owner));
            }

            return owners is null ? [] : [.. owners];
        }
    }

    private void MarkExplicitProductionBufferStressSubmitted(in VulkanExplicitProductionSubmissionReceipt receipt)
    {
        if (_lastExplicitProductionBufferStressProbeEvidence is not { } evidence)
            return;
        if (!receipt.IsValid || receipt.CommandBufferHandle != evidence.RecordedCommandBufferHandle)
        {
            _lastExplicitProductionBufferStressProbeEvidence = evidence with { Failure = "The native submission does not match the probe's recorded command buffer." };
            return;
        }
        VulkanResourceLifetimeTracker tracker = _resourceRuntime.Lifetime.Tracker;
        bool oldGenerationSubmitted = false;
        lock (tracker.SyncRoot)
        {
            if (tracker.TryResolveResourceGenerationNoLock(new(ObjectType.Buffer, evidence.OldBinding.BufferHandle),
                    evidence.OldBinding.PublishedGeneration, out VulkanResourceLifetimeRecord oldResource))
                foreach (VulkanLifetimeSubmission submission in tracker.LifetimeSubmissions)
                    if (submission.QueueDomain == EVulkanLifetimeQueueDomain.Graphics &&
                        submission.TimelineSemaphoreHandle == _commandRuntime.Synchronization._graphicsTimelineSemaphore.Handle &&
                        submission.TimelineValue == receipt.GraphicsTimelineSignal &&
                        oldResource.Pins.LastGraphicsSequence == submission.QueueSequence)
                    {
                        oldGenerationSubmitted = true;
                        break;
                    }
        }
        if (!oldGenerationSubmitted)
        {
            _lastExplicitProductionBufferStressProbeEvidence = evidence with { Failure = "The accepted submission did not prove ownership of the recorded old buffer generation." };
            return;
        }

        // Query the real graphics timeline as soon as queue ownership of the
        // exact old generation is established. Do this before any cold ledger
        // snapshots or post-submit housekeeping can outlast a short GPU frame.
        if (!TryGetExplicitProductionSubmissionCompletion(in receipt, out bool completed))
        {
            _lastExplicitProductionBufferStressProbeEvidence = evidence with
            {
                Failure = "The accepted submission receipt was rejected before overlap sampling.",
            };
            return;
        }

        _lastExplicitProductionBufferStressProbeEvidence = evidence with
        {
            SubmissionAllowed = true,
            Submission = receipt,
            GpuOverlapObserved = !completed,
        };
        RefreshExplicitProductionBufferStressEvidence();
    }

    private void ObserveExplicitProductionBufferStressSlotReuse(in VulkanExplicitProductionSubmissionReceipt receipt)
    {
        if (_lastExplicitProductionBufferStressProbeEvidence is not { SubmissionAllowed: true } evidence)
            return;
        VulkanExplicitProductionSubmissionReceipt original = evidence.Submission;
        if (receipt.OwnerIdentity != original.OwnerIdentity || receipt.BackendGeneration != original.BackendGeneration ||
            receipt.DeviceHandle != original.DeviceHandle || receipt.TargetGeneration != original.TargetGeneration ||
            receipt.ExplicitFrameNumber <= original.ExplicitFrameNumber ||
            receipt.ExpectedFrameSlot != original.ExpectedFrameSlot || receipt.CommandBufferHandle != original.CommandBufferHandle)
            return;

        // A later accepted submission using the same target slot and primary command
        // buffer follows the driver's fence wait and tracked command-pool reset.
        _lastExplicitProductionBufferStressProbeEvidence = evidence with
        {
            RecordedFrameSlotReused = true, SlotReuseSubmission = receipt,
        };
        RefreshExplicitProductionBufferStressEvidence();
    }

    private void RefreshExplicitProductionBufferStressEvidence()
    {
        if (_lastExplicitProductionBufferStressProbeEvidence is not { SubmissionAllowed: true } evidence)
            return;
        VulkanNativeBufferDiagnosticDescription oldBinding = evidence.OldBinding;
        VulkanNativeBufferLifetimeDiagnostic lifetime = DescribeBufferLifetime(in oldBinding);
        VulkanExplicitProductionSubmissionReceipt receipt = evidence.Submission;
        if (!TryGetExplicitProductionSubmissionCompletion(in receipt, out bool completed))
            return;
        // A pending completion query during the cold snapshot is corroborating
        // evidence; the primary overlap sample is latched at queue acceptance.
        bool overlap = !completed && lifetime.Found && !lifetime.Destroyed && lifetime.PendingRetirement;
        bool prematureReclamation = evidence.PrematureReclamationObserved ||
            (!completed && (!lifetime.Found || lifetime.Destroyed));
        bool reclaimed = !prematureReclamation && completed && evidence.RecordedRetentionProven && evidence.RecordedFrameSlotReused &&
            (!lifetime.Found || lifetime.Destroyed);
        _lastExplicitProductionBufferStressProbeEvidence = evidence with
        {
            LatestLifetime = lifetime,
            OldDescriptorOwners = DescribeBufferDescriptorOwners(in oldBinding),
            GpuOverlapObserved = evidence.GpuOverlapObserved || overlap,
            PrematureReclamationObserved = prematureReclamation,
            ReclamationObservedAfterCompletion = !prematureReclamation && (evidence.ReclamationObservedAfterCompletion || reclaimed),
            Failure = prematureReclamation ? "The old native generation disappeared before its submission completed." : evidence.Failure,
        };
    }
}
