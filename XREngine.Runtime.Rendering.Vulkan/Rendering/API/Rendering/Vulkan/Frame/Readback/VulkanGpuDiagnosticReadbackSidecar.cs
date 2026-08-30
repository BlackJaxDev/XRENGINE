using System.Threading;
using Silk.NET.Vulkan;
using XREngine.Rendering.Diagnostics;

namespace XREngine.Rendering.Vulkan;

/// <summary>Schedules decoding after the copied staging slice has retired.</summary>
internal delegate void VulkanGpuDiagnosticDecodeScheduler(
    ulong frameIdentity,
    in GpuDiagnosticReadbackPlanNode node);

internal delegate void VulkanGpuDiagnosticPrimaryDecodeScheduler(
    in VulkanGpuDiagnosticReadbackReservation reservation,
    in VulkanFrameDataSlice slice,
    ulong frameIdentity,
    in GpuDiagnosticReadbackPlanNode node);

/// <summary>
/// Fixed-capacity reservation ring for delayed diagnostic staging copies. This
/// class owns no producer resources and intentionally has no wait operation:
/// command recording supplies copies, while retirement polls completion.
/// </summary>
internal sealed class VulkanGpuDiagnosticReadbackSidecar
{
    private readonly VulkanReadbackOutputResourceService _readbackResources;
    private readonly VulkanGpuDiagnosticReadbackSidecarSlot[] _slots;
    private int _cursor;
    private long _droppedReservationCount;
    private long _completedReservationCount;

    internal VulkanGpuDiagnosticReadbackSidecar(
        VulkanReadbackOutputResourceService readbackResources,
        int capacity)
    {
        _readbackResources = readbackResources ?? throw new ArgumentNullException(nameof(readbackResources));
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        _slots = new VulkanGpuDiagnosticReadbackSidecarSlot[capacity];
        for (int index = 0; index < _slots.Length; ++index)
            _slots[index] = new VulkanGpuDiagnosticReadbackSidecarSlot();
    }

    internal int Capacity => _slots.Length;
    internal long DroppedReservationCount => Interlocked.Read(ref _droppedReservationCount);
    internal long CompletedReservationCount => Interlocked.Read(ref _completedReservationCount);

    /// <summary>
    /// Acquires a slice from the dedicated host-visible diagnostic arena. The
    /// arena is fixed to the sidecar's ring-slot count and is never touched
    /// until an instrumented plan has already reserved a slot.
    /// </summary>
    internal bool TryAcquireStagingSlice(
        int ringSlot,
        uint byteCount,
        out VulkanFrameDataSlice slice)
    {
        slice = default;
        return ringSlot >= 0 && ringSlot < _slots.Length &&
               _readbackResources.TryAcquireGpuStatsSlice(ringSlot, byteCount, out slice);
    }

    internal bool TryPrepareStagingSlice(in VulkanFrameDataSlice slice)
        => _readbackResources.TryPrepareGpuStatsSlice(slice);

    internal void MarkStagingSliceSubmitted(in VulkanFrameDataSlice slice)
        => _readbackResources.MarkGpuStatsSliceSubmitted(slice);

    internal void CancelStagingSliceSubmission(in VulkanFrameDataSlice slice)
        => _readbackResources.CancelGpuStatsSliceSubmission(slice);

    internal bool TryCompleteStagingSlice(in VulkanFrameDataSlice slice)
        => _readbackResources.TryCompleteGpuStatsSlice(slice);

    internal bool TryBeginStagingRead(
        in VulkanFrameDataSlice slice,
        out VulkanFrameDataReadScope scope)
        => _readbackResources.TryBeginGpuStatsRead(slice, out scope);

    /// <summary>
    /// Attempts one non-blocking reservation. Saturation is diagnostic-only: it
    /// drops the request and cannot change rendering or strategy selection.
    /// </summary>
    internal bool TryReserve(
        in GpuDiagnosticReadbackPlanNode node,
        ulong frameIdentity,
        int ringSlot,
        EVulkanGpuDiagnosticReadbackPurpose purpose,
        out VulkanGpuDiagnosticReadbackReservation reservation)
    {
        reservation = default;
        if (!IsEligible(in node, purpose) || (uint)ringSlot >= (uint)_slots.Length)
            return false;

        VulkanGpuDiagnosticReadbackSidecarSlot slot = _slots[ringSlot];
        if (Interlocked.CompareExchange(
                ref slot.State,
                (int)EVulkanGpuDiagnosticReadbackSidecarSlotState.Reserved,
                (int)EVulkanGpuDiagnosticReadbackSidecarSlotState.Idle) !=
            (int)EVulkanGpuDiagnosticReadbackSidecarSlotState.Idle)
        {
            return false;
        }

        slot.FrameIdentity = frameIdentity;
        slot.Node = node;
        _cursor = (ringSlot + 1) % _slots.Length;
        reservation = new VulkanGpuDiagnosticReadbackReservation(ringSlot, frameIdentity);
        return true;
    }

