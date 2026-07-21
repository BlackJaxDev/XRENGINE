using System.Collections.Concurrent;

namespace XREngine.Components;

public sealed partial class PhysicsChainWorld
{
    private readonly record struct DynamicCommandKey(
        PhysicsChainComponent Component,
        PhysicsChainWorldDynamicCommandKind Kind);

    private readonly record struct DynamicCommand(
        DynamicCommandKey Key,
        long Version);

    private readonly record struct RetiredArenaRegistration(
        PhysicsChainArenaHandle Instance,
        PhysicsChainArenaHandle State,
        PhysicsChainArenaHandle Output,
        long RetirementFrame);

    private readonly ConcurrentQueue<DynamicCommand> _dynamicCommands = [];
    private readonly ConcurrentDictionary<DynamicCommandKey, long> _latestDynamicCommandVersion = [];
    private readonly PhysicsChainSlotArena<PhysicsChainInstance> _instanceArena = new();
    private readonly PhysicsChainSlotArena<PhysicsChainState> _stateArena = new();
    private readonly PhysicsChainSlotArena<PhysicsChainOutput> _outputArena = new();
    private readonly List<RetiredArenaRegistration> _retiredArenaRegistrations = [];
    private long _activeFrame;
    private long _appliedStructuralCommands;
    private long _appliedDynamicCommands;

    public PhysicsChainWorldLifecycleSnapshot GetLifecycleSnapshot()
        => new(
            _activeFrame,
            _instanceArena.LiveCount,
            _stateArena.LiveCount,
            _outputArena.LiveCount,
            _retiredArenaRegistrations.Count,
            _appliedStructuralCommands,
            _appliedDynamicCommands);

    public bool TryGetRegistration(
        PhysicsChainRuntimeHandle runtimeHandle,
        out PhysicsChainInstance instance,
        out PhysicsChainOutput output)
    {
        instance = default;
        output = default;
        if (!runtimeHandle.IsValid || (uint)runtimeHandle.Slot >= (uint)_slots.Count)
            return false;
        RuntimeSlot slot = _slots[runtimeHandle.Slot];
        if (slot.Generation != runtimeHandle.Generation || slot.Component is null)
            return false;
        return _instanceArena.TryGet(slot.InstanceArenaHandle, out instance)
            && _outputArena.TryGet(slot.OutputArenaHandle, out output);
    }

    public bool TryGetRegistration(
        PhysicsChainRuntimeHandle runtimeHandle,
        out PhysicsChainInstance instance,
        out PhysicsChainState state,
        out PhysicsChainOutput output)
    {
        state = default;
        if (!TryGetRegistration(runtimeHandle, out instance, out output))
            return false;
        RuntimeSlot slot = _slots[runtimeHandle.Slot];
        return _stateArena.TryGet(slot.StateArenaHandle, out state);
    }

    internal void HoldOutputHistory(PhysicsChainRuntimeHandle runtimeHandle)
    {
        if (!runtimeHandle.IsValid || (uint)runtimeHandle.Slot >= (uint)_slots.Count)
            return;
        RuntimeSlot slot = _slots[runtimeHandle.Slot];
        if (slot.Generation != runtimeHandle.Generation || slot.Component is null
            || !_outputArena.TryGet(slot.OutputArenaHandle, out PhysicsChainOutput output))
            return;
        output.ResetHistory();
        _outputArena.TrySet(slot.OutputArenaHandle, output);
    }

    public static void RequestStructural(
        PhysicsChainComponent component,
        PhysicsChainWorldCommandKind kind)
    {
        ArgumentNullException.ThrowIfNull(component);
        if (kind is PhysicsChainWorldCommandKind.Add or PhysicsChainWorldCommandKind.Remove)
            throw new ArgumentOutOfRangeException(nameof(kind), "Use Register or Unregister for add/remove intents.");
        if (component.World is not IRuntimeWorldContext world || !TryGet(world, out PhysicsChainWorld? scheduler) || scheduler is null)
            return;
        scheduler.EnqueueStructuralCommand((CommandKind)kind, component);
    }

    public static void NotifyDynamic(
        PhysicsChainComponent component,
        PhysicsChainWorldDynamicCommandKind kind)
    {
        ArgumentNullException.ThrowIfNull(component);
        if (component.World is not IRuntimeWorldContext world || !TryGet(world, out PhysicsChainWorld? scheduler) || scheduler is null)
            return;
        scheduler.EnqueueDynamicCommand(component, kind);
    }

    private void EnqueueDynamicCommand(PhysicsChainComponent component, PhysicsChainWorldDynamicCommandKind kind)
    {
        long version = Interlocked.Increment(ref _nextCommandVersion);
        var key = new DynamicCommandKey(component, kind);
        _latestDynamicCommandVersion.AddOrUpdate(
            key,
            static (_, candidate) => candidate,
            static (_, current, candidate) => Math.Max(current, candidate),
            version);
        _dynamicCommands.Enqueue(new DynamicCommand(key, version));
    }

    private void DrainDynamicCommands()
    {
        while (_dynamicCommands.TryDequeue(out DynamicCommand command))
        {
            if (!_latestDynamicCommandVersion.TryGetValue(command.Key, out long latest)
                || latest != command.Version)
                continue;
            if (_slotByComponent.ContainsKey(command.Key.Component))
            {
                command.Key.Component.ApplyWorldDynamicCommand(command.Key.Kind);
                ++_appliedDynamicCommands;
            }
            ((ICollection<KeyValuePair<DynamicCommandKey, long>>)_latestDynamicCommandVersion).Remove(
                new KeyValuePair<DynamicCommandKey, long>(command.Key, command.Version));
        }
    }

