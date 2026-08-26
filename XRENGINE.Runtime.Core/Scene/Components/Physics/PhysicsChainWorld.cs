using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace XREngine.Components;

/// <summary>
/// Owns scheduling for every physics chain in one runtime world. Components
/// remain authoring facades; the world contributes only one callback to each
/// required engine tick phase regardless of chain count.
/// </summary>
public sealed partial class PhysicsChainWorld
{
    private enum CommandKind : byte
    {
        Add,
        Remove,
        Retemplate,
        Resize,
        Rebind,
        BackendSwitch,
    }

    private readonly record struct StructuralCommand(
        CommandKind Kind,
        PhysicsChainComponent Component,
        long Version);

    private struct RuntimeSlot
    {
        public PhysicsChainComponent? Component;
        public uint Generation;
        public int DenseIndex;
        public QualityRuntimeState QualityState;
        public bool WasSleeping;
        public ulong ObservedWakeCount;
        public PhysicsChainArenaHandle InstanceArenaHandle;
        public PhysicsChainArenaHandle StateArenaHandle;
        public PhysicsChainArenaHandle OutputArenaHandle;
    }

    private sealed class BatchWorkItem
    {
        private readonly ManualResetEventSlim _completed = new(initialState: true);
        private List<PhysicsChainComponent>? _components;
        private PhysicsChainCpuBackend? _cpuBackend;
        private PhysicsChainArenaHandle[]? _cpuHandles;
        private PhysicsChainComponent[]? _cpuComponents;
        private PhysicsChainComponent[]? _prepareComponents;
        private bool[]? _prepareEligible;
        private bool[]? _prepareResults;
        private Exception?[]? _prepareFaults;
        private int _startInclusive;
        private int _endExclusive;

        public Exception? Fault { get; private set; }

        public void Configure(List<PhysicsChainComponent> components, int startInclusive, int endExclusive)
        {
            _components = components;
            _cpuBackend = null;
            _cpuHandles = null;
            _cpuComponents = null;
            _prepareComponents = null;
            _prepareEligible = null;
            _prepareResults = null;
            _prepareFaults = null;
            _startInclusive = startInclusive;
            _endExclusive = endExclusive;
            Fault = null;
            _completed.Reset();
        }

        public void ConfigureCpuBatch(
            PhysicsChainCpuBackend backend,
            PhysicsChainArenaHandle[] handles,
            PhysicsChainComponent[] components,
            int startInclusive,
            int endExclusive)
        {
            _prepareComponents = null;
            _prepareEligible = null;
            _prepareResults = null;
            _prepareFaults = null;
            _components = null;
            _cpuBackend = backend;
            _cpuHandles = handles;
            _cpuComponents = components;
            _startInclusive = startInclusive;
            _endExclusive = endExclusive;
            Fault = null;
            _completed.Reset();
        }

        public void ConfigurePrepare(
            PhysicsChainComponent[] components,
            bool[] eligible,
            bool[] results,
            Exception?[] faults,
            int startInclusive,
            int endExclusive)
        {
            _components = null;
            _cpuBackend = null;
            _cpuHandles = null;
            _cpuComponents = null;
            _prepareComponents = components;
            _prepareEligible = eligible;
            _prepareResults = results;
            _prepareFaults = faults;
            _startInclusive = startInclusive;
            _endExclusive = endExclusive;
            Fault = null;
            _completed.Reset();
        }

        public void Run()
        {
            try
            {
                List<PhysicsChainComponent>? components = _components;
                if (components is not null)
                    RunPreparedRange(components, _startInclusive, _endExclusive);
                else if (_cpuBackend is not null && _cpuHandles is not null && _cpuComponents is not null)
                    RunCpuBatchRange(
                        _cpuBackend,
                        _cpuHandles,
                        _cpuComponents,
                        _startInclusive,
                        _endExclusive);
                else if (_prepareComponents is not null
                    && _prepareEligible is not null
                    && _prepareResults is not null
                    && _prepareFaults is not null)
                    RunPrepareRange(
                        _prepareComponents,
                        _prepareEligible,
                        _prepareResults,
                        _prepareFaults,
                        _startInclusive,
                        _endExclusive);
            }
            catch (Exception ex)
            {
                Fault = ex;
            }
            finally
            {
                _completed.Set();
            }
        }