    internal bool TryReserveNext(
        in GpuDiagnosticReadbackPlanNode node,
        ulong frameIdentity,
        EVulkanGpuDiagnosticReadbackPurpose purpose,
        out VulkanGpuDiagnosticReadbackReservation reservation)
    {
        reservation = default;
        if (!IsEligible(in node, purpose))
            return false;

        int start = Math.Abs(Interlocked.Increment(ref _cursor));
        for (int attempt = 0; attempt < _slots.Length; ++attempt)
        {
            int index = (start + attempt) % _slots.Length;
            if (TryReserve(in node, frameIdentity, index, purpose, out reservation))
                return true;
        }

        Interlocked.Increment(ref _droppedReservationCount);
        return false;
    }

    private static bool IsEligible(
        in GpuDiagnosticReadbackPlanNode node,
        EVulkanGpuDiagnosticReadbackPurpose purpose)
        => purpose switch
        {
            EVulkanGpuDiagnosticReadbackPurpose.Instrumented => node.IsInstrumentedPass,
            EVulkanGpuDiagnosticReadbackPurpose.MeshletZeroReadbackEvidence =>
                node.Strategy == global::XREngine.Data.Rendering.EMeshSubmissionStrategy.GpuMeshletZeroReadback &&
                node.Decoder is EGpuDiagnosticReadbackDecoder.SubmissionValidation or
                    EGpuDiagnosticReadbackDecoder.MeshletVisibility,
            _ => false,
        };

    /// <summary>
    /// Attaches an already-recorded copy to the primary command buffer that
    /// produced it. The staging arena is prepared here, but is only marked
    /// submitted once that exact primary submit is accepted.
    /// </summary>
    internal bool TryAttachPrimaryCopy(
        in VulkanGpuDiagnosticReadbackReservation reservation,
        CommandBuffer primaryCommandBuffer,
        in VulkanFrameDataSlice slice)
    {
        if (!slice.IsValid || !TryPrepareStagingSlice(slice) ||
            (uint)reservation.SlotIndex >= (uint)_slots.Length)
            return false;

        VulkanGpuDiagnosticReadbackSidecarSlot slot = _slots[reservation.SlotIndex];
        if (slot.FrameIdentity != reservation.FrameIdentity ||
            Volatile.Read(ref slot.State) !=
                (int)EVulkanGpuDiagnosticReadbackSidecarSlotState.Reserved)
        {
            CancelStagingSliceSubmission(slice);
            return false;
        }

        slot.Slice = slice;
        slot.PrimaryCommandBuffer = primaryCommandBuffer;
        return true;
    }

    internal void MarkPrimarySubmissionAccepted(
        CommandBuffer primaryCommandBuffer,
        ulong completionValue)
    {
        if (primaryCommandBuffer.Handle == 0 || completionValue == 0UL)
            return;

        for (int index = 0; index < _slots.Length; ++index)
        {
            VulkanGpuDiagnosticReadbackSidecarSlot slot = _slots[index];
            if (slot.PrimaryCommandBuffer.Handle != primaryCommandBuffer.Handle ||
                !slot.Slice.IsValid ||
                Interlocked.CompareExchange(
                    ref slot.State,
                    (int)EVulkanGpuDiagnosticReadbackSidecarSlotState.Submitted,
                    (int)EVulkanGpuDiagnosticReadbackSidecarSlotState.Reserved) !=
                (int)EVulkanGpuDiagnosticReadbackSidecarSlotState.Reserved)
            {
                continue;
            }

            slot.CompletionValue = completionValue;
            MarkStagingSliceSubmitted(slot.Slice);
        }
    }

    internal void CancelPrimarySubmission(CommandBuffer primaryCommandBuffer)
    {
        if (primaryCommandBuffer.Handle == 0)
            return;

        for (int index = 0; index < _slots.Length; ++index)
        {
            VulkanGpuDiagnosticReadbackSidecarSlot slot = _slots[index];
            if (slot.PrimaryCommandBuffer.Handle != primaryCommandBuffer.Handle)
                continue;

            if (slot.Slice.IsValid)
                CancelStagingSliceSubmission(slot.Slice);
            Cancel(new VulkanGpuDiagnosticReadbackReservation(index, slot.FrameIdentity));
            slot.Slice = default;
            slot.PrimaryCommandBuffer = default;
            slot.CompletionValue = 0UL;
        }
    }

    internal void PollPrimaryCompleted(
        Func<ulong, bool> isCompletionSignalled,
        VulkanGpuDiagnosticPrimaryDecodeScheduler scheduleDecode)
    {
        ArgumentNullException.ThrowIfNull(isCompletionSignalled);
        ArgumentNullException.ThrowIfNull(scheduleDecode);

        for (int index = 0; index < _slots.Length; ++index)
        {
            VulkanGpuDiagnosticReadbackSidecarSlot slot = _slots[index];
            if (Volatile.Read(ref slot.State) !=
                    (int)EVulkanGpuDiagnosticReadbackSidecarSlotState.Submitted ||
                slot.CompletionValue == 0UL || !slot.Slice.IsValid ||
                !isCompletionSignalled(slot.CompletionValue) ||
                Interlocked.CompareExchange(
                    ref slot.State,
                    (int)EVulkanGpuDiagnosticReadbackSidecarSlotState.Decoding,
                    (int)EVulkanGpuDiagnosticReadbackSidecarSlotState.Submitted) !=
                (int)EVulkanGpuDiagnosticReadbackSidecarSlotState.Submitted)
            {
                continue;
            }

            VulkanGpuDiagnosticReadbackReservation reservation = new(index, slot.FrameIdentity);
            try
            {
                scheduleDecode(in reservation, in slot.Slice, slot.FrameIdentity, in slot.Node);
                Interlocked.Increment(ref _completedReservationCount);
            }
            finally
            {
                slot.Node = default;
                slot.FrameIdentity = 0UL;
                slot.Slice = default;
                slot.PrimaryCommandBuffer = default;
                slot.CompletionValue = 0UL;
                Volatile.Write(ref slot.State, (int)EVulkanGpuDiagnosticReadbackSidecarSlotState.Idle);
            }
        }
    }

