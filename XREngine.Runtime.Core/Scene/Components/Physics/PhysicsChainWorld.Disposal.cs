using System.Runtime.ExceptionServices;

namespace XREngine.Components;

public sealed partial class PhysicsChainWorld
{
    /// <summary>Whether this world has stopped accepting physics-chain work.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposeRequested) != 0;

    /// <summary>
    /// Releases the physics-chain runtime for a context without a disposal event.
    /// The context cannot register chains again; a new context starts a new lifetime.
    /// RuntimeWorld invokes this cleanup automatically through its disposal event.
    /// </summary>
    public static void Release(IRuntimeWorldContext world)
    {
        ArgumentNullException.ThrowIfNull(world);
        PhysicsChainWorld? scheduler;
        using (RegistryLock.EnterScope())
        {
            if (!ReleasedWorlds.TryGetValue(world, out _))
                ReleasedWorlds.Add(world, ReleasedWorld);
            Worlds.TryGetValue(world, out scheduler);
        }
        scheduler?.Dispose();
    }

    /// <summary>
    /// Closes admission, waits for the current tick and its workers, and releases
    /// owned runtime state. Reentrant teardown finishes when its calling tick exits.
    /// </summary>
    public void Dispose()
    {
        CloseAdmission();

        // A worker cannot wait for the tick which is waiting for that worker.
        // Its outer tick performs teardown after all completion signals arrive.
        if (ReferenceEquals(s_executingWorkerWorld, this))
            return;

        // Ticks can consult the registry. Never hold its lock while waiting for
        // a tick or for workers which can themselves enqueue component changes.
        using (_tickGate.EnterScope())
            if (_tickDepth == 0 && !_attachingCallbacks)
                DisposeExclusive();
    }

    private void CloseAdmission()
    {
        Interlocked.Exchange(ref _disposeRequested, 1);
        using (RegistryLock.EnterScope())
            if (!ReleasedWorlds.TryGetValue(_world, out _))
                ReleasedWorlds.Add(_world, ReleasedWorld);
    }

    private void AttachCallbacks()
    {
        bool disposeOwner = false;
        using (_tickGate.EnterScope())
        {
            if (IsDisposed)
                return;
            IRuntimeWorldContext world = _world;
            _attachingCallbacks = true;
            ExceptionDispatchInfo? firstFault = null;
            try
            {
                world.RegisterTick(ETickGroup.PostPhysics, (int)ETickOrder.Animation, FixedTick);
                if (!IsDisposed)
                    world.RegisterTick(ETickGroup.Normal, (int)ETickOrder.Animation, UpdateTick);
                if (!IsDisposed)
                    world.RegisterTick(ETickGroup.Late, (int)ETickOrder.Animation, LateTick);
            }
            catch (Exception ex)
            {
                firstFault = ExceptionDispatchInfo.Capture(ex);
                CloseAdmission();
            }
            finally
            {
                _attachingCallbacks = false;
                try { disposeOwner = FinishPendingDisposal(); }
                catch (Exception ex) { firstFault ??= ExceptionDispatchInfo.Capture(ex); }
            }
            firstFault?.Throw();
        }
        if (disposeOwner)
            ScheduleOwnerDisposal();
    }

    internal static bool PrepareWorldDisposal(RuntimeWorld world)
    {
        PhysicsChainWorld? scheduler;
        using (RegistryLock.EnterScope())
            Worlds.TryGetValue(world, out scheduler);

        // The world's own tick, worker, or mutation lease defers the disposal until it ends.
        bool ownContext = scheduler is not null &&
            (ReferenceEquals(s_executingWorkerWorld, scheduler)
                || (scheduler._tickGate.IsHeldByCurrentThread &&
                    (scheduler._tickDepth > 0 || scheduler._attachingCallbacks)));

        // Another world's physics callback, worker, or scene mutation cannot wait for
        // this world or start its scene teardown. Reject before admission closes, so
        // the world stays usable.
        if (!ownContext && (s_outerTickWorld is not null
            || s_executingWorkerWorld is not null
            || s_activeWorldMutation is not null
            || s_hierarchyReadCallbackDepth != 0))
            throw new InvalidOperationException(
                "Dispose the runtime world after the current physics callback or scene mutation completes.");

        if (scheduler is null)
            return true;

        scheduler._ownerDisposalPending = true;
        scheduler.CloseAdmission();
        if (ownContext)
        {
            scheduler._deferredOwnerDisposal = true;
            return false;
        }

        // No new tick can start after admission closes. Keep the closing entry
        // discoverable until cleanup so concurrent owner requests also quiesce.
        using (scheduler._tickGate.EnterScope())
            return true;
    }

