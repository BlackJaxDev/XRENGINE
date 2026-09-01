using System.Diagnostics;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns presentation-engine release proof independently of graphics submission
/// completion. Each swapchain generation has a fixed fence pool. Ordinary work
/// only polls; capacity is reserved before acquiring an image.
/// </summary>
internal sealed unsafe class VulkanWsiPresentCompletion
{
    private readonly Vk _api;
    private readonly Device _device;
    private readonly Fence[] _fences;
    private readonly EVulkanWsiPresentState[] _states;
    private readonly ulong[] _serials;
    private readonly bool[] _needsReset;
    private bool _sealed;
    private bool _destroyed;
    private ulong _nextSerial;

    internal VulkanWsiPresentCompletion(Vk api, Device device, int imageCount, bool maintenanceEnabled)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(imageCount);
        _api = api;
        _device = device;
        MaintenanceEnabled = maintenanceEnabled;
        int capacity = maintenanceEnabled ? imageCount : 1;
        _fences = new Fence[capacity];
        _states = new EVulkanWsiPresentState[capacity];
        _serials = new ulong[capacity];
        _needsReset = new bool[capacity];
        if (!maintenanceEnabled)
            return;

        FenceCreateInfo info = new() { SType = StructureType.FenceCreateInfo };
        try
        {
            for (int index = 0; index < capacity; index++)
                Check(api.CreateFence(device, in info, null, out _fences[index]), "create WSI presentation fence");
        }
        catch
        {
            for (int index = 0; index < capacity; index++)
                if (_fences[index].Handle != 0)
                    api.DestroyFence(device, _fences[index], null);
            throw;
        }
    }

    internal bool MaintenanceEnabled { get; }
    internal bool HasUnprovenLegacyPresent { get; private set; }
    internal long SubmittedCount { get; private set; }
    internal long CompletedCount { get; private set; }
    internal long CapacityDeferrals { get; private set; }

    internal bool TryReserve(out VulkanWsiPresentReservation reservation)
    {
        reservation = default;
        if (_sealed || _destroyed)
            return false;
        Poll();
        for (int index = 0; index < _states.Length; index++)
        {
            if (_states[index] != EVulkanWsiPresentState.Free)
                continue;
            if (_needsReset[index])
            {
                Check(_api.ResetFences(_device, 1, in _fences[index]), "reset completed WSI presentation fence");
                _needsReset[index] = false;
            }
            ulong serial = checked(++_nextSerial);
            _serials[index] = serial;
            _states[index] = EVulkanWsiPresentState.Reserved;
            reservation = new(this, _fences[index], index, serial);
            return true;
        }
        CapacityDeferrals++;
        return false;
    }

    /// <summary>Must run immediately after the native call, before fallible diagnostics.</summary>
    internal void Commit(in VulkanWsiPresentReservation reservation, bool dispatched, Result result)
    {
        Validate(reservation);
        if (_states[reservation.Slot] != EVulkanWsiPresentState.Reserved)
            throw new InvalidOperationException("A WSI presentation reservation was committed more than once.");
        if (!dispatched || result is Result.ErrorOutOfHostMemory or Result.ErrorOutOfDeviceMemory)
        {
            _states[reservation.Slot] = EVulkanWsiPresentState.Free;
            return;
        }
        bool enqueued = VulkanWsiPresentResult.EnqueuesPresentationRelease(result);
        if (!MaintenanceEnabled)
        {
            HasUnprovenLegacyPresent = true;
            _states[reservation.Slot] = enqueued
                ? EVulkanWsiPresentState.Free : EVulkanWsiPresentState.Quarantined;
        }
        else
            _states[reservation.Slot] = enqueued
                ? EVulkanWsiPresentState.Enqueued : EVulkanWsiPresentState.Quarantined;
        SubmittedCount++;
    }

    /// <summary>
    /// Verifies that a reservation is still dispatchable before native presentation.
    /// A quarantined or committed reservation may retain native image ownership and
    /// must never be retried.
    /// </summary>
    internal void RequireReservedForDispatch(in VulkanWsiPresentReservation reservation)
    {
        Validate(reservation);
        if (_states[reservation.Slot] != EVulkanWsiPresentState.Reserved)
            throw new InvalidOperationException("A WSI presentation reservation is not dispatchable.");
    }

    internal bool IsQuarantined(in VulkanWsiPresentReservation reservation)
    {
        Validate(reservation);
        return _states[reservation.Slot] == EVulkanWsiPresentState.Quarantined;
    }

    /// <summary>An exception with unknown native dispatch must retain ownership.</summary>
    internal void Quarantine(in VulkanWsiPresentReservation reservation)
    {
        Validate(reservation);
        if (_states[reservation.Slot] == EVulkanWsiPresentState.Reserved)
            _states[reservation.Slot] = EVulkanWsiPresentState.Quarantined;
    }

    internal void Cancel(in VulkanWsiPresentReservation reservation)
    {
        if (reservation.Owner is null)
            return;
        Validate(reservation);
        if (_states[reservation.Slot] == EVulkanWsiPresentState.Reserved)
            _states[reservation.Slot] = EVulkanWsiPresentState.Free;
    }

    internal void Seal() => _sealed = true;

    internal bool PollRetirement()
    {
        if (_destroyed)
            return true;
        Poll();
        if (HasUnprovenLegacyPresent)
            return false;
        for (int index = 0; index < _states.Length; index++)
            if (_states[index] != EVulkanWsiPresentState.Free)
                return false;
        return _sealed;
    }

    /// <summary>Explicit shutdown-only wait, never used for resize or ordinary retirement.</summary>
    internal void WaitForShutdown()
    {
        Seal();
        if (HasUnprovenLegacyPresent)
            throw new NotSupportedException("WSI teardown lacks presentation release proof: VK_EXT_swapchain_maintenance1 is required. Native ownership is retained.");
        long start = Stopwatch.GetTimestamp();
        for (int index = 0; index < _states.Length; index++)
        {
            if (_states[index] == EVulkanWsiPresentState.Reserved)
                _states[index] = EVulkanWsiPresentState.Free;
            if (_states[index] == EVulkanWsiPresentState.Quarantined)
                throw new InvalidOperationException("WSI teardown has an indeterminate presentation; native ownership is retained.");
            if (_states[index] != EVulkanWsiPresentState.Enqueued)
                continue;
            double remaining = 5.0 - Stopwatch.GetElapsedTime(start).TotalSeconds;
            if (remaining <= 0)
                throw new TimeoutException("WSI presentation release did not complete within the shutdown budget.");
            Check(_api.WaitForFences(_device, 1, in _fences[index], true,
                (ulong)(remaining * 1_000_000_000)), "wait for WSI presentation release at shutdown");
            MarkComplete(index);
        }
    }

    internal void Destroy(bool deviceLost = false)
    {
        if (_destroyed)
            return;
        if (!deviceLost && !PollRetirement())
            throw new InvalidOperationException("Cannot destroy WSI fences before presentation release proof.");
        for (int index = 0; index < _fences.Length; index++)
            if (_fences[index].Handle != 0)
                _api.DestroyFence(_device, _fences[index], null);
        _destroyed = true;
    }

    private void Poll()
    {
        for (int index = 0; index < _states.Length; index++)
        {
            if (_states[index] != EVulkanWsiPresentState.Enqueued)
                continue;
            Result result = _api.GetFenceStatus(_device, _fences[index]);
            if (result == Result.Success)
                MarkComplete(index);
            else if (result != Result.NotReady)
            {
                _states[index] = EVulkanWsiPresentState.Quarantined;
                Check(result, "poll WSI presentation release");
            }
        }
    }

    private void MarkComplete(int index)
    {
        _states[index] = EVulkanWsiPresentState.Free;
        _needsReset[index] = true;
        CompletedCount++;
    }

    private void Validate(in VulkanWsiPresentReservation reservation)
    {
        if (_destroyed || !ReferenceEquals(reservation.Owner, this) ||
            (uint)reservation.Slot >= (uint)_states.Length ||
            reservation.Serial == 0 || _serials[reservation.Slot] != reservation.Serial)
            throw new InvalidOperationException("Stale or foreign WSI presentation reservation.");
    }

    private static void Check(Result result, string operation)
    {
        if (result != Result.Success)
            throw new InvalidOperationException($"Failed to {operation}: {result}.");
    }
}
