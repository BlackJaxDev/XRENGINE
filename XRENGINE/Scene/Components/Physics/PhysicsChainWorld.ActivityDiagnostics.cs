namespace XREngine.Components;

public sealed partial class PhysicsChainWorld
{
    public const int MaximumSelectedActivityDiagnostics = 16;

    private readonly Lock _activityDiagnosticSelectionLock = new();
    private readonly PhysicsChainRuntimeHandle[] _activityDiagnosticSelection =
        new PhysicsChainRuntimeHandle[MaximumSelectedActivityDiagnostics];
    private readonly PhysicsChainSelectedActivityDiagnostic[] _pendingSelectedActivityDiagnostics =
        new PhysicsChainSelectedActivityDiagnostic[MaximumSelectedActivityDiagnostics];
    private readonly PhysicsChainSelectedActivityDiagnostic[] _publishedSelectedActivityDiagnostics =
        new PhysicsChainSelectedActivityDiagnostic[MaximumSelectedActivityDiagnostics];
    private int _activityDiagnosticSelectionCount;
    private int _pendingSelectedActivityDiagnosticCount;
    private int _publishedSelectedActivityDiagnosticCount;
    private PhysicsChainActivityCounters _pendingActivityCounters = new(-1L, 0, 0, 0, 0UL);
    private PhysicsChainActivityCounters _publishedActivityCounters = new(-1L, 0, 0, 0, 0UL);

    /// <summary>Returns counters sampled at the end of the preceding world frame.</summary>
    public PhysicsChainActivityCounters GetActivityCounters()
        => _publishedActivityCounters;

    /// <summary>
    /// Selects a bounded set of instances for delayed detailed inspection.
    /// Invalid, stale, and duplicate handles are omitted during publication.
    /// </summary>
    public void SetActivityDiagnosticSelection(ReadOnlySpan<PhysicsChainRuntimeHandle> handles)
    {
        using (_activityDiagnosticSelectionLock.EnterScope())
        {
            int count = Math.Min(handles.Length, MaximumSelectedActivityDiagnostics);
            handles[..count].CopyTo(_activityDiagnosticSelection);
            _activityDiagnosticSelectionCount = count;
        }
    }

    /// <summary>Copies the most recently published selected-instance diagnostics.</summary>
    public int CopySelectedActivityDiagnostics(Span<PhysicsChainSelectedActivityDiagnostic> destination)
    {
        int count = Math.Min(destination.Length, _publishedSelectedActivityDiagnosticCount);
        _publishedSelectedActivityDiagnostics.AsSpan(0, count).CopyTo(destination);
        return count;
    }

    private void PublishActivityDiagnostics()
    {
        _publishedActivityCounters = _pendingActivityCounters;
        _publishedSelectedActivityDiagnosticCount = _pendingSelectedActivityDiagnosticCount;
        _pendingSelectedActivityDiagnostics.AsSpan(0, _pendingSelectedActivityDiagnosticCount)
            .CopyTo(_publishedSelectedActivityDiagnostics);

        int activeCount = 0;
        int sleepingCount = 0;
        int enteredSleepCount = 0;
        ulong wakeCount = 0UL;
        for (int liveIndex = 0; liveIndex < _liveSlots.Count; ++liveIndex)
        {
            int slotIndex = _liveSlots[liveIndex];
            RuntimeSlot slot = _slots[slotIndex];
            PhysicsChainComponent? component = slot.Component;
            if (component is null)
                continue;

            bool sleeping = component.IsRuntimeSleeping;
            if (sleeping)
                ++sleepingCount;
            else if (component.IsActiveInHierarchy)
                ++activeCount;
            if (sleeping && !slot.WasSleeping)
                ++enteredSleepCount;
            if (component.WakeCount >= slot.ObservedWakeCount)
                wakeCount += component.WakeCount - slot.ObservedWakeCount;
            slot.WasSleeping = sleeping;
            slot.ObservedWakeCount = component.WakeCount;
            _slots[slotIndex] = slot;
        }
        _pendingActivityCounters = new PhysicsChainActivityCounters(
            _activeFrame, activeCount, sleepingCount, enteredSleepCount, wakeCount);

        Span<PhysicsChainRuntimeHandle> selection = stackalloc PhysicsChainRuntimeHandle[MaximumSelectedActivityDiagnostics];
        int selectionCount;
        using (_activityDiagnosticSelectionLock.EnterScope())
        {
            selectionCount = _activityDiagnosticSelectionCount;
            _activityDiagnosticSelection.AsSpan(0, selectionCount).CopyTo(selection);
        }

        int diagnosticCount = 0;
        for (int selectionIndex = 0; selectionIndex < selectionCount; ++selectionIndex)
        {
            PhysicsChainRuntimeHandle handle = selection[selectionIndex];
            if (!TryResolveRuntimeHandle(handle, out PhysicsChainComponent? component) || component is null)
                continue;

            bool duplicate = false;
            for (int existingIndex = 0; existingIndex < diagnosticCount; ++existingIndex)
            {
                if (_pendingSelectedActivityDiagnostics[existingIndex].Handle != handle)
                    continue;
                duplicate = true;
                break;
            }
            if (duplicate)
                continue;

            _pendingSelectedActivityDiagnostics[diagnosticCount++] = new PhysicsChainSelectedActivityDiagnostic(
                handle,
                component.IsRuntimeSleeping,
                component.LastActivitySnapshot,
                component.LastWakeReason,
                component.WakeCount);
        }
        _pendingSelectedActivityDiagnosticCount = diagnosticCount;
    }
}