    private void OnWorldDisposing(RuntimeWorld world)
        => Dispose();

    /// <summary>
    /// Rejects a readback call from a batch worker of this world. The worker's
    /// tick holds the world state that the readback must validate.
    /// </summary>
    internal void ThrowIfReadbackFromBatchWorker()
    {
        if (ReferenceEquals(s_executingWorkerWorld, this))
            throw new InvalidOperationException("Physics-chain readbacks cannot execute inside a world batch worker.");
    }

    /// <summary>
    /// Ends one tick inside the tick gate. Returns true when the owner world must
    /// be disposed after the tick releases its gate and scene-mutation admission.
    /// </summary>
    private bool CompleteTick()
    {
        --_tickDepth;
        return FinishPendingDisposal();
    }

    /// <summary>
    /// Runs after the outermost tick releases every gate. Deferred transfers and a
    /// deferred owner disposal can change scene state, so they cannot run inside a tick.
    /// </summary>
    private void FinishOuterTick(PhysicsChainWorld? previousContext, bool disposeOwner)
    {
        try
        {
            if (previousContext is null)
                DrainDeferredTransfers();
        }
        finally
        {
            if (disposeOwner)
                ScheduleOwnerDisposal();
        }
    }

    /// <summary>
    /// Disposes the owner world after the current tick dispatch of that world ends.
    /// Later callbacks of the same dispatch then never run on a torn-down world.
    /// </summary>
    private void ScheduleOwnerDisposal()
    {
        if (_world is RuntimeWorld world)
            world.RunAfterTickDispatch(DisposeOwnerWorld);
        else
            DisposeOwnerWorld();
    }

    private bool FinishPendingDisposal()
    {
        if (_tickDepth != 0 || _attachingCallbacks || !IsDisposed)
            return false;
        if (_deferredOwnerDisposal && _world is RuntimeWorld)
            return true;
        if (!_ownerDisposalPending)
            DisposeExclusive();
        return false;
    }

    private void DisposeOwnerWorld()
    {
        // The world field never changes, so a concurrent teardown cannot lose this request.
        RuntimeWorld? world = _world as RuntimeWorld;
        _deferredOwnerDisposal = false;
        try { world?.Dispose(); }
        finally
        {
            using (_tickGate.EnterScope())
                DisposeExclusive();
        }
    }