        public void Wait()
            => _completed.Wait();
    }

    private static readonly Lock RegistryLock = new();
    private static readonly Dictionary<IRuntimeWorldContext, PhysicsChainWorld> Worlds =
        new(System.Collections.Generic.ReferenceEqualityComparer.Instance);

    // Fixed updates run on their own timer thread and can overlap normal/late updates.
    // All three phases touch the same slot registries and component simulation state, so
    // serialize them at the world boundary instead of allowing concurrent collection access.
    private readonly Lock _tickGate = new();
    private readonly IRuntimeWorldContext _world;
    private readonly ConcurrentQueue<StructuralCommand> _commands = [];
    private readonly ConcurrentDictionary<PhysicsChainComponent, long> _latestCommandVersion =
        new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
    private long _nextCommandVersion;
    private readonly Dictionary<PhysicsChainComponent, int> _slotByComponent =
        new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
    private readonly List<RuntimeSlot> _slots = [];
    private readonly List<int> _liveSlots = [];
    private readonly Stack<int> _freeSlots = [];
    private readonly List<PhysicsChainComponent> _parallelComponents = [];
    private int[] _parallelRangeEnds = [];
    private BatchWorkItem[] _parallelWorkItems = [];
    private PhysicsChainArenaHandle[] _cpuBatchHandles = [];
    private PhysicsChainComponent[] _cpuBatchComponents = [];
    private readonly PhysicsChainCpuBackend _cpuBackend = new();
    private PhysicsChainComponent[] _prepareComponents = [];
    private bool[] _prepareEligible = [];
    private bool[] _prepareResults = [];
    private Exception?[] _prepareFaults = [];

    private PhysicsChainWorld(IRuntimeWorldContext world)
    {
        _world = world;
        world.RegisterTick(ETickGroup.PostPhysics, (int)ETickOrder.Animation, FixedTick);
        world.RegisterTick(ETickGroup.Normal, (int)ETickOrder.Animation, UpdateTick);
        world.RegisterTick(ETickGroup.Late, (int)ETickOrder.Animation, LateTick);
    }

    /// <summary>
    /// Number of components whose structural registration is currently live.
    /// Pending structural commands are applied at the next world tick.
    /// </summary>
    public int RegisteredCount => _liveSlots.Count;

    /// <summary>
    /// Number of runtime slots reserved by this world, including reusable free
    /// slots. This distinguishes stable arena capacity from the live count.
    /// </summary>
    public int SlotCapacity => _slots.Count;

    /// <summary>
    /// Resolves a handle while the world is at a structural-command boundary.
    /// A recycled slot never resolves through a previous occupant's generation.
    /// </summary>
    internal bool TryResolveRuntimeHandle(
        PhysicsChainRuntimeHandle handle,
        out PhysicsChainComponent? component)
    {
        component = null;
        if (!handle.IsValid || (uint)handle.Slot >= (uint)_slots.Count)
            return false;

        RuntimeSlot slot = _slots[handle.Slot];
        if (slot.Generation != handle.Generation || slot.Component is null)
            return false;

        component = slot.Component;
        return true;
    }

    /// <summary>
    /// Registers a component with the scheduler for its current world.
    /// </summary>
    public static void Register(PhysicsChainComponent component)
    {
        ArgumentNullException.ThrowIfNull(component);
        IRuntimeWorldContext? world = component.World;
        if (world is null)
            return;

        PhysicsChainWorld scheduler;
        using (RegistryLock.EnterScope())
        {
            if (!Worlds.TryGetValue(world, out scheduler!))
            {
                scheduler = new PhysicsChainWorld(world);
                Worlds.Add(world, scheduler);
            }
        }

        scheduler.EnqueueStructuralCommand(CommandKind.Add, component);
    }

    /// <summary>
    /// Removes a component from the scheduler for its current world.
    /// </summary>
    public static void Unregister(PhysicsChainComponent component)
    {
        ArgumentNullException.ThrowIfNull(component);
        IRuntimeWorldContext? world = component.World;
        if (world is null)
        {
            component.SetRuntimeHandle(PhysicsChainRuntimeHandle.Invalid);
            return;
        }

        PhysicsChainWorld? scheduler;
        using (RegistryLock.EnterScope())
            Worlds.TryGetValue(world, out scheduler);

        if (scheduler is null)
        {
            component.SetRuntimeHandle(PhysicsChainRuntimeHandle.Invalid);
            return;
        }

        scheduler.EnqueueStructuralCommand(CommandKind.Remove, component);
    }

