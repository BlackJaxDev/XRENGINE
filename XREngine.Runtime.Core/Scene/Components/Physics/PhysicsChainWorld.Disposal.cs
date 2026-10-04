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
        if (ReferenceEquals(_executingWorkerWorld, this))
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
            if (_world is { } world && !ReleasedWorlds.TryGetValue(world, out _))
                ReleasedWorlds.Add(world, ReleasedWorld);
    }

    private void AttachCallbacks()
    {
        using (_tickGate.EnterScope())
        {
            if (IsDisposed || _world is not { } world)
                return;
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
                try { FinishPendingDisposal(); }
                catch (Exception ex) { firstFault ??= ExceptionDispatchInfo.Capture(ex); }
            }
            firstFault?.Throw();
        }
    }

    internal static bool PrepareWorldDisposal(RuntimeWorld world)
    {
        PhysicsChainWorld? scheduler;
        using (RegistryLock.EnterScope())
            Worlds.TryGetValue(world, out scheduler);
        if (scheduler is null)
            return true;

        scheduler._ownerDisposalPending = true;
        scheduler.CloseAdmission();
        if (ReferenceEquals(_executingWorkerWorld, scheduler)
            || (scheduler._tickGate.IsHeldByCurrentThread &&
                (scheduler._tickDepth > 0 || scheduler._attachingCallbacks)))
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

    internal Lock.Scope EnterReadbackScope()
    {
        if (ReferenceEquals(_executingWorkerWorld, this))
            throw new InvalidOperationException("Physics-chain readbacks cannot execute inside a world batch worker.");
        Lock.Scope scope = _tickGate.EnterScope();
        if (!IsDisposed)
            return scope;
        scope.Dispose();
        throw new ObjectDisposedException(nameof(PhysicsChainWorld));
    }

    private void CompleteTick()
    {
        --_tickDepth;
        FinishPendingDisposal();
    }

    private void FinishPendingDisposal()
    {
        if (_tickDepth != 0 || _attachingCallbacks || !IsDisposed)
            return;
        if (_deferredOwnerDisposal && _world is RuntimeWorld world)
        {
            try { world.Dispose(); }
            finally { DisposeExclusive(); }
        }
        else if (!_ownerDisposalPending)
            DisposeExclusive();
    }

    private void DisposeExclusive()
    {
        if (_disposed)
            return;
        _disposed = true;

        IRuntimeWorldContext? world = _world;
        using (RegistryLock.EnterScope())
            if (world is not null && Worlds.TryGetValue(world, out PhysicsChainWorld? current) && ReferenceEquals(current, this))
                Worlds.Remove(world);

        ExceptionDispatchInfo? firstFault = null;
        if (world is RuntimeWorld runtimeWorld)
            runtimeWorld.Disposing -= OnWorldDisposing;
        ReleaseOwned(() => world?.UnregisterTick(ETickGroup.PostPhysics, (int)ETickOrder.Animation, FixedTick));
        ReleaseOwned(() => world?.UnregisterTick(ETickGroup.Normal, (int)ETickOrder.Animation, UpdateTick));
        ReleaseOwned(() => world?.UnregisterTick(ETickGroup.Late, (int)ETickOrder.Animation, LateTick));

        var components = new HashSet<PhysicsChainComponent>(System.Collections.Generic.ReferenceEqualityComparer.Instance);
        foreach (RuntimeSlot slot in _slots)
        {
            if (slot.Component is { } component)
                components.Add(component);
            FreeRegistrationArenas(slot.InstanceArenaHandle, slot.StateArenaHandle, slot.OutputArenaHandle);
        }
        foreach (RetiredArenaRegistration retired in _retiredArenaRegistrations)
            FreeRegistrationArenas(retired.Instance, retired.State, retired.Output);

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
            component.DetachWorldRuntime(this);

        _slotByComponent.Clear();
        _slots.Clear();
        _liveSlots.Clear();
        _freeSlots.Clear();
        _retiredArenaRegistrations.Clear();
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
        _world = null;

        PhysicsChainTemplateCache.ReleaseWorld(this);
        PhysicsChainColliderSetCache.ReleaseWorld(this);
        ReleaseOwned(() => PhysicsChainWorldReadbackExtensions.ReleaseWorld(this));
        firstFault?.Throw();

        void ReleaseOwned(Action release)
        {
            try { release(); }
            catch (Exception ex) { firstFault ??= ExceptionDispatchInfo.Capture(ex); }
        }
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
