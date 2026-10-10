using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using XREngine.Execution;

namespace XREngine.Components;

/// <summary>
/// Owns scheduling for every physics chain in one runtime world. Components
/// remain authoring facades; the world contributes only one callback to each
/// required engine tick phase regardless of chain count.
/// </summary>
public sealed partial class PhysicsChainWorld : IDisposable
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

    private readonly record struct DeferredTransfer(
        PhysicsChainComponent Component,
        PhysicsChainWorld ExpectedOwner,
        PhysicsChainRuntimeHandle ExpectedHandle,
        PhysicsChainWorld Destination);

    private struct RuntimeSlot
    {
        public PhysicsChainComponent? Component;
        public PhysicsChainRuntimeGraph? Graph;
        public uint Generation;
        public int DenseIndex;
        public QualityRuntimeState QualityState;
        public bool WasSleeping;
        public ulong ObservedWakeCount;
        public PhysicsChainArenaHandle InstanceArenaHandle;
        public PhysicsChainArenaHandle StateArenaHandle;
        public PhysicsChainArenaHandle OutputArenaHandle;
    }

    private sealed class BatchWorkItem(PhysicsChainWorld owner) : IDisposable
    {
        private readonly PhysicsChainWorld _owner = owner;
        private readonly ManualResetEventSlim _completed = new(initialState: true);
        private readonly Lock _completionGate = new();
        private PhysicsChainWorld? _outerTickContext;
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

        public void Configure(PhysicsChainWorld owner, List<PhysicsChainComponent> components, int startInclusive, int endExclusive)
        {
            _outerTickContext = s_outerTickWorld ?? owner;
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
            ResetCompletion();
        }

        public void ConfigureCpuBatch(
            PhysicsChainWorld owner,
            PhysicsChainCpuBackend backend,
            PhysicsChainArenaHandle[] handles,
            PhysicsChainComponent[] components,
            int startInclusive,
            int endExclusive)
        {
            _outerTickContext = s_outerTickWorld ?? owner;
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
            ResetCompletion();
        }

        public void ConfigurePrepare(
            PhysicsChainWorld owner,
            PhysicsChainComponent[] components,
            bool[] eligible,
            bool[] results,
            Exception?[] faults,
            int startInclusive,
            int endExclusive)
        {
            _outerTickContext = s_outerTickWorld ?? owner;
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
            ResetCompletion();
        }

        private void ResetCompletion()
        {
            using (_completionGate.EnterScope())
                _completed.Reset();
        }

        public void Run()
        {
            // The outer-tick context keeps scene-mutation and readback admission rules.
            // The executing-worker owner stops a worker from waiting on its own world.
            PhysicsChainWorld? previousContext = s_outerTickWorld;
            PhysicsChainWorld? previousOwner = s_executingWorkerWorld;
            s_outerTickWorld = _outerTickContext;
            s_executingWorkerWorld = _owner;
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
                s_outerTickWorld = previousContext;
                s_executingWorkerWorld = previousOwner;
                using (_completionGate.EnterScope())
                    _completed.Set();
            }
        }

        public void Wait()
            => _completed.Wait();

        public void Dispose()
        {
            // Wait can observe the signal before Set itself returns. Serialize
            // event destruction with that final worker operation as well.
            using (_completionGate.EnterScope())
                _completed.Dispose();
            _components = null;
            _cpuBackend = null;
            _cpuHandles = null;
            _cpuComponents = null;
            _prepareComponents = null;
            _prepareEligible = null;
            _prepareResults = null;
            _prepareFaults = null;
            Fault = null;
        }
    }

    private static readonly Lock RegistryLock = new();

    private static readonly ConditionalWeakTable<PhysicsChainComponent, PhysicsChainRuntimeGraph> PendingRuntimeGraphs = new();
    private static readonly ConditionalWeakTable<IRuntimeWorldContext, PhysicsChainWorld> Worlds = new();
    private static readonly ConditionalWeakTable<IRuntimeWorldContext, object> ReleasedWorlds = new();
    private static readonly object ReleasedWorld = new();

    // Fixed updates run on their own timer thread and can overlap normal/late updates.
    // All three phases touch the same slot registries and component simulation state, so
    // serialize them at the world boundary instead of allowing concurrent collection access.
    private readonly Lock _tickGate = new();
    [ThreadStatic] private static PhysicsChainWorld? s_outerTickWorld;
    [ThreadStatic] private static PhysicsChainWorld? s_executingWorkerWorld;
    [ThreadStatic] private static bool s_drainingTransfers;
    [ThreadStatic] private static Queue<PhysicsChainWorld>? s_transferDrainQueue;
    [ThreadStatic] private static HashSet<PhysicsChainWorld>? s_transferDrainSet;
    private readonly ConcurrentQueue<DeferredTransfer> _deferredTransfers = [];
    private readonly IRuntimeWorldContext _world;
    private readonly Lock _commandGate = new();
    private int _disposeRequested;
    private bool _disposed;
    private int _tickDepth;
    private bool _attachingCallbacks;
    private volatile bool _ownerDisposalPending;
    private volatile bool _deferredOwnerDisposal;
    private readonly ConcurrentQueue<StructuralCommand> _commands = [];
    private readonly ConcurrentDictionary<PhysicsChainComponent, long> _latestCommandVersion =
        new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
    private long _nextCommandVersion;
    private readonly Dictionary<PhysicsChainComponent, int> _slotByComponent =
        new(System.Collections.Generic.ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Binds the first rest graph before structural registration is applied.
    /// The weak key keeps a disabled component's rest pose without retaining it.
    /// </summary>
    internal static PhysicsChainRuntimeGraph BindPendingRuntimeGraph(PhysicsChainComponent component)
    {
        PhysicsChainRuntimeGraph graph = PendingRuntimeGraphs.GetValue(
            component, static _ => new PhysicsChainRuntimeGraph());
        component.BindRuntimeGraph(graph);
        return graph;
    }
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
        if (world is RuntimeWorld runtimeWorld)
            runtimeWorld.Disposing += OnWorldDisposing;
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

    internal bool TryCaptureReadbackSource(PhysicsChainRuntimeHandle handle,
        out PhysicsChainComponent? component, out long generation, out PhysicsChainReadbackRejection rejection)
    {
        rejection = PhysicsChainReadbackRejection.InvalidInstance;
        if (ReferenceEquals(s_outerTickWorld, this))
            return CaptureReadbackSourceAtBoundary(handle, out component, out generation, out rejection);
        if (s_outerTickWorld is not null)
        {
            if (!_tickGate.TryEnter())
            {
                component = null;
                generation = 0L;
                rejection = PhysicsChainReadbackRejection.SourceBusy;
                return false;
            }
            try { return CaptureReadbackSourceAtBoundary(handle, out component, out generation, out rejection); }
            finally { _tickGate.Exit(); }
        }
        using (_tickGate.EnterScope())
            return CaptureReadbackSourceAtBoundary(handle, out component, out generation, out rejection);
    }

    private bool CaptureReadbackSourceAtBoundary(PhysicsChainRuntimeHandle handle,
        out PhysicsChainComponent? component, out long generation, out PhysicsChainReadbackRejection rejection)
    {
        rejection = PhysicsChainReadbackRejection.InvalidInstance;
        generation = 0L;
        if (!TryResolveRuntimeHandle(handle, out component) || component is null)
            return false;
        generation = component.ReadbackSourceGeneration;
        if (!component.MatchesReadbackSource(this, handle, generation))
            return false;
        rejection = PhysicsChainReadbackRejection.None;
        return true;
    }

    /// <summary>
    /// Registers a component with the scheduler for its current world.
    /// </summary>
    public static void Register(PhysicsChainComponent component)
    {
        ArgumentNullException.ThrowIfNull(component);
        if (s_activeWorldMutation is { } mutation)
        {
            mutation.AddParticipant(component);
            return;
        }
        if (component.IsWorldMutationPending)
            return;
        IRuntimeWorldContext? world = component.World;
        if (world is null)
            return;

        PhysicsChainWorld scheduler;
        bool created = false;
        using (RegistryLock.EnterScope())
        {
            if (ReleasedWorlds.TryGetValue(world, out _) || world is RuntimeWorld { IsDisposing: true })
                return;
            if (!Worlds.TryGetValue(world, out scheduler!))
            {
                scheduler = new PhysicsChainWorld(world);
                Worlds.Add(world, scheduler);
                created = true;
            }
        }

        if (created)
            scheduler.AttachCallbacks();

        // Disposal can have started before the constructor subscribed to the event.
        // Never enter the tick gate while holding the registry lock.
        if (world is RuntimeWorld { IsDisposing: true })
        {
            // Only the creator disposes here. An existing scheduler is released by
            // the disposal event, and this caller can hold another world's gate.
            if (created)
                scheduler.Dispose();
            return;
        }

        component.CaptureRuntimeBinding(out PhysicsChainWorld? previousOwner, out PhysicsChainRuntimeHandle previousHandle);
        if (previousOwner is not null && !ReferenceEquals(previousOwner, scheduler))
        {
            var transfer = new DeferredTransfer(component, previousOwner, previousHandle, scheduler);
            if (s_outerTickWorld is { } outerTick)
            {
                outerTick._deferredTransfers.Enqueue(transfer);
                return;
            }
            CompleteDeferredTransfer(transfer);
            return;
        }
        scheduler.EnqueueStructuralCommand(CommandKind.Add, component);
    }

    /// <summary>
    /// Removes the registration from the world that owns it.
    /// </summary>
    public static void Unregister(PhysicsChainComponent component)
    {
        ArgumentNullException.ThrowIfNull(component);
        PhysicsChainWorld? owner = component.CaptureRuntimeOwner();
        if (owner is not null)
        {
            owner.EnqueueStructuralCommand(CommandKind.Remove, component);
            return;
        }

        // A pending add can have no owner yet. Cancel it in its source world.
        if (component.World is not IRuntimeWorldContext world)
            return;
        using (RegistryLock.EnterScope())
        {
            if (Worlds.TryGetValue(world, out PhysicsChainWorld? scheduler))
                scheduler.EnqueueStructuralCommand(CommandKind.Remove, component);
        }
    }

    internal static bool TryGet(IRuntimeWorldContext world, out PhysicsChainWorld? scheduler)
    {
        using (RegistryLock.EnterScope())
            return Worlds.TryGetValue(world, out scheduler);
    }

    private void FixedTick()
    {
        PhysicsChainWorld? previousContext = s_outerTickWorld;
        bool disposeOwner = false;
        try
        {
            using var admission = new TickAdmissionScope(rejectNestedTick: true);
            using (_tickGate.EnterScope())
            {
                if (IsDisposed)
                    return;
                ++_tickDepth;
                s_outerTickWorld ??= this;
                try { FixedTickExclusive(); }
                finally
                {
                    s_outerTickWorld = previousContext;
                    disposeOwner = CompleteTick();
                }
            }
        }
        finally { FinishOuterTick(previousContext, disposeOwner); }
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
        PhysicsChainWorld? previousContext = s_outerTickWorld;
        bool disposeOwner = false;
        try
        {
            using var admission = new TickAdmissionScope(rejectNestedTick: true);
            using (_tickGate.EnterScope())
            {
                if (IsDisposed)
                    return;
                ++_tickDepth;
                s_outerTickWorld ??= this;
                try { UpdateTickExclusive(); }
                finally
                {
                    s_outerTickWorld = previousContext;
                    disposeOwner = CompleteTick();
                }
            }
        }
        finally { FinishOuterTick(previousContext, disposeOwner); }
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
        bool observe = RuntimeWorldTickTelemetry.Enabled;
        long gateStart = observe ? Stopwatch.GetTimestamp() : 0L;
        PhysicsChainWorld? previousContext = s_outerTickWorld;
        bool disposeOwner = false;
        try
        {
            using var admission = new TickAdmissionScope(rejectNestedTick: true);
            using (_tickGate.EnterScope())
            {
                if (IsDisposed)
                    return;
                ++_tickDepth;
                long bodyStart = observe ? Stopwatch.GetTimestamp() : 0L;
                if (observe)
                    Interlocked.Add(ref _lateTickGateWaitTicks, bodyStart - gateStart);
                PhysicsChainWorld? previous = s_activeLateTick;
                s_outerTickWorld ??= this;
                if (observe)
                    s_activeLateTick = this;
                try
                {
                    LateTickExclusive();
                }
                finally
                {
                    s_outerTickWorld = previousContext;
                    if (observe)
                    {
                        s_activeLateTick = previous;
                        Interlocked.Add(ref _lateTickBodyTicks, Stopwatch.GetTimestamp() - bodyStart);
                        Interlocked.Increment(ref _lateTickCount);
                    }
                    disposeOwner = CompleteTick();
                }
            }
        }
        finally { FinishOuterTick(previousContext, disposeOwner); }
    }

    private void LateTickExclusive()
    {
        bool observe = RuntimeWorldTickTelemetry.Enabled;
        long stageStart = observe ? Stopwatch.GetTimestamp() : 0L;
        DrainStructuralCommands();
        if (observe)
            Interlocked.Add(ref _lateStructuralBoundaryTicks, Stopwatch.GetTimestamp() - stageStart);
        PhysicsChainComponent.AdvancePreparedColliderFrame();
        long qualityStart = observe ? Stopwatch.GetTimestamp() : 0L;
        AssignQualityTiers();
        long dependencyStart = observe ? Stopwatch.GetTimestamp() : 0L;
        if (observe)
            Interlocked.Add(ref _lateQualityAssignmentTicks, dependencyStart - qualityStart);
        PrepareGpuRestInputPhase();
        if (observe)
        {
            long qualityEnd = Stopwatch.GetTimestamp();
            Interlocked.Add(ref _lateGpuRestDependencyPreparationTicks, qualityEnd - dependencyStart);
            Interlocked.Add(ref _lateQualityBudgetTicks, qualityEnd - qualityStart);
        }
        long inputGatherStart = observe ? Stopwatch.GetTimestamp() : 0L;
        CaptureGpuRestInputsForWorldPhase();
        if (observe)
            Interlocked.Add(ref _lateGpuWorldInputGatherTicks, Stopwatch.GetTimestamp() - inputGatherStart);
        _parallelComponents.Clear();
        if (observe)
        {
            long now = Stopwatch.GetTimestamp();
            Interlocked.Add(ref _lateBoundaryTicks, now - stageStart);
            stageStart = now;
        }

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
        int parallelPrepareCount = 0;
        for (int prepareIndex = 0; prepareIndex < prepareCount; ++prepareIndex)
        {
            if (!_prepareEligible[prepareIndex])
                continue;
            try
            {
                _prepareComponents[prepareIndex].PrepareWorldCollidersForParallelInputGather();
                ++parallelPrepareCount;
            }
            catch (Exception ex)
            {
                _prepareComponents[prepareIndex].AbortWorldLateTick();
                _prepareEligible[prepareIndex] = false;
                _prepareFaults[prepareIndex] = ex;
            }
        }

        Exception? prepareFault = parallelPrepareCount > 0
            ? ExecutePrepareParallel(prepareCount)
            : null;
        firstFault = prepareFault is not null
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

        if (observe)
        {
            long now = Stopwatch.GetTimestamp();
            Interlocked.Add(ref _lateComponentPreparationTicks, now - stageStart);
            stageStart = now;
        }

        SynchronizeCpuSharedColliderSets();
        if (_parallelComponents.Count > 0)
        {
            int pendingStage = 1;
            try
            {
                ExecutePreparedCpuBatch(_parallelComponents);
                if (observe)
                {
                    long now = Stopwatch.GetTimestamp();
                    Interlocked.Add(ref _lateCpuBatchTicks, now - stageStart);
                    stageStart = now;
                }
                pendingStage = 2;
                ExecutePreparedParallel(_parallelComponents);
                if (observe)
                {
                    long now = Stopwatch.GetTimestamp();
                    Interlocked.Add(ref _lateParallelSolveTicks, now - stageStart);
                    stageStart = now;
                }
            }
            catch (Exception ex)
            {
                if (observe)
                {
                    long now = Stopwatch.GetTimestamp();
                    if (pendingStage == 1)
                        Interlocked.Add(ref _lateCpuBatchTicks, now - stageStart);
                    else
                        Interlocked.Add(ref _lateParallelSolveTicks, now - stageStart);
                    stageStart = now;
                }
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
            if (observe)
            {
                long now = Stopwatch.GetTimestamp();
                Interlocked.Add(ref _latePublicationTicks, now - stageStart);
                stageStart = now;
            }
        }
        else if (observe)
        {
            long now = Stopwatch.GetTimestamp();
            Interlocked.Add(ref _lateCpuBatchTicks, now - stageStart);
            stageStart = now;
        }

        PublishActivityDiagnostics();
        ++_activeFrame;
        if (observe)
            Interlocked.Add(ref _lateDiagnosticsTicks, Stopwatch.GetTimestamp() - stageStart);
        firstFault?.Throw();
    }

    private void AssignQualityTiers()
    {
        EvaluateQualityBudget();
    }

    private void EnqueueStructuralCommand(CommandKind kind, PhysicsChainComponent component)
    {
        using (_commandGate.EnterScope())
        {
            if (IsDisposed)
                return;
            long version = Interlocked.Increment(ref _nextCommandVersion);
            _latestCommandVersion.AddOrUpdate(component, version, (_, current) => Math.Max(current, version));
            _commands.Enqueue(new StructuralCommand(kind, component, version));
        }
    }

    /// <summary>
    /// Releases an exact old registration after its tick and worker jobs finish.
    /// </summary>
    private bool ReleaseForTransfer(in DeferredTransfer transfer)
    {
        PhysicsChainComponent component = transfer.Component;
        PhysicsChainWorld? previousContext = s_outerTickWorld;
        try
        {
            using var admission = new TickAdmissionScope(rejectNestedTick: false);
            using (_tickGate.EnterScope())
            {
                s_outerTickWorld ??= this;
                try
                {
                    using var bindingScope = component.RuntimeBindingSync.EnterScope();
                    component.CaptureRuntimeBinding(out PhysicsChainWorld? owner, out PhysicsChainRuntimeHandle handle);
                    if (!ReferenceEquals(owner, this)
                        || handle != transfer.ExpectedHandle
                        || component.IsDestroyed
                        || !component.IsActiveInHierarchy
                        || !ReferenceEquals(component.World, transfer.Destination._world))
                        return false;
                    if (!_slotByComponent.TryGetValue(component, out int slotIndex)
                        || slotIndex != handle.Slot
                        || !ReferenceEquals(_slots[slotIndex].Component, component)
                        || _slots[slotIndex].Generation != handle.Generation)
                        throw new InvalidOperationException("The physics chain transfer lost its source registration.");

                    RemoveComponent(component, registerCurrentWorld: false);
                    return true;
                }
                finally { s_outerTickWorld = previousContext; }
            }
        }
        finally { if (previousContext is null) DrainDeferredTransfers(); }
    }

    private static void CompleteDeferredTransfer(in DeferredTransfer transfer)
    {
        if (!transfer.ExpectedOwner.ReleaseForTransfer(transfer))
            return;
        PhysicsChainComponent component = transfer.Component;
        if (component.IsDestroyed || !component.IsActiveInHierarchy)
            return;
        if (ReferenceEquals(component.World, transfer.Destination._world))
            transfer.Destination.EnqueueStructuralCommand(CommandKind.Add, component);
        else
            Register(component);
    }

    private void DrainDeferredTransfers()
    {
        if (_deferredTransfers.IsEmpty)
            return;

        Queue<PhysicsChainWorld> queue = s_transferDrainQueue ??= new Queue<PhysicsChainWorld>();
        HashSet<PhysicsChainWorld> queued = s_transferDrainSet ??= new HashSet<PhysicsChainWorld>();
        if (queued.Add(this))
            queue.Enqueue(this);
        if (s_drainingTransfers)
            return;

        s_drainingTransfers = true;
        try
        {
            while (queue.TryDequeue(out PhysicsChainWorld? world))
            {
                if (world is null)
                    continue;
                queued.Remove(world);
                while (world._deferredTransfers.TryDequeue(out DeferredTransfer transfer))
                {
                    try { CompleteDeferredTransfer(transfer); }
                    catch (Exception ex)
                    {
                        Debug.PhysicsWarning($"[PhysicsChain] Deferred world transfer failed: {ex}");
                    }
                }
            }
        }
        finally
        {
            queue.Clear();
            queued.Clear();
            s_drainingTransfers = false;
        }
    }

    private void DrainStructuralCommands()
    {
        ReclaimRetiredArenas();
        while (_commands.TryDequeue(out StructuralCommand command))
        {
            if (!_latestCommandVersion.TryGetValue(command.Component, out long latestVersion)
                || latestVersion != command.Version)
            {
                if (command.Kind is not CommandKind.Add and not CommandKind.Remove
                    && !command.Component.IsDestroyed
                    && command.Component.IsActiveInHierarchy
                    && ReferenceEquals(command.Component.World, _world))
                    RetainDeferredStructuralMutation(command.Component, command.Kind);
                continue;
            }

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
        using var bindingScope = component.RuntimeBindingSync.EnterScope();
        if (component.IsWorldMutationPending)
            return;
        if (component.IsDestroyed
            || !ReferenceEquals(component.World, _world)
            || !component.IsActiveInHierarchy)
        {
            if (_slotByComponent.ContainsKey(component))
                RemoveComponent(component);
            _deferredStructuralMutations.Remove(component);
            return;
        }

        PhysicsChainWorld? owner = component.CaptureRuntimeOwner();
        if (owner is not null && !ReferenceEquals(owner, this))
            return;

        if (_slotByComponent.TryGetValue(component, out int existingSlot))
        {
            RuntimeSlot existing = _slots[existingSlot];
            if (!component.TryBindRuntimeOwner(this, new PhysicsChainRuntimeHandle(existingSlot, existing.Generation)))
                return;
            component.AttachCpuBackend(this, _cpuBackend);
            ReplayDeferredStructuralMutations(component);
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
            _gpuRestInputRanges.Add(null);
        }

        slot.Component = component;
        slot.Graph = BindPendingRuntimeGraph(component);
        slot.DenseIndex = _liveSlots.Count;
        slot.QualityState = CreateInitialQualityState(component);
        slot.WasSleeping = component.IsRuntimeSleeping;
        slot.ObservedWakeCount = component.WakeCount;
        float cadencePhase = PhysicsChainComponent.ComputeDeterministicQualityPhase(slotIndex, slot.Generation);
        if (slotIndex == _clocks.Count)
            _clocks.Add(default);
        CollectionsMarshal.AsSpan(_clocks)[slotIndex].Initialize(
            cadencePhase, component.EffectiveQualityPolicy.SimulationRateHz);
        var runtimeHandle = new PhysicsChainRuntimeHandle(slotIndex, slot.Generation);
        AllocateRegistrationArenas(ref slot, runtimeHandle);
        _slots[slotIndex] = slot;
        _liveSlots.Add(slotIndex);
        _slotByComponent.Add(component, slotIndex);
        MarkGpuRestOwnershipDirty();
        if (!component.TryBindRuntimeOwner(this, runtimeHandle))
            throw new InvalidOperationException("The physics chain world owner changed during registration.");
        component.AttachCpuBackend(this, _cpuBackend);
        ReplayDeferredStructuralMutations(component);
    }

    private void RemoveComponent(PhysicsChainComponent component, bool registerCurrentWorld = true)
    {
        using var bindingScope = component.RuntimeBindingSync.EnterScope();
        bool registerInCurrentWorld = false;
        if (!_slotByComponent.Remove(component, out int slotIndex))
        {
            _deferredStructuralMutations.Remove(component);
            return;
        }
        _deferredStructuralMutations.Remove(component);

        RuntimeSlot slot = _slots[slotIndex];
        PhysicsChainRuntimeHandle removedHandle = new(slotIndex, slot.Generation);
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
        slot.Graph = null;
        _clocks[slotIndex] = default;
        _gpuRestInputRanges[slotIndex]?.RigidCache.Clear();
        _gpuRestInputRanges[slotIndex] = null;
        MarkGpuRestOwnershipDirty();
        slot.DenseIndex = -1;
        slot.Generation = NextGeneration(slot.Generation);
        _slots[slotIndex] = slot;
        _freeSlots.Push(slotIndex);
        if (component.TryReleaseRuntimeOwner(this, removedHandle))
            registerInCurrentWorld = component.IsActiveInHierarchy
                && !component.IsDestroyed
                && component.World is not null
                && !ReferenceEquals(component.World, _world);

        if (registerCurrentWorld && registerInCurrentWorld)
            Register(component);
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
        int sliceCount = OperatingSystem.IsBrowser() || RuntimeWorkScheduler.IsCallerThread || JobManager.IsJobWorkerThread
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
        Exception? firstFault = null;
        for (int slice = 1; slice < sliceCount; ++slice)
        {
            int end = _parallelRangeEnds[slice];
            if (end <= start)
                continue;
            BatchWorkItem workItem = _parallelWorkItems[workItemCount];
            workItem.ConfigurePrepare(
                this,
                _prepareComponents,
                _prepareEligible,
                _prepareResults,
                _prepareFaults,
                start,
                end);
            try
            {
                if (!ThreadPool.UnsafeQueueUserWorkItem(static state => state.Run(), workItem, preferLocal: false))
                    throw new InvalidOperationException("A physics chain prepare worker could not start.");
                ++workItemCount;
            }
            catch (Exception ex)
            {
                firstFault = ex;
                break;
            }
            start = end;
        }

        try
        {
            if (firstFault is null)
                RunPrepareRange(_prepareComponents, _prepareEligible, _prepareResults, _prepareFaults, 0, localEnd);
        }
        catch (Exception ex)
        {
            firstFault ??= ex;
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
        if (sliceCount <= 1 || OperatingSystem.IsBrowser() || RuntimeWorkScheduler.IsCallerThread || JobManager.IsJobWorkerThread)
        {
            RunPreparedRange(components, 0, workCount);
            return;
        }

        EnsureParallelCapacity(sliceCount);
        BuildWeightedRanges(components, sliceCount);

        int start = 0;
        int workItemCount = 0;
        Exception? firstFault = null;
        for (int slice = 0; slice < sliceCount; ++slice)
        {
            int end = _parallelRangeEnds[slice];
            if (end <= start)
                continue;

            if (slice == 0)
            {
                try { RunPreparedRange(components, start, end); }
                catch (Exception ex) { firstFault = ex; }
            }
            else
            {
                BatchWorkItem workItem = _parallelWorkItems[workItemCount];
                workItem.Configure(this, components, start, end);
                try
                {
                    if (!ThreadPool.UnsafeQueueUserWorkItem(static state => state.Run(), workItem, preferLocal: false))
                        throw new InvalidOperationException("A physics chain solve worker could not start.");
                    ++workItemCount;
                }
                catch (Exception ex)
                {
                    firstFault ??= ex;
                    break;
                }
            }

            start = end;
        }

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
            _parallelWorkItems[i] = new BatchWorkItem(this);
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
        int sliceCount = OperatingSystem.IsBrowser() || RuntimeWorkScheduler.IsCallerThread || JobManager.IsJobWorkerThread
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
        Exception? firstFault = null;
        for (int start = localEnd; start < count; start += handlesPerSlice)
        {
            int end = Math.Min(start + handlesPerSlice, count);
            BatchWorkItem workItem = _parallelWorkItems[workItemCount];
            workItem.ConfigureCpuBatch(
                this,
                _cpuBackend,
                _cpuBatchHandles,
                _cpuBatchComponents,
                start,
                end);
            try
            {
                if (!ThreadPool.UnsafeQueueUserWorkItem(static state => state.Run(), workItem, preferLocal: false))
                    throw new InvalidOperationException("A physics chain CPU worker could not start.");
                ++workItemCount;
            }
            catch (Exception ex)
            {
                firstFault = ex;
                break;
            }
        }

        try
        {
            if (firstFault is null)
                RunCpuBatchRange(_cpuBackend, _cpuBatchHandles, _cpuBatchComponents, 0, localEnd);
        }
        catch (Exception ex)
        {
            firstFault ??= ex;
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
