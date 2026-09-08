namespace XREngine.Rendering.Vulkan;

internal sealed partial class FramePlan
{
    private readonly AdvancedVisibilityFamilyReservation[] _advancedBinReservations =
        new AdvancedVisibilityFamilyReservation[VulkanAdvancedVisibilityOutputCapacity.Maximum];
    private readonly VulkanPreparedStableBinStream?[] _advancedFamilyBins =
        new VulkanPreparedStableBinStream?[VulkanAdvancedVisibilityOutputCapacity.Maximum];
    private readonly AdvancedVisibilityFamilyReservation[] _advancedPlanLeaseReservations =
        new AdvancedVisibilityFamilyReservation[VulkanAdvancedVisibilityOutputCapacity.Maximum];
    private int _advancedPlanLeaseCount;
    private Action<AdvancedVisibilityFamilyReservation>? _releaseAdvancedPlanLease;

    internal void AttachAdvancedVisibilityPlanLeases(
        Func<AdvancedVisibilityFamilyReservation, bool> acquire,
        Action<AdvancedVisibilityFamilyReservation> release)
    {
        ArgumentNullException.ThrowIfNull(acquire);
        ArgumentNullException.ThrowIfNull(release);
        Span<AdvancedVisibilityFamilyReservation> frozenReservations =
            stackalloc AdvancedVisibilityFamilyReservation[VulkanAdvancedVisibilityOutputCapacity.Maximum];
        int count;
        ulong generation;
        lock (_leaseGate)
        {
            EnsureSealed();
            if (_advancedPlanLeaseCount != 0)
                return;
            generation = Generation;
            count = CollectAdvancedVisibilityReservations(frozenReservations);
        }

        int acquired = 0;
        try
        {
            for (; acquired < count; acquired++)
                if (!acquire(frozenReservations[acquired]))
                    throw new VulkanPlanPreconditionException(
                        "An Advanced output reservation retired before its sealed frame plan could retain it.");
        }
        catch
        {
            while (acquired-- > 0)
                release(frozenReservations[acquired]);
            throw;
        }

        bool releaseAcquired;
        lock (_leaseGate)
        {
            releaseAcquired = !IsSealed || Generation != generation || _advancedPlanLeaseCount != 0;
            if (!releaseAcquired)
            {
                frozenReservations[..count].CopyTo(_advancedPlanLeaseReservations);
                _advancedPlanLeaseCount = count;
                _releaseAdvancedPlanLease = release;
            }
        }
        if (!releaseAcquired)
            return;
        for (int index = 0; index < count; index++)
            release(frozenReservations[index]);
        throw new VulkanPlanPreconditionException(
            "The sealed Advanced frame plan changed before bank ownership was attached.");
    }

    private int CollectAdvancedVisibilityReservations(
        Span<AdvancedVisibilityFamilyReservation> destination)
    {
        int count = CollectAdvancedVisibilityReservations(_operations, destination, 0);
        count = CollectAdvancedVisibilityReservations(_dynamicOverlayOperations, destination, count);
        return CollectAdvancedVisibilityReservations(_textureUploadOperations, destination, count);
    }

    private static int CollectAdvancedVisibilityReservations(
        FrameOperationStream operations,
        Span<AdvancedVisibilityFamilyReservation> destination,
        int count)
    {
        for (int index = 0; index < operations.Count; index++)
        {
            if (operations.GetHeader(index).OpCode != EVulkanPrimaryPlanNodeKind.AdvancedVisibility)
                continue;
            AdvancedVisibilityFamilyReservation reservation =
                operations.GetAdvancedVisibility(index).Request.Reservation;
            if (!reservation.IsValid)
                continue;
            bool exists = false;
            for (int candidate = 0; candidate < count; candidate++)
                if (destination[candidate] == reservation)
                {
                    exists = true;
                    break;
                }
            if (exists)
                continue;
            if (count >= destination.Length)
                throw new VulkanPlanPreconditionException("The bounded Advanced output-bank plan lease capacity is exhausted.");
            destination[count++] = reservation;
        }
        return count;
    }