    private void DisposeExclusive()
    {
        if (_disposed)
            return;
        _disposed = true;

        IRuntimeWorldContext world = _world;
        ExceptionDispatchInfo? firstFault = null;

        // Close readback first. Renderer calls then see a closed service before
        // any slot state changes below.
        ReleaseOwned(() => PhysicsChainWorldReadbackExtensions.ReleaseWorld(this));

        using (RegistryLock.EnterScope())
            if (Worlds.TryGetValue(world, out PhysicsChainWorld? current) && ReferenceEquals(current, this))
                Worlds.Remove(world);

        if (world is RuntimeWorld runtimeWorld)
            runtimeWorld.Disposing -= OnWorldDisposing;
        ReleaseOwned(() => world.UnregisterTick(ETickGroup.PostPhysics, (int)ETickOrder.Animation, FixedTick));
        ReleaseOwned(() => world.UnregisterTick(ETickGroup.Normal, (int)ETickOrder.Animation, UpdateTick));
        ReleaseOwned(() => world.UnregisterTick(ETickGroup.Late, (int)ETickOrder.Animation, LateTick));

        var components = new HashSet<PhysicsChainComponent>(System.Collections.Generic.ReferenceEqualityComparer.Instance);
        foreach (RuntimeSlot slot in _slots)
            if (slot.Component is { } component)
                components.Add(component);
        ReleaseOwned(FreeAllRegistrationArenas);

        using (_commandGate.EnterScope())
        {
            while (_commands.TryDequeue(out StructuralCommand command))
                components.Add(command.Component);
            while (_dynamicCommands.TryDequeue(out DynamicCommand command))
                components.Add(command.Key.Component);
            _latestCommandVersion.Clear();
            _latestDynamicCommandVersion.Clear();
        }

        foreach (PhysicsChainComponent component in components)
            ReleaseOwned(() => DetachAndForward(component));

        // Some readers resolve handles without the tick gate. Keep the lengths of
        // slot-indexed lists and invalidate every entry instead of shrinking them.
        for (int slotIndex = 0; slotIndex < _slots.Count; ++slotIndex)
        {
            RuntimeSlot slot = _slots[slotIndex];
            _slots[slotIndex] = new RuntimeSlot
            {
                Generation = NextGeneration(slot.Generation),
                DenseIndex = -1,
            };
        }
        for (int slotIndex = 0; slotIndex < _clocks.Count; ++slotIndex)
            _clocks[slotIndex] = default;
        for (int slotIndex = 0; slotIndex < _gpuRestInputRanges.Count; ++slotIndex)
        {
            _gpuRestInputRanges[slotIndex]?.RigidCache.Clear();
            _gpuRestInputRanges[slotIndex] = null;
        }

        _slotByComponent.Clear();
        _liveSlots.Clear();
        _freeSlots.Clear();
        _retiredArenaRegistrations.Clear();
        _deferredStructuralMutations.Clear();
        _gpuRestResetNodeOwners.Clear();
        _gpuRestResetNodeFirstOwnerSlots.Clear();
        _gpuRestForcedCompatibilitySlots.Clear();
        _gpuRestPrerequisiteMatrixTargets.Clear();
        _parallelComponents.Clear();
        _cpuSharedColliderSets.Clear();
        _qualityCandidates.Clear();
        foreach (BatchWorkItem workItem in _parallelWorkItems)
            workItem.Dispose();
        _parallelWorkItems = [];
        _parallelRangeEnds = [];
        _cpuBatchHandles = [];
        _cpuBatchComponents = [];
        _prepareComponents = [];
        _prepareEligible = [];
        _prepareResults = [];
        _prepareFaults = [];

        ReleaseOwned(() => PhysicsChainTemplateCache.ReleaseWorld(this));
        ReleaseOwned(() => PhysicsChainColliderSetCache.ReleaseWorld(this));
        firstFault?.Throw();

        void ReleaseOwned(Action release)
        {
            try { release(); }
            catch (Exception ex) { firstFault ??= ExceptionDispatchInfo.Capture(ex); }
        }
    }

    /// <summary>
    /// Releases this world's binding. When the component already moved to another
    /// world, queues its add command there. This runs inside the tick gate, so it
    /// never creates, ticks, or disposes another world.
    /// </summary>
    private void DetachAndForward(PhysicsChainComponent component)
    {
        if (!component.DetachWorldRuntime(this))
            return;
        if (component.IsDestroyed
            || !component.IsActiveInHierarchy
            || component.World is not IRuntimeWorldContext target
            || ReferenceEquals(target, _world))
            return;
        if (TryGet(target, out PhysicsChainWorld? destination) && destination is not null)
            destination.EnqueueStructuralCommand(CommandKind.Add, component);
    }

    private void FreeAllRegistrationArenas()
    {
        foreach (RuntimeSlot slot in _slots)
            FreeRegistrationArenas(slot.InstanceArenaHandle, slot.StateArenaHandle, slot.OutputArenaHandle);
        foreach (RetiredArenaRegistration retired in _retiredArenaRegistrations)
            FreeRegistrationArenas(retired.Instance, retired.State, retired.Output);
    }

    private void FreeRegistrationArenas(
        PhysicsChainArenaHandle instance,
        PhysicsChainArenaHandle state,
        PhysicsChainArenaHandle output)
    {
        _instanceArena.Free(instance);
        _stateArena.Free(state);
        _outputArena.Free(output);
    }
}