    internal static bool TryGet(IRuntimeWorldContext world, out PhysicsChainWorld? scheduler)
    {
        using (RegistryLock.EnterScope())
            return Worlds.TryGetValue(world, out scheduler);
    }

    private void FixedTick()
    {
        using (_tickGate.EnterScope())
            FixedTickExclusive();
    }

    private void FixedTickExclusive()
    {
        DrainStructuralCommands();
        for (int i = 0; i < _liveSlots.Count; ++i)
        {
            PhysicsChainComponent? component = _slots[_liveSlots[i]].Component;
            if (component is { IsActiveInHierarchy: true })
                component.WorldFixedTick();
        }
    }

    private void UpdateTick()
    {
        using (_tickGate.EnterScope())
            UpdateTickExclusive();
    }

    private void UpdateTickExclusive()
    {
        DrainStructuralCommands();
        for (int i = 0; i < _liveSlots.Count; ++i)
        {
            PhysicsChainComponent? component = _slots[_liveSlots[i]].Component;
            if (component is { IsActiveInHierarchy: true })
                component.WorldUpdateTick();
        }
    }

    private void LateTick()
    {
        using (_tickGate.EnterScope())
            LateTickExclusive();
    }

    private void LateTickExclusive()
    {
        DrainStructuralCommands();
        PhysicsChainComponent.AdvancePreparedColliderFrame();
        AssignQualityTiers();
        _parallelComponents.Clear();

        int prepareCount = 0;
        ExceptionDispatchInfo? firstFault = null;
        for (int i = 0; i < _liveSlots.Count; ++i)
        {
            PhysicsChainComponent? component = _slots[_liveSlots[i]].Component;
            if (component is not { IsActiveInHierarchy: true })
                continue;
            EnsurePrepareCapacity(prepareCount + 1);
            _prepareComponents[prepareCount] = component;
            _prepareEligible[prepareCount] = component.CanPrepareWorldLateTickInputsInParallel;
            _prepareResults[prepareCount] = false;
            _prepareFaults[prepareCount] = null;

            if (!_prepareEligible[prepareCount])
            {
                try
                {
                    _prepareResults[prepareCount] = component.BeginWorldLateTick();
                }
                catch (Exception ex)
                {
                    component.AbortWorldLateTick();
                    _prepareFaults[prepareCount] = ex;
                }
            }
            ++prepareCount;
        }

        // Collider snapshots may be shared by many chains. Prepare their
        // mutable world-space cache once on the world thread before component
        // hierarchy/input gathering fans out to workers.
        for (int prepareIndex = 0; prepareIndex < prepareCount; ++prepareIndex)
        {
            if (!_prepareEligible[prepareIndex])
                continue;
            try
            {
                _prepareComponents[prepareIndex].PrepareWorldCollidersForParallelInputGather();
            }
            catch (Exception ex)
            {
                _prepareComponents[prepareIndex].AbortWorldLateTick();
                _prepareEligible[prepareIndex] = false;
                _prepareFaults[prepareIndex] = ex;
            }
        }

        firstFault = ExecutePrepareParallel(prepareCount) is Exception prepareFault
            ? ExceptionDispatchInfo.Capture(prepareFault)
            : null;
        for (int prepareIndex = 0; prepareIndex < prepareCount; ++prepareIndex)
        {
            PhysicsChainComponent component = _prepareComponents[prepareIndex];
            Exception? fault = _prepareFaults[prepareIndex];
            if (fault is not null)
                firstFault ??= ExceptionDispatchInfo.Capture(fault);
            else if (_prepareResults[prepareIndex])
            {
                try
                {
                    if (_prepareEligible[prepareIndex])
                        component.FinalizeWorldLateTickParallelPreparation();
                    _parallelComponents.Add(component);
                }
                catch (Exception ex)
                {
                    component.AbortWorldLateTick();
                    firstFault ??= ExceptionDispatchInfo.Capture(ex);
                }
            }
            _prepareComponents[prepareIndex] = null!;
            _prepareFaults[prepareIndex] = null;
        }

        SynchronizeCpuSharedColliderSets();
        if (_parallelComponents.Count > 0)
        {
            try
            {
                ExecutePreparedCpuBatch(_parallelComponents);
                ExecutePreparedParallel(_parallelComponents);
            }
            catch (Exception ex)
            {
                firstFault ??= ExceptionDispatchInfo.Capture(ex);
            }

            for (int i = 0; i < _parallelComponents.Count; ++i)
            {
                PhysicsChainComponent component = _parallelComponents[i];
                try
                {
                    component.PublishWorldLateTick();
                }
                catch (Exception ex)
                {
                    component.AbortWorldLateTick();
                    firstFault ??= ExceptionDispatchInfo.Capture(ex);
                }
            }
        }

        PublishActivityDiagnostics();
        ++_activeFrame;
        firstFault?.Throw();
    }

