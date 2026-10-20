using System.Runtime.InteropServices;

namespace XREngine.Components;

public sealed partial class PhysicsChainWorld
{
    // Keep clock data separate from copied runtime-slot records.
    private readonly List<PhysicsChainSimulationClock> _clocks = [];

    private bool TryGetClockSlot(PhysicsChainRuntimeHandle handle, PhysicsChainComponent component, out int slotIndex)
    {
        slotIndex = handle.Slot;
        if (!handle.IsValid
            || (uint)slotIndex >= (uint)_slots.Count
            || (uint)slotIndex >= (uint)_clocks.Count)
            return false;

        RuntimeSlot slot = _slots[slotIndex];
        return slot.Generation == handle.Generation && ReferenceEquals(slot.Component, component);
    }

    internal void ResetSimulationClock(PhysicsChainRuntimeHandle handle, PhysicsChainComponent component)
    {
        if (!TryGetClockSlot(handle, component, out int slotIndex))
            return;

        CollectionsMarshal.AsSpan(_clocks)[slotIndex].Reset();
    }

    internal void AdvanceFixedRenderClock(PhysicsChainRuntimeHandle handle, PhysicsChainComponent component, long deltaTicks)
    {
        if (!TryGetClockSlot(handle, component, out int slotIndex))
            return;

        CollectionsMarshal.AsSpan(_clocks)[slotIndex].FixedRenderAccumulatedTicks += Math.Max(0L, deltaTicks);
    }

    internal void ClearFixedRenderClock(PhysicsChainRuntimeHandle handle, PhysicsChainComponent component)
    {
        if (!TryGetClockSlot(handle, component, out int slotIndex))
            return;

        CollectionsMarshal.AsSpan(_clocks)[slotIndex].FixedRenderAccumulatedTicks = 0L;
    }

    internal float CaptureCadenceProgress(
        PhysicsChainRuntimeHandle handle,
        PhysicsChainComponent component,
        float rate,
        float fallback)
    {
        if (!TryGetClockSlot(handle, component, out int slotIndex))
            return fallback;

        ref PhysicsChainSimulationClock clock = ref CollectionsMarshal.AsSpan(_clocks)[slotIndex];
        return PhysicsChainComponent.ComputeCadenceProgress(clock.ElapsedSeconds, rate, clock.CadenceProgress);
    }

    internal void SetCadenceProgress(
        PhysicsChainRuntimeHandle handle,
        PhysicsChainComponent component,
        float progress,
        float rate)
    {
        if (!TryGetClockSlot(handle, component, out int slotIndex))
            return;

        CollectionsMarshal.AsSpan(_clocks)[slotIndex].SetCadenceProgress(progress, rate);
    }

    internal bool TryResolveSimulationLoop(
        PhysicsChainRuntimeHandle handle,
        PhysicsChainComponent component,
        float deltaSeconds,
        float rate,
        int maximumCatchUpSteps,
        out int loop,
        out float stepDelta)
    {
        loop = 0;
        stepDelta = 0.0f;
        if (!TryGetClockSlot(handle, component, out int slotIndex))
            return false;

        CollectionsMarshal.AsSpan(_clocks)[slotIndex].ResolveLoop(
            deltaSeconds, rate, maximumCatchUpSteps, out loop, out stepDelta);
        return true;
    }

    internal bool TryGetSimulationClock(
        PhysicsChainRuntimeHandle handle,
        PhysicsChainComponent component,
        out PhysicsChainSimulationClock clock)
    {
        if (TryGetClockSlot(handle, component, out int slotIndex))
        {
            clock = _clocks[slotIndex];
            return true;
        }

        clock = default;
        return false;
    }
}
