using System.Runtime.CompilerServices;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanTextureUploadService
{
    private readonly ConditionalWeakTable<XRTexture2D, VulkanTextureUploadGenerationRecord>
        _uploadGenerations = new();

    private bool RegisterUploadGeneration(
        XRTexture2D texture,
        in VulkanImportedTextureUploadRequest request,
        out string? failureReason)
    {
        VulkanTextureUploadGenerationRecord record = _uploadGenerations.GetValue(
            texture,
            static _ => new VulkanTextureUploadGenerationRecord());
        using (VulkanFrameLockScope.Enter(
                   record.Sync,
                   EVulkanFrameWaitReason.UploadLock))
        {
            if (FindUploadGenerationNoLock(record, request.Ticket) is not null)
            {
                failureReason = null;
                return true;
            }

            if (record.Entries.Count >= VulkanTextureUploadGenerationRecord.Capacity)
            {
                if (!TryEvictRetiredUploadGenerationNoLock(record))
                {
                    failureReason =
                        $"Texture upload generation history reached its bounded capacity of " +
                        $"{VulkanTextureUploadGenerationRecord.Capacity}; every entry is active, " +
                        "is the current published generation, or is pinned by an accepted frame.";
                    return false;
                }
            }

            record.Entries.Add(new VulkanTextureUploadGenerationEntry
            {
                Ticket = request.Ticket,
                StreamingGeneration = request.StreamingGeneration,
                PriorityClass = request.PriorityClass,
                State = VulkanTextureUploadGenerationState.UploadQueued,
                Detail = "Vulkan upload generation registered",
            });
            failureReason = null;
            return true;
        }
    }

    private void UpdateUploadGeneration(
        in VulkanImportedTextureUploadRequest request,
        VulkanTextureUploadGenerationState state,
        string? detail)
    {
        if (!request.TryGetTexture(out XRTexture2D? texture) || texture is null ||
            !_uploadGenerations.TryGetValue(texture, out VulkanTextureUploadGenerationRecord? record))
            return;

        using (VulkanFrameLockScope.Enter(
                   record.Sync,
                   EVulkanFrameWaitReason.UploadLock))
        {
            VulkanTextureUploadGenerationEntry? entry =
                FindUploadGenerationNoLock(record, request.Ticket);
            if (entry is null)
                return;
            if (!CanTransitionUploadGeneration(entry.State, state))
                return;

            entry.State = state;
            entry.Detail = detail;
            if (state is VulkanTextureUploadGenerationState.Published or
                    VulkanTextureUploadGenerationState.Retired)
            {
                record.LatestPublishedStreamingGeneration = Math.Max(
                    record.LatestPublishedStreamingGeneration,
                    entry.StreamingGeneration);
            }
        }
    }

    private static bool TryEvictRetiredUploadGenerationNoLock(
        VulkanTextureUploadGenerationRecord record)
    {
        int candidateIndex = -1;
        long candidateSequence = long.MaxValue;
        for (int index = 0; index < record.Entries.Count; index++)
        {
            VulkanTextureUploadGenerationEntry candidate = record.Entries[index];
            if (!candidate.IsTerminal || candidate.PinCount != 0 ||
                candidate.StreamingGeneration ==
                    record.LatestPublishedStreamingGeneration ||
                candidate.Ticket.Sequence >= candidateSequence)
            {
                continue;
            }

            candidateIndex = index;
            candidateSequence = candidate.Ticket.Sequence;
        }

        if (candidateIndex < 0)
            return false;

        record.Entries.RemoveAt(candidateIndex);
        return true;
    }

    private static bool CanTransitionUploadGeneration(
        VulkanTextureUploadGenerationState current,
        VulkanTextureUploadGenerationState next)
    {
        if (current == next)
            return true;
        if (current == VulkanTextureUploadGenerationState.Published)
            return next == VulkanTextureUploadGenerationState.Retired;
        if (current is VulkanTextureUploadGenerationState.Retired or
                VulkanTextureUploadGenerationState.Canceled or
                VulkanTextureUploadGenerationState.Failed)
        {
            return false;
        }
        if (next is VulkanTextureUploadGenerationState.Canceled or
                VulkanTextureUploadGenerationState.Failed)
        {
            return true;
        }

        return GetUploadGenerationProgressRank(next) >=
            GetUploadGenerationProgressRank(current);
    }

    private static int GetUploadGenerationProgressRank(
        VulkanTextureUploadGenerationState state)
        => state switch
        {
            VulkanTextureUploadGenerationState.Decoded => 0,
            VulkanTextureUploadGenerationState.UploadQueued or
            VulkanTextureUploadGenerationState.PrepQueued or
            VulkanTextureUploadGenerationState.PrepDeferred => 1,
            VulkanTextureUploadGenerationState.PrepRunning => 2,
            VulkanTextureUploadGenerationState.PrepReady => 3,
            VulkanTextureUploadGenerationState.UploadRecording => 4,
            VulkanTextureUploadGenerationState.GpuUploadPending or
            VulkanTextureUploadGenerationState.TransferSubmitted => 5,
            VulkanTextureUploadGenerationState.TransferComplete => 6,
            VulkanTextureUploadGenerationState.Uploaded => 7,
            VulkanTextureUploadGenerationState.DescriptorPublishPending => 8,
            VulkanTextureUploadGenerationState.Published => 9,
            VulkanTextureUploadGenerationState.Retired => 10,
            _ => int.MaxValue,
        };

    private bool TryGetUploadGeneration(
        XRTexture2D texture,
        in VulkanTextureUploadTicket ticket,
        out EVulkanFrameDependencyState dependencyState,
        out string? detail,
        out EVulkanPresentNowFailureDisposition failureDisposition)
    {
        dependencyState = EVulkanFrameDependencyState.Declared;
        detail = null;
        failureDisposition =
            EVulkanPresentNowFailureDisposition.RendererTerminal;
        if (!_uploadGenerations.TryGetValue(texture, out VulkanTextureUploadGenerationRecord? record))
            return false;

        using (VulkanFrameLockScope.Enter(
                   record.Sync,
                   EVulkanFrameWaitReason.UploadLock))
        {
            VulkanTextureUploadGenerationEntry? entry =
                FindUploadGenerationNoLock(record, ticket);
            if (entry is null)
                return false;

            dependencyState = MapDependencyState(entry.State);
            detail = entry.Detail;
            failureDisposition = ResolveUploadFailureDisposition(entry.State);
            return true;
        }
    }

    private bool TryGetUploadGeneration(
        XRTexture2D texture,
        long requiredGeneration,
        VulkanTextureUploadManifest? pinOwner,
        out VulkanTextureUploadTicket ticket,
        out TextureUploadPriorityClass priorityClass,
        out EVulkanFrameDependencyState dependencyState,
        out string? detail,
        out EVulkanPresentNowFailureDisposition failureDisposition)
    {
        ticket = default;
        priorityClass = TextureUploadPriorityClass.Background;
        dependencyState = EVulkanFrameDependencyState.Declared;
        detail = null;
        failureDisposition =
            EVulkanPresentNowFailureDisposition.RendererTerminal;
        if (!_uploadGenerations.TryGetValue(texture, out VulkanTextureUploadGenerationRecord? record))
            return false;

        using (VulkanFrameLockScope.Enter(
                   record.Sync,
                   EVulkanFrameWaitReason.UploadLock))
        {
            VulkanTextureUploadGenerationEntry? selected = null;
            for (int index = 0; index < record.Entries.Count; index++)
            {
                VulkanTextureUploadGenerationEntry candidate = record.Entries[index];
                if (candidate.StreamingGeneration != requiredGeneration ||
                    selected is not null && candidate.Ticket.Sequence <= selected.Ticket.Sequence)
                    continue;

                selected = candidate;
            }

            if (selected is null)
                return false;

            ticket = selected.Ticket;
            priorityClass = selected.PriorityClass;
            dependencyState = MapDependencyState(selected.State);
            detail = selected.Detail;
            failureDisposition = ResolveUploadFailureDisposition(selected.State);
            pinOwner?.PinGenerationNoLock(record, selected);
            return true;
        }
    }

    private bool TryPinUploadGeneration(
        VulkanTextureUploadManifest manifest,
        XRTexture2D? texture,
        in VulkanTextureUploadTicket ticket)
    {
        if (texture is null ||
            !_uploadGenerations.TryGetValue(
                texture,
                out VulkanTextureUploadGenerationRecord? record))
        {
            return false;
        }

        using (VulkanFrameLockScope.Enter(
                   record.Sync,
                   EVulkanFrameWaitReason.UploadLock))
        {
            VulkanTextureUploadGenerationEntry? entry =
                FindUploadGenerationNoLock(record, ticket);
            if (entry is null)
                return false;
            manifest.PinGenerationNoLock(record, entry);
            return true;
        }
    }

    private bool TryGetLatestUploadGeneration(
        XRTexture2D texture,
        out VulkanTextureUploadTicket ticket,
        out long generation,
        out TextureUploadPriorityClass priorityClass,
        out EVulkanFrameDependencyState dependencyState,
        out string? detail)
    {
        ticket = default;
        generation = 0L;
        priorityClass = TextureUploadPriorityClass.Background;
        dependencyState = EVulkanFrameDependencyState.Declared;
        detail = null;
        if (!_uploadGenerations.TryGetValue(texture, out VulkanTextureUploadGenerationRecord? record))
            return false;

        using (VulkanFrameLockScope.Enter(
                   record.Sync,
                   EVulkanFrameWaitReason.UploadLock))
        {
            VulkanTextureUploadGenerationEntry? selected = null;
            for (int index = 0; index < record.Entries.Count; index++)
            {
                VulkanTextureUploadGenerationEntry candidate = record.Entries[index];
                if (selected is not null &&
                    (candidate.StreamingGeneration < selected.StreamingGeneration ||
                     candidate.StreamingGeneration == selected.StreamingGeneration &&
                     candidate.Ticket.Sequence <= selected.Ticket.Sequence))
                    continue;

                selected = candidate;
            }

            if (selected is null)
                return false;

            ticket = selected.Ticket;
            generation = selected.StreamingGeneration;
            priorityClass = selected.PriorityClass;
            dependencyState = MapDependencyState(selected.State);
            detail = selected.Detail;
            return true;
        }
    }

    private static VulkanTextureUploadGenerationEntry? FindUploadGenerationNoLock(
        VulkanTextureUploadGenerationRecord record,
        in VulkanTextureUploadTicket ticket)
    {
        for (int index = 0; index < record.Entries.Count; index++)
            if (record.Entries[index].Ticket == ticket)
                return record.Entries[index];
        return null;
    }

    private static EVulkanFrameDependencyState MapDependencyState(
        VulkanTextureUploadGenerationState state)
        => state switch
        {
            VulkanTextureUploadGenerationState.PrepReady => EVulkanFrameDependencyState.CpuPrepared,
            VulkanTextureUploadGenerationState.UploadRecording or
            VulkanTextureUploadGenerationState.GpuUploadPending =>
                EVulkanFrameDependencyState.CpuPrepared,
            VulkanTextureUploadGenerationState.TransferSubmitted or
            VulkanTextureUploadGenerationState.TransferComplete or
            VulkanTextureUploadGenerationState.Uploaded or
            VulkanTextureUploadGenerationState.DescriptorPublishPending =>
                EVulkanFrameDependencyState.GpuSubmitted,
            VulkanTextureUploadGenerationState.Published or
            VulkanTextureUploadGenerationState.Retired => EVulkanFrameDependencyState.Ready,
            VulkanTextureUploadGenerationState.Canceled or
            VulkanTextureUploadGenerationState.Failed => EVulkanFrameDependencyState.TerminalFailed,
            _ => EVulkanFrameDependencyState.Declared,
        };

    private static EVulkanPresentNowFailureDisposition ResolveUploadFailureDisposition(
        VulkanTextureUploadGenerationState state)
        => state == VulkanTextureUploadGenerationState.Canceled
            ? EVulkanPresentNowFailureDisposition.RetryFrame
            : EVulkanPresentNowFailureDisposition.RendererTerminal;

    private void CaptureRequiredUploadGeneration(
        VulkanTextureUploadManifest manifest,
        XRTexture2D texture,
        long requiredGeneration)
    {
        if (!TryGetUploadGeneration(
                texture,
                requiredGeneration,
                manifest,
                out VulkanTextureUploadTicket ticket,
                out _,
                out EVulkanFrameDependencyState dependencyState,
                out string? detail,
                out EVulkanPresentNowFailureDisposition failureDisposition))
        {
            if (ImportedTextureStreamingManager.Instance.TryGetTerminalGenerationFailure(
                    texture,
                    requiredGeneration,
                    out string terminalFailure))
            {
                manifest.FailCapture(terminalFailure);
                return;
            }

            manifest.AddUnresolved(texture, requiredGeneration);
            return;
        }

        manifest.Add(ticket, texture);
        manifest.ApplyDurableState(
            ticket,
            dependencyState,
            detail,
            failureDisposition);
    }

    private void RefreshRequiredUploadGenerations(VulkanTextureUploadManifest manifest)
    {
        for (int index = 0; index < manifest.Count; index++)
        {
            XRTexture2D? texture = manifest.GetTexture(index);
            if (texture is null)
                continue;

            ref readonly VulkanTextureUploadTicket capturedTicket = ref manifest.GetTicket(index);
            if (TryGetUploadGeneration(
                    texture,
                    capturedTicket,
                    out EVulkanFrameDependencyState dependencyState,
                    out string? detail,
                    out EVulkanPresentNowFailureDisposition failureDisposition))
            {
                manifest.ApplyDurableState(
                    capturedTicket,
                    dependencyState,
                    detail,
                    failureDisposition);
                continue;
            }

            _ = manifest.Fail(
                capturedTicket,
                $"Required texture upload ticket {capturedTicket.Sequence}:" +
                $"{capturedTicket.StreamingGeneration} disappeared from the bounded exact-generation ledger.");
        }

        for (int index = manifest.UnresolvedCount - 1; index >= 0; index--)
        {
            if (!manifest.TryGetUnresolved(
                    index,
                    out XRTexture2D? texture,
                    out long requiredGeneration) || texture is null)
                continue;

            if (TryGetUploadGeneration(
                    texture,
                    requiredGeneration,
                    manifest,
                    out VulkanTextureUploadTicket ticket,
                    out _,
                    out EVulkanFrameDependencyState dependencyState,
                    out string? detail,
                    out EVulkanPresentNowFailureDisposition failureDisposition))
            {
                manifest.Add(ticket, texture);
                manifest.ApplyDurableState(
                    ticket,
                    dependencyState,
                    detail,
                    failureDisposition);
                manifest.ResolveUnresolved(index);
                continue;
            }

            if (!ImportedTextureStreamingManager.Instance.TryGetGenerationState(
                    texture,
                    out long publishedGeneration,
                    out long uploadGeneration,
                    out _,
                    out bool hasPendingTransition))
            {
                manifest.FailCapture(
                    $"Required texture generation {requiredGeneration} has no streaming or Vulkan upload state.");
                manifest.ResolveUnresolved(index);
                continue;
            }

            if (publishedGeneration > requiredGeneration ||
                uploadGeneration > requiredGeneration ||
                uploadGeneration == requiredGeneration && !hasPendingTransition)
            {
                manifest.FailCapture(
                    $"Required texture generation {requiredGeneration} was canceled or superseded before " +
                    $"Vulkan upload registration (published={publishedGeneration}, currentUpload={uploadGeneration}).",
                    EVulkanPresentNowFailureDisposition.RetryFrame);
                manifest.ResolveUnresolved(index);
            }
        }
    }
}