    private void AssignQualityTiers()
    {
        EvaluateQualityBudget();
    }

    private void EnqueueStructuralCommand(CommandKind kind, PhysicsChainComponent component)
    {
        long version = Interlocked.Increment(ref _nextCommandVersion);
        _latestCommandVersion.AddOrUpdate(component, version, (_, current) => Math.Max(current, version));
        _commands.Enqueue(new StructuralCommand(kind, component, version));
    }

    private void DrainStructuralCommands()
    {
        ReclaimRetiredArenas();
        while (_commands.TryDequeue(out StructuralCommand command))
        {
            if (!_latestCommandVersion.TryGetValue(command.Component, out long latestVersion)
                || latestVersion != command.Version)
                continue;

            if (command.Kind == CommandKind.Add)
                AddComponent(command.Component);
            else if (command.Kind == CommandKind.Remove)
                RemoveComponent(command.Component);
            else
                ApplyStructuralMutation(command.Kind, command.Component);
            ++_appliedStructuralCommands;

            ((ICollection<KeyValuePair<PhysicsChainComponent, long>>)_latestCommandVersion).Remove(
                new KeyValuePair<PhysicsChainComponent, long>(command.Component, command.Version));
        }
        DrainDynamicCommands();
    }

    private void AddComponent(PhysicsChainComponent component)
    {
        if (component.IsDestroyed
            || !ReferenceEquals(component.World, _world)
            || !component.IsActiveInHierarchy)
        {
            component.DetachCpuBackend();
            component.SetRuntimeHandle(PhysicsChainRuntimeHandle.Invalid);
            return;
        }

        if (_slotByComponent.TryGetValue(component, out int existingSlot))
        {
            RuntimeSlot existing = _slots[existingSlot];
            component.AttachCpuBackend(this, _cpuBackend);
            component.SetRuntimeHandle(new PhysicsChainRuntimeHandle(existingSlot, existing.Generation));
            return;
        }

        // Snapshot restoration recreates components with their persistent XR object ID.
        // If an earlier graph missed a lifecycle callback, replace that stale identity
        // instead of allowing two runtime slots for the same authored component.
        PhysicsChainComponent? replacedComponent = null;
        for (int liveIndex = 0; liveIndex < _liveSlots.Count; ++liveIndex)
        {
            PhysicsChainComponent? candidate = _slots[_liveSlots[liveIndex]].Component;
            if (candidate is not null
                && !ReferenceEquals(candidate, component)
                && candidate.ID == component.ID)
            {
                replacedComponent = candidate;
                break;
            }
        }
        if (replacedComponent is not null)
            RemoveComponent(replacedComponent);

        int slotIndex;
        RuntimeSlot slot;
        if (_freeSlots.TryPop(out slotIndex))
        {
            slot = _slots[slotIndex];
            slot.Generation = NextGeneration(slot.Generation);
        }
        else
        {
            slotIndex = _slots.Count;
            slot = new RuntimeSlot { Generation = 1u };
            _slots.Add(default);
        }

        slot.Component = component;
        slot.DenseIndex = _liveSlots.Count;
        slot.QualityState = CreateInitialQualityState(component);
        slot.WasSleeping = component.IsRuntimeSleeping;
        slot.ObservedWakeCount = component.WakeCount;
        var runtimeHandle = new PhysicsChainRuntimeHandle(slotIndex, slot.Generation);
        AllocateRegistrationArenas(ref slot, runtimeHandle);
        _slots[slotIndex] = slot;
        _liveSlots.Add(slotIndex);
        _slotByComponent.Add(component, slotIndex);
        component.AttachCpuBackend(this, _cpuBackend);
        component.SetRuntimeHandle(runtimeHandle);
    }

