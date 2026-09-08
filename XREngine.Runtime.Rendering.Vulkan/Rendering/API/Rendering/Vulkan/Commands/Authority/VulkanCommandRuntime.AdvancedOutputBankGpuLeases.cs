using Silk.NET.Vulkan;
using System.Collections.Generic;

namespace XREngine.Rendering.Vulkan;

/// <summary>Recorded-command ownership and completion-watermark handoff for Advanced output banks.</summary>
internal sealed partial class VulkanCommandRuntime
{
    private const int AdvancedVisibilityRecordedLeaseCapacity = 128;
    private readonly object _advancedVisibilityRecordedLeaseGate = new();
    private readonly Dictionary<ulong, AdvancedVisibilityRecordedBankLease> _advancedVisibilityRecordedBankLeases =
        new(AdvancedVisibilityRecordedLeaseCapacity);
    private readonly AdvancedVisibilityRecordedBankLease[] _advancedVisibilityRecordedLeasePool =
        CreateAdvancedVisibilityRecordedLeasePool();

    internal void RegisterRecordedAdvancedVisibilityBanks(CommandBuffer commandBuffer, FramePlan framePlan)
    {
        if (commandBuffer.Handle == 0)
            return;
        Span<AdvancedVisibilityFamilyReservation> reservations = stackalloc AdvancedVisibilityFamilyReservation[VulkanAdvancedVisibilityOutputCapacity.Maximum];
        int count = framePlan.CopyAdvancedVisibilityPlanReservations(reservations);
        if (count == 0)
            return;

        ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
        lock (_advancedVisibilityRecordedLeaseGate)
        {
            if (_advancedVisibilityRecordedBankLeases.TryGetValue(commandBufferHandle, out AdvancedVisibilityRecordedBankLease? existing))
            {
                if (!existing.Matches(reservations[..count]))
                    throw new VulkanPlanPreconditionException("A recorded command buffer cannot change its Advanced output-bank ownership without first releasing the prior binding.");
                return;
            }

            AdvancedVisibilityRecordedBankLease? lease = FindFreeAdvancedVisibilityRecordedLeaseNoLock();
            if (lease is null)
                throw new VulkanPlanPreconditionException("The bounded Advanced recorded-command lease ledger is exhausted.");
            if (!TryAcquireRecordedAdvancedVisibilityBanks(reservations[..count]))
                throw new VulkanPlanPreconditionException("An Advanced output bank retired before recorded command ownership could be retained.");
            lease.Bind(reservations[..count]);
            _advancedVisibilityRecordedBankLeases.Add(commandBufferHandle, lease);
        }
    }

    internal void ReleaseRecordedAdvancedVisibilityBanks(CommandBuffer commandBuffer)
    {
        if (commandBuffer.Handle == 0)
            return;
        AdvancedVisibilityRecordedBankLease? lease;
        lock (_advancedVisibilityRecordedLeaseGate)
        {
            ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
            if (!_advancedVisibilityRecordedBankLeases.Remove(commandBufferHandle, out lease))
                return;
        }
        ReleaseRecordedAdvancedVisibilityBanks(lease);
        lock (_advancedVisibilityRecordedLeaseGate)
            lease.Clear();
        TryFinalizeRetiringAdvancedVisibilityBanks();
    }

    /// <summary>
    /// Called only after native queue acceptance, while the lifetime publisher owns the exact command vector.
    /// It never takes the reservation gate because tracked submission already holds the lifetime lock.
    /// </summary>
    private unsafe void RecordAdvancedVisibilitySubmissionWatermarksNoLock(
        SubmitInfo submitInfo,
        EVulkanLifetimeQueueDomain domain,
        ulong sequence)
    {
        lock (_advancedVisibilityRecordedLeaseGate)
        {
            for (int commandIndex = 0; commandIndex < submitInfo.CommandBufferCount; commandIndex++)
            {
                ulong handle = unchecked((ulong)submitInfo.PCommandBuffers[commandIndex].Handle);
                if (handle == 0 || !_advancedVisibilityRecordedBankLeases.TryGetValue(handle, out AdvancedVisibilityRecordedBankLease? lease))
                    continue;
                for (int reservationIndex = 0; reservationIndex < lease.Count; reservationIndex++)
                    RecordAdvancedVisibilitySubmissionWatermarkNoLock(lease.Reservations[reservationIndex], domain, sequence);
            }
        }
    }

    private bool TryAcquireRecordedAdvancedVisibilityBanks(ReadOnlySpan<AdvancedVisibilityFamilyReservation> reservations)
    {
        int acquired = 0;
        lock (_advancedVisibilityReservationGate)
        {
            for (; acquired < reservations.Length; acquired++)
            {
                if (!TryGetConsumableAdvancedVisibilityBankNoLock(in reservations[acquired], out AdvancedVisibilityOutputBank? bank))
                    break;
                bank.RecordedCommandBufferLeaseCount++;
            }
            if (acquired == reservations.Length)
                return true;
            while (acquired-- > 0)
                _advancedVisibilityOutputBanks[checked((int)reservations[acquired].ReservationId - 1)].RecordedCommandBufferLeaseCount--;
        }
        return false;
    }

