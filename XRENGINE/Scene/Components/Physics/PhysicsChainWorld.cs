using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace XREngine.Components;

/// <summary>
/// Owns scheduling for every physics chain in one runtime world. Components
/// remain authoring facades; the world contributes only one callback to each
/// required engine tick phase regardless of chain count.
/// </summary>
public sealed class PhysicsChainWorld
{
    private enum CommandKind : byte
    {
        Add,
        Remove,
    }

    private readonly record struct StructuralCommand(CommandKind Kind, PhysicsChainComponent Component);

    private struct RuntimeSlot
    {
        public PhysicsChainComponent? Component;
        public uint Generation;
        public int DenseIndex;
    }

    private sealed class BatchWorkItem
    {
        private readonly ManualResetEventSlim _completed = new(initialState: true);
        private List<PhysicsChainComponent>? _components;
        private int _startInclusive;
        private int _endExclusive;

        public Exception? Fault { get; private set; }

        public void Configure(List<PhysicsChainComponent> components, int startInclusive, int endExclusive)
        {
            _components = components;
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

    private readonly ConcurrentQueue<StructuralCommand> _commands = [];
    private readonly Dictionary<PhysicsChainComponent, int> _slotByComponent =
        new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
    private readonly List<RuntimeSlot> _slots = [];
    private readonly List<int> _liveSlots = [];
    private readonly Stack<int> _freeSlots = [];
    private readonly List<PhysicsChainComponent> _parallelComponents = [];
    private int[] _parallelRangeEnds = [];
    private BatchWorkItem[] _parallelWorkItems = [];

    /// <summary>
    /// Approximate active particle/collider work allowed at full automatic
    /// quality before the world selects a lower explicit cadence.
    /// </summary>
    public int AutomaticParticleStepBudget { get; set; } = 100_000;

    private PhysicsChainWorld(IRuntimeWorldContext world)
    {
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

        scheduler._commands.Enqueue(new StructuralCommand(CommandKind.Add, component));
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

        scheduler._commands.Enqueue(new StructuralCommand(CommandKind.Remove, component));
    }

    internal static bool TryGet(IRuntimeWorldContext world, out PhysicsChainWorld? scheduler)
    {
        using (RegistryLock.EnterScope())
            return Worlds.TryGetValue(world, out scheduler);
    }

    private void FixedTick()
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
        DrainStructuralCommands();
        PhysicsChainComponent.AdvancePreparedColliderFrame();
        AssignQualityTiers();
        _parallelComponents.Clear();

        ExceptionDispatchInfo? firstFault = null;
        for (int i = 0; i < _liveSlots.Count; ++i)
        {
            PhysicsChainComponent? component = _slots[_liveSlots[i]].Component;
            if (component is not { IsActiveInHierarchy: true })
                continue;

            try
            {
                if (component.BeginWorldLateTick())
                    _parallelComponents.Add(component);
            }
            catch (Exception ex)
            {
                component.AbortWorldLateTick();
                firstFault ??= ExceptionDispatchInfo.Capture(ex);
            }
        }

        if (_parallelComponents.Count > 0)
        {
            try
            {
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

        firstFault?.Throw();
    }

    private void AssignQualityTiers()
    {
        long automaticWork = 0L;
        for (int i = 0; i < _liveSlots.Count; ++i)
        {
            PhysicsChainComponent? component = _slots[_liveSlots[i]].Component;
            if (component is { IsActiveInHierarchy: true, QualityTier: PhysicsChainQualityTier.Automatic })
                automaticWork += component.EstimatedWorldWork;
        }

        long budget = Math.Max(AutomaticParticleStepBudget, 1);
        PhysicsChainQualityTier automaticTier = automaticWork switch
        {
            <= 0 => PhysicsChainQualityTier.Strict,
            _ when automaticWork <= budget => PhysicsChainQualityTier.Strict,
            _ when automaticWork <= budget * 2L => PhysicsChainQualityTier.Hz30,
            _ when automaticWork <= budget * 4L => PhysicsChainQualityTier.Hz15,
            _ => PhysicsChainQualityTier.Hz7_5,
        };

        for (int i = 0; i < _liveSlots.Count; ++i)
        {
            PhysicsChainComponent? component = _slots[_liveSlots[i]].Component;
            if (component is not { IsActiveInHierarchy: true })
                continue;

            PhysicsChainQualityTier tier = component.QualityTier == PhysicsChainQualityTier.Automatic
                ? automaticTier
                : component.QualityTier;
            component.SetEffectiveQualityTier(tier);
        }
    }

    private void DrainStructuralCommands()
    {
        while (_commands.TryDequeue(out StructuralCommand command))
        {
            if (command.Kind == CommandKind.Add)
                AddComponent(command.Component);
            else
                RemoveComponent(command.Component);
        }
    }

    private void AddComponent(PhysicsChainComponent component)
    {
        if (_slotByComponent.TryGetValue(component, out int existingSlot))
        {
            RuntimeSlot existing = _slots[existingSlot];
            component.SetRuntimeHandle(new PhysicsChainRuntimeHandle(existingSlot, existing.Generation));
            return;
        }

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
        _slots[slotIndex] = slot;
        _liveSlots.Add(slotIndex);
        _slotByComponent.Add(component, slotIndex);
        component.SetRuntimeHandle(new PhysicsChainRuntimeHandle(slotIndex, slot.Generation));
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
        slot.Component = null;
        slot.DenseIndex = -1;
        slot.Generation = NextGeneration(slot.Generation);
        _slots[slotIndex] = slot;
        _freeSlots.Push(slotIndex);
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

    private static void RunPreparedRange(List<PhysicsChainComponent> components, int startInclusive, int endExclusive)
    {
        for (int i = startInclusive; i < endExclusive; ++i)
            components[i].SolveWorldLateTick();
    }
}