    private void RemoveComponent(PhysicsChainComponent component)
    {
        if (!_slotByComponent.Remove(component, out int slotIndex))
        {
            component.SetRuntimeHandle(PhysicsChainRuntimeHandle.Invalid);
            return;
        }

        RuntimeSlot slot = _slots[slotIndex];
        int removedDenseIndex = slot.DenseIndex;
        int lastDenseIndex = _liveSlots.Count - 1;
        if (removedDenseIndex != lastDenseIndex)
        {
            int movedSlotIndex = _liveSlots[lastDenseIndex];
            _liveSlots[removedDenseIndex] = movedSlotIndex;
            RuntimeSlot movedSlot = _slots[movedSlotIndex];
            movedSlot.DenseIndex = removedDenseIndex;
            _slots[movedSlotIndex] = movedSlot;
        }

        _liveSlots.RemoveAt(lastDenseIndex);
        RetireRegistrationArenas(slot);
        slot.Component = null;
        slot.DenseIndex = -1;
        slot.Generation = NextGeneration(slot.Generation);
        _slots[slotIndex] = slot;
        _freeSlots.Push(slotIndex);
        component.DetachCpuBackend();
        component.SetRuntimeHandle(PhysicsChainRuntimeHandle.Invalid);
    }

    private static uint NextGeneration(uint generation)
    {
        unchecked
        {
            ++generation;
        }

        return generation == 0u ? 1u : generation;
    }

    private void EnsurePrepareCapacity(int requiredCount)
    {
        if (_prepareComponents.Length >= requiredCount)
            return;

        int capacity = Math.Max(requiredCount, Math.Max(_prepareComponents.Length * 2, 16));
        Array.Resize(ref _prepareComponents, capacity);
        Array.Resize(ref _prepareEligible, capacity);
        Array.Resize(ref _prepareResults, capacity);
        Array.Resize(ref _prepareFaults, capacity);
    }

    private Exception? ExecutePrepareParallel(int count)
    {
        const int minimumComponentsPerSlice = 32;
        int processorCount = Math.Max(Environment.ProcessorCount, 1);
        int sliceCount = JobManager.IsJobWorkerThread
            ? 1
            : Math.Min(processorCount, Math.Max(1, (count + minimumComponentsPerSlice - 1) / minimumComponentsPerSlice));
        if (sliceCount <= 1)
        {
            RunPrepareRange(_prepareComponents, _prepareEligible, _prepareResults, _prepareFaults, 0, count);
            return null;
        }

        EnsureParallelCapacity(sliceCount);
        BuildWeightedPrepareRanges(count, sliceCount);
        int localEnd = _parallelRangeEnds[0];
        int start = localEnd;
        int workItemCount = 0;
        for (int slice = 1; slice < sliceCount; ++slice)
        {
            int end = _parallelRangeEnds[slice];
            if (end <= start)
                continue;
            BatchWorkItem workItem = _parallelWorkItems[workItemCount++];
            workItem.ConfigurePrepare(
                _prepareComponents,
                _prepareEligible,
                _prepareResults,
                _prepareFaults,
                start,
                end);
            ThreadPool.UnsafeQueueUserWorkItem(static state => state.Run(), workItem, preferLocal: false);
            start = end;
        }

        Exception? firstFault = null;
        try
        {
            RunPrepareRange(_prepareComponents, _prepareEligible, _prepareResults, _prepareFaults, 0, localEnd);
        }
        catch (Exception ex)
        {
            firstFault = ex;
        }
        for (int workItemIndex = 0; workItemIndex < workItemCount; ++workItemIndex)
        {
            BatchWorkItem workItem = _parallelWorkItems[workItemIndex];
            workItem.Wait();
            firstFault ??= workItem.Fault;
        }
        return firstFault;
    }