    private void DetachAdvancedVisibilityPlanLeases(
        Span<AdvancedVisibilityFamilyReservation> destination,
        out Action<AdvancedVisibilityFamilyReservation>? release,
        out int count)
    {
        release = _releaseAdvancedPlanLease;
        count = _advancedPlanLeaseCount;
        if (destination.Length < count)
            throw new ArgumentException("The destination cannot hold every sealed Advanced output-bank lease.", nameof(destination));
        _advancedPlanLeaseReservations.AsSpan(0, count).CopyTo(destination);
        _advancedPlanLeaseReservations.AsSpan(0, count).Clear();
        _advancedPlanLeaseCount = 0;
        _releaseAdvancedPlanLease = null;
    }

    internal int CopyAdvancedVisibilityPlanReservations(
        Span<AdvancedVisibilityFamilyReservation> destination)
    {
        lock (_leaseGate)
        {
            EnsureSealed();
            if (destination.Length < _advancedPlanLeaseCount)
                throw new ArgumentException("The destination cannot hold every sealed Advanced output-bank reservation.", nameof(destination));
            _advancedPlanLeaseReservations.AsSpan(0, _advancedPlanLeaseCount).CopyTo(destination);
            return _advancedPlanLeaseCount;
        }
    }

    internal bool HoldsAdvancedVisibilityPlanLease(
        in AdvancedVisibilityFamilyReservation reservation)
    {
        lock (_leaseGate)
        {
            EnsureSealed();
            for (int index = 0; index < _advancedPlanLeaseCount; index++)
                if (_advancedPlanLeaseReservations[index] == reservation)
                    return true;
            return false;
        }
    }

    internal void ProvisionAdvancedVisibilityFamily(int bankIndex)
    {
        // Output activation allocates only previously absent storage. Existing
        // sealed plans retain every workspace and its immutable contents.
        _operations.ProvisionAdvancedVisibilityFamily(bankIndex);
        if (Volatile.Read(ref _advancedFamilyBins[bankIndex]) is null)
            Volatile.Write(ref _advancedFamilyBins[bankIndex], new(
                VulkanMeshOperationRequestQueue.Capacity, VulkanMeshOperationRequestQueue.Capacity * 16));
    }

    internal VulkanPreparedStableBinStream GetAdvancedVisibilityFamilyBins(
        in AdvancedVisibilityFamilyReservation reservation, bool allowCreate = true)
    {
        if (!reservation.IsValid)
            throw new VulkanPlanPreconditionException("Advanced draw bins require an exact output reservation.");
        int empty = -1;
        for (int i = 0; i < _advancedBinReservations.Length; i++)
        {
            if (Volatile.Read(ref _advancedFamilyBins[i]) is null)
                continue;
            AdvancedVisibilityFamilyReservation current = _advancedBinReservations[i];
            if (!current.IsValid)
            {
                if (empty < 0) empty = i;
                continue;
            }
            if (current.OutputId != reservation.OutputId)
                continue;
            if (current != reservation)
                throw new VulkanPlanPreconditionException("An Advanced draw-bin owner changed within a sealed frame plan.");
            return _advancedFamilyBins[i]!;
        }
        if (!allowCreate)
            throw new VulkanPlanPreconditionException("The exact Advanced draw-bin family was not prepared.");
        if (empty < 0)
            throw new VulkanPlanPreconditionException("The bounded Advanced draw-bin family capacity is exhausted.");
        _advancedBinReservations[empty] = reservation;
        return _advancedFamilyBins[empty]!;
    }

    private void ResetAdvancedVisibilityFamilyBins()
    {
        Array.Clear(_advancedBinReservations);
        for (int i = 0; i < _advancedFamilyBins.Length; i++)
            Volatile.Read(ref _advancedFamilyBins[i])?.ThawForReuse();
    }
}