    private void AllocateRegistrationArenas(ref RuntimeSlot slot, PhysicsChainRuntimeHandle runtimeHandle)
    {
        int particleCount = Math.Max(slot.Component?.RuntimeParticleCount ?? 0, 1);
        var particleSlice = new PhysicsChainArenaSlice(0, particleCount, 1u);
        PhysicsChainArenaHandle stateHandle = _stateArena.Allocate(new PhysicsChainState
        {
            CurrentParticles = particleSlice,
            PreviousParticles = particleSlice,
            VelocityAndInertia = particleSlice,
            StateGeneration = 1u,
        });
        PhysicsChainArenaHandle outputHandle = _outputArena.Allocate(new PhysicsChainOutput
        {
            InstanceHandle = runtimeHandle,
            CurrentPalette = particleSlice,
            PreviousPalette = particleSlice,
            BoundsSlot = stateHandle,
            OutputGeneration = 1u,
            BackendStatus = PhysicsChainBackendStatus.Ready,
        });
        PhysicsChainTemplate template = slot.Component!.GetOrCreateRuntimeTemplate(this);
        PhysicsChainArenaHandle instanceHandle = _instanceArena.Allocate(new PhysicsChainInstance
        {
            RuntimeHandle = runtimeHandle,
            TemplateId = template.StableId,
            RootInputSlice = new PhysicsChainArenaSlice(0, Math.Max(template.Trees.Length, 1), 1u),
            StateSlice = particleSlice,
            PaletteSlice = particleSlice,
            BoundsSlot = stateHandle,
            RequestedQuality = slot.Component.EffectiveQualityPolicy,
            EffectiveQuality = slot.Component.EffectiveQualityPolicy,
            Flags = PhysicsChainInstanceFlags.Enabled | PhysicsChainInstanceFlags.Visible,
            Generation = runtimeHandle.Generation,
        });
        slot.InstanceArenaHandle = instanceHandle;
        slot.StateArenaHandle = stateHandle;
        slot.OutputArenaHandle = outputHandle;
    }

    private void RetireRegistrationArenas(in RuntimeSlot slot)
    {
        if (!slot.InstanceArenaHandle.IsValid)
            return;
        _retiredArenaRegistrations.Add(new RetiredArenaRegistration(
            slot.InstanceArenaHandle,
            slot.StateArenaHandle,
            slot.OutputArenaHandle,
            _activeFrame));
    }

    private void ReclaimRetiredArenas()
    {
        int write = 0;
        for (int index = 0; index < _retiredArenaRegistrations.Count; ++index)
        {
            RetiredArenaRegistration retired = _retiredArenaRegistrations[index];
            if (retired.RetirementFrame < _activeFrame)
            {
                _instanceArena.Free(retired.Instance);
                _stateArena.Free(retired.State);
                _outputArena.Free(retired.Output);
                continue;
            }
            _retiredArenaRegistrations[write++] = retired;
        }
        if (write < _retiredArenaRegistrations.Count)
            _retiredArenaRegistrations.RemoveRange(write, _retiredArenaRegistrations.Count - write);
    }

    private void ApplyStructuralMutation(CommandKind kind, PhysicsChainComponent component)
    {
        if (!_slotByComponent.TryGetValue(component, out int slotIndex))
        {
            AddComponent(component);
            slotIndex = _slotByComponent[component];
        }
        RuntimeSlot slot = _slots[slotIndex];
        component.ApplyWorldStructuralCommand((PhysicsChainWorldCommandKind)kind, this, _cpuBackend);
        PhysicsChainTemplate template = component.GetOrCreateRuntimeTemplate(this);
        int particleCount = Math.Max(component.RuntimeParticleCount, 1);
        var particleSlice = new PhysicsChainArenaSlice(0, particleCount, 1u);
        if (_instanceArena.TryGet(slot.InstanceArenaHandle, out PhysicsChainInstance instance))
        {
            instance.TemplateId = template.StableId;
            instance.RootInputSlice = new PhysicsChainArenaSlice(0, Math.Max(template.Trees.Length, 1), 1u);
            if (kind is CommandKind.Retemplate or CommandKind.Resize)
            {
                instance.StateSlice = particleSlice;
                instance.PaletteSlice = particleSlice;
            }
            _instanceArena.TrySet(slot.InstanceArenaHandle, instance);
        }
        if (_stateArena.TryGet(slot.StateArenaHandle, out PhysicsChainState state))
        {
            if (kind is CommandKind.Retemplate or CommandKind.Resize)
            {
                state.CurrentParticles = particleSlice;
                state.VelocityAndInertia = particleSlice;
                state.StateGeneration = NextGeneration(state.StateGeneration);
            }
            state.ResetHistory();
            _stateArena.TrySet(slot.StateArenaHandle, state);
        }
        if (_outputArena.TryGet(slot.OutputArenaHandle, out PhysicsChainOutput output))
        {
            if (kind is CommandKind.Retemplate or CommandKind.Resize)
                output.CurrentPalette = particleSlice;
            output.ResetHistory();
            output.OutputGeneration = NextGeneration(output.OutputGeneration);
            _outputArena.TrySet(slot.OutputArenaHandle, output);
        }
    }
}