    private void BuildWeightedPrepareRanges(int count, int sliceCount)
    {
        long totalWeight = 0L;
        for (int scanIndex = 0; scanIndex < count; ++scanIndex)
            totalWeight += Math.Max(_prepareComponents[scanIndex].EstimatedWorldWork, 1);

        int componentIndex = 0;
        long consumedWeight = 0L;
        for (int slice = 0; slice < sliceCount; ++slice)
        {
            int remainingSlices = sliceCount - slice;
            int maxEnd = count - (remainingSlices - 1);
            long targetWeight = (totalWeight * (slice + 1) + sliceCount - 1L) / sliceCount;
            while (componentIndex < maxEnd && consumedWeight < targetWeight)
            {
                consumedWeight += Math.Max(_prepareComponents[componentIndex].EstimatedWorldWork, 1);
                ++componentIndex;
            }
            _parallelRangeEnds[slice] = componentIndex;
        }
        _parallelRangeEnds[sliceCount - 1] = count;
    }

    private void ExecutePreparedParallel(List<PhysicsChainComponent> components)
    {
        int workCount = components.Count;
        int sliceCount = Math.Min(workCount, Math.Max(Environment.ProcessorCount, 1));
        if (sliceCount <= 1 || JobManager.IsJobWorkerThread)
        {
            RunPreparedRange(components, 0, workCount);
            return;
        }

        EnsureParallelCapacity(sliceCount);
        BuildWeightedRanges(components, sliceCount);

        int start = 0;
        int workItemCount = 0;
        for (int slice = 0; slice < sliceCount; ++slice)
        {
            int end = _parallelRangeEnds[slice];
            if (end <= start)
                continue;

            if (slice == 0)
                RunPreparedRange(components, start, end);
            else
            {
                BatchWorkItem workItem = _parallelWorkItems[workItemCount++];
                workItem.Configure(components, start, end);
                ThreadPool.UnsafeQueueUserWorkItem(static state => state.Run(), workItem, preferLocal: false);
            }

            start = end;
        }

        Exception? firstFault = null;
        for (int i = 0; i < workItemCount; ++i)
        {
            BatchWorkItem workItem = _parallelWorkItems[i];
            workItem.Wait();
            firstFault ??= workItem.Fault;
        }

        if (firstFault is not null)
            throw new AggregateException("Physics chain world update failed.", firstFault);
    }

    private void EnsureParallelCapacity(int sliceCount)
    {
        if (_parallelRangeEnds.Length < sliceCount)
            Array.Resize(ref _parallelRangeEnds, sliceCount);

        int requiredWorkItems = Math.Max(0, sliceCount - 1);
        if (_parallelWorkItems.Length >= requiredWorkItems)
            return;

        int previousLength = _parallelWorkItems.Length;
        Array.Resize(ref _parallelWorkItems, requiredWorkItems);
        for (int i = previousLength; i < requiredWorkItems; ++i)
            _parallelWorkItems[i] = new BatchWorkItem();
    }

    private void BuildWeightedRanges(List<PhysicsChainComponent> components, int sliceCount)
    {
        long totalWeight = 0L;
        for (int i = 0; i < components.Count; ++i)
            totalWeight += Math.Max(components[i].EstimatedWorldWork, 1);

        int componentIndex = 0;
        long consumedWeight = 0L;
        for (int slice = 0; slice < sliceCount; ++slice)
        {
            int remainingSlices = sliceCount - slice;
            int maxEnd = components.Count - (remainingSlices - 1);
            long targetWeight = (totalWeight * (slice + 1) + sliceCount - 1L) / sliceCount;

            while (componentIndex < maxEnd && consumedWeight < targetWeight)
            {
                consumedWeight += Math.Max(components[componentIndex].EstimatedWorldWork, 1);
                ++componentIndex;
            }

            _parallelRangeEnds[slice] = componentIndex;
        }

        _parallelRangeEnds[sliceCount - 1] = components.Count;
    }