    private void ReleaseRecordedAdvancedVisibilityBanks(AdvancedVisibilityRecordedBankLease lease)
    {
        lock (_advancedVisibilityReservationGate)
        {
            for (int index = 0; index < lease.Count; index++)
            {
                AdvancedVisibilityFamilyReservation reservation = lease.Reservations[index];
                if (!TryGetExactAdvancedVisibilityBankNoLock(in reservation, out AdvancedVisibilityOutputBank? bank) ||
                    bank.RecordedCommandBufferLeaseCount <= 0)
                    throw new InvalidOperationException("Advanced output-bank recorded command lease underflow or stale token.");
                bank.RecordedCommandBufferLeaseCount--;
            }
        }
    }

    private void RecordAdvancedVisibilitySubmissionWatermarkNoLock(
        in AdvancedVisibilityFamilyReservation reservation,
        EVulkanLifetimeQueueDomain domain,
        ulong sequence)
    {
        if (!TryGetExactAdvancedVisibilityBankNoLock(in reservation, out AdvancedVisibilityOutputBank? bank))
            return;
        if (domain == EVulkanLifetimeQueueDomain.Graphics)
            RecordAdvancedVisibilitySubmissionWatermark(ref bank.GraphicsCompletionWatermark, bank, sequence);
        else if (domain == EVulkanLifetimeQueueDomain.Transfer)
            RecordAdvancedVisibilitySubmissionWatermark(ref bank.TransferCompletionWatermark, bank, sequence);
        else
            RecordAdvancedVisibilitySubmissionWatermark(ref bank.OtherCompletionWatermark, bank, sequence);
    }

    private static void RecordAdvancedVisibilitySubmissionWatermark(
        ref long watermark,
        AdvancedVisibilityOutputBank bank,
        ulong sequence)
    {
        long observed = Volatile.Read(ref watermark);
        while (unchecked((ulong)observed) < sequence)
        {
            if (Interlocked.CompareExchange(ref watermark, unchecked((long)sequence), observed) == observed)
            {
                if (observed == 0)
                    Interlocked.Increment(ref bank.PendingQueueDomainCount);
                break;
            }
            observed = Volatile.Read(ref watermark);
        }
    }

    private void TryFinalizeRetiringAdvancedVisibilityBanks()
    {
        VulkanResourceLifetimeTracker tracker = ResourceRuntime.Lifetime.Tracker;
        ulong graphics;
        ulong transfer;
        ulong other;
        lock (tracker.SyncRoot)
        {
            if (tracker.DeviceLost)
                return;
            graphics = tracker.CompletedGraphicsSequence;
            transfer = tracker.CompletedTransferSequence;
            other = tracker.CompletedOtherSequence;
        }
        lock (_advancedVisibilityReservationGate)
        {
            foreach (AdvancedVisibilityOutputBank bank in _advancedVisibilityOutputBanks)
                TryFinalizeRetiringAdvancedVisibilityBankNoLock(bank, graphics, transfer, other);
        }
    }

    private static AdvancedVisibilityRecordedBankLease[] CreateAdvancedVisibilityRecordedLeasePool()
    {
        AdvancedVisibilityRecordedBankLease[] leases =
            new AdvancedVisibilityRecordedBankLease[AdvancedVisibilityRecordedLeaseCapacity];
        for (int index = 0; index < leases.Length; index++)
            leases[index] = new();
        return leases;
    }

    private AdvancedVisibilityRecordedBankLease? FindFreeAdvancedVisibilityRecordedLeaseNoLock()
    {
        foreach (AdvancedVisibilityRecordedBankLease lease in _advancedVisibilityRecordedLeasePool)
            if (!lease.IsInUse)
                return lease;
        return null;
    }

    private void CaptureAdvancedVisibilityRecordedLeaseOccupancy(
        out int capacity,
        out int count)
    {
        lock (_advancedVisibilityRecordedLeaseGate)
        {
            capacity = _advancedVisibilityRecordedLeasePool.Length;
            count = _advancedVisibilityRecordedBankLeases.Count;
        }
    }

    private sealed class AdvancedVisibilityRecordedBankLease
    {
        internal readonly AdvancedVisibilityFamilyReservation[] Reservations = new AdvancedVisibilityFamilyReservation[VulkanAdvancedVisibilityOutputCapacity.Maximum];
        internal int Count { get; private set; }
        internal bool IsInUse { get; private set; }

        internal void Bind(ReadOnlySpan<AdvancedVisibilityFamilyReservation> reservations)
        {
            reservations.CopyTo(Reservations);
            Count = reservations.Length;
            IsInUse = true;
        }

        internal void Clear()
        {
            Reservations.AsSpan(0, Count).Clear();
            Count = 0;
            IsInUse = false;
        }

        internal bool Matches(ReadOnlySpan<AdvancedVisibilityFamilyReservation> reservations)
            => reservations.SequenceEqual(Reservations.AsSpan(0, Count));
    }
}