    /// <summary>Marks a reserved staging copy submitted without observing its fence.</summary>
    internal bool TryMarkSubmitted(in VulkanGpuDiagnosticReadbackReservation reservation)
        => TryTransition(
            reservation,
            EVulkanGpuDiagnosticReadbackSidecarSlotState.Reserved,
            EVulkanGpuDiagnosticReadbackSidecarSlotState.Submitted);

    internal bool TryComplete(in VulkanGpuDiagnosticReadbackReservation reservation)
    {
        if (!TryTransition(
                reservation,
                EVulkanGpuDiagnosticReadbackSidecarSlotState.Submitted,
                EVulkanGpuDiagnosticReadbackSidecarSlotState.Decoding))
        {
            return false;
        }

        VulkanGpuDiagnosticReadbackSidecarSlot slot = _slots[reservation.SlotIndex];
        slot.Node = default;
        slot.FrameIdentity = 0UL;
        Volatile.Write(ref slot.State, (int)EVulkanGpuDiagnosticReadbackSidecarSlotState.Idle);
        Interlocked.Increment(ref _completedReservationCount);
        return true;
    }

    internal void Cancel(in VulkanGpuDiagnosticReadbackReservation reservation)
    {
        if ((uint)reservation.SlotIndex >= (uint)_slots.Length)
            return;

        VulkanGpuDiagnosticReadbackSidecarSlot slot = _slots[reservation.SlotIndex];
        if (slot.FrameIdentity != reservation.FrameIdentity)
            return;

        int state = Volatile.Read(ref slot.State);
        if (state is not (int)EVulkanGpuDiagnosticReadbackSidecarSlotState.Reserved and
            not (int)EVulkanGpuDiagnosticReadbackSidecarSlotState.Submitted)
        {
            return;
        }

        slot.Node = default;
        slot.FrameIdentity = 0UL;
        Volatile.Write(ref slot.State, (int)EVulkanGpuDiagnosticReadbackSidecarSlotState.Idle);
    }

    /// <summary>
    /// Polls only slots whose completion authority has already signalled. The
    /// caller provides a non-blocking status probe and schedules decoding on the
    /// telemetry domain; this method never waits or spins.
    /// </summary>
    internal void PollCompleted(
        Func<int, bool> isCompletionSignalled,
        VulkanGpuDiagnosticDecodeScheduler scheduleDecode)
    {
        ArgumentNullException.ThrowIfNull(isCompletionSignalled);
        ArgumentNullException.ThrowIfNull(scheduleDecode);

        for (int index = 0; index < _slots.Length; ++index)
        {
            VulkanGpuDiagnosticReadbackSidecarSlot slot = _slots[index];
            if (Volatile.Read(ref slot.State) != (int)EVulkanGpuDiagnosticReadbackSidecarSlotState.Submitted ||
                !isCompletionSignalled(index) ||
                Interlocked.CompareExchange(
                    ref slot.State,
                    (int)EVulkanGpuDiagnosticReadbackSidecarSlotState.Decoding,
                    (int)EVulkanGpuDiagnosticReadbackSidecarSlotState.Submitted) !=
                (int)EVulkanGpuDiagnosticReadbackSidecarSlotState.Submitted)
            {
                continue;
            }

            try
            {
                scheduleDecode(slot.FrameIdentity, in slot.Node);
                Interlocked.Increment(ref _completedReservationCount);
            }
            finally
            {
                slot.Node = default;
                slot.FrameIdentity = 0UL;
                Volatile.Write(ref slot.State, (int)EVulkanGpuDiagnosticReadbackSidecarSlotState.Idle);
            }
        }
    }

    private bool TryTransition(
        in VulkanGpuDiagnosticReadbackReservation reservation,
        EVulkanGpuDiagnosticReadbackSidecarSlotState expected,
        EVulkanGpuDiagnosticReadbackSidecarSlotState next)
    {
        if ((uint)reservation.SlotIndex >= (uint)_slots.Length)
            return false;

        VulkanGpuDiagnosticReadbackSidecarSlot slot = _slots[reservation.SlotIndex];
        if (slot.FrameIdentity != reservation.FrameIdentity)
            return false;

        return Interlocked.CompareExchange(ref slot.State, (int)next, (int)expected) == (int)expected;
    }
}