    private void ExecutePreparedCpuBatch(List<PhysicsChainComponent> components)
    {
        if (_cpuBatchHandles.Length < components.Count)
        {
            int capacity = Math.Max(components.Count, Math.Max(_cpuBatchHandles.Length * 2, 16));
            Array.Resize(ref _cpuBatchHandles, capacity);
            Array.Resize(ref _cpuBatchComponents, capacity);
        }

        int count = 0;
        for (int componentIndex = 0; componentIndex < components.Count; ++componentIndex)
        {
            PhysicsChainComponent component = components[componentIndex];
            if (!component.TryGetPreparedCpuBatchHandle(out PhysicsChainArenaHandle handle))
                continue;
            _cpuBatchHandles[count] = handle;
            _cpuBatchComponents[count] = component;
            ++count;
        }

        if (count == 0)
            return;

        const int minimumHandlesPerSlice = 32;
        int processorCount = Math.Max(Environment.ProcessorCount, 1);
        int sliceCount = JobManager.IsJobWorkerThread
            ? 1
            : Math.Min(processorCount, Math.Max(1, (count + minimumHandlesPerSlice - 1) / minimumHandlesPerSlice));
        if (sliceCount <= 1)
        {
            RunCpuBatchRange(_cpuBackend, _cpuBatchHandles, _cpuBatchComponents, 0, count);
            ClearCpuBatchComponents(count);
            return;
        }

        EnsureParallelCapacity(sliceCount);
        int handlesPerSlice = (count + sliceCount - 1) / sliceCount;
        int localEnd = Math.Min(handlesPerSlice, count);
        int workItemCount = 0;
        for (int start = localEnd; start < count; start += handlesPerSlice)
        {
            int end = Math.Min(start + handlesPerSlice, count);
            BatchWorkItem workItem = _parallelWorkItems[workItemCount++];
            workItem.ConfigureCpuBatch(
                _cpuBackend,
                _cpuBatchHandles,
                _cpuBatchComponents,
                start,
                end);
            ThreadPool.UnsafeQueueUserWorkItem(static state => state.Run(), workItem, preferLocal: false);
        }

        Exception? firstFault = null;
        try
        {
            RunCpuBatchRange(_cpuBackend, _cpuBatchHandles, _cpuBatchComponents, 0, localEnd);
        }
        catch (Exception ex)
        {
            firstFault = ex;
        }

        for (int workItemIndex = 0; workItemIndex < workItemCount; ++workItemIndex)
        {
            BatchWorkItem workItem = _parallelWorkItems[workItemIndex];
            workItem.Wait();
            firstFault ??= workItem.Fault;
        }

        ClearCpuBatchComponents(count);
        if (firstFault is not null)
            throw new AggregateException("Physics chain CPU batch update failed.", firstFault);
    }

    private void ClearCpuBatchComponents(int count)
    {
        for (int batchIndex = 0; batchIndex < count; ++batchIndex)
            _cpuBatchComponents[batchIndex] = null!;
    }

    private static bool RunCpuBatchRange(
        PhysicsChainCpuBackend backend,
        PhysicsChainArenaHandle[] handles,
        PhysicsChainComponent[] components,
        int startInclusive,
        int endExclusive)
    {
        if (!backend.TryStepBatch(handles.AsSpan(startInclusive, endExclusive - startInclusive)))
            return false;

        for (int batchIndex = startInclusive; batchIndex < endExclusive; ++batchIndex)
        {
            components[batchIndex].MarkPreparedCpuBatchSolved();
        }
        return true;
    }

    private static void RunPrepareRange(
        PhysicsChainComponent[] components,
        bool[] eligible,
        bool[] results,
        Exception?[] faults,
        int startInclusive,
        int endExclusive)
    {
        for (int componentIndex = startInclusive; componentIndex < endExclusive; ++componentIndex)
        {
            if (!eligible[componentIndex])
                continue;
            PhysicsChainComponent component = components[componentIndex];
            try
            {
                results[componentIndex] = component.BeginWorldLateTickParallelPreparation();
            }
            catch (Exception ex)
            {
                component.AbortWorldLateTick();
                faults[componentIndex] = ex;
                results[componentIndex] = false;
            }
        }
    }

    private static void RunPreparedRange(List<PhysicsChainComponent> components, int startInclusive, int endExclusive)
    {
        for (int i = startInclusive; i < endExclusive; ++i)
            components[i].SolveWorldLateTick();
    }
}
