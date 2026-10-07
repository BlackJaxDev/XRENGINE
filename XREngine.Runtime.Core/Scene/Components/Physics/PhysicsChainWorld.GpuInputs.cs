using System.Numerics;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using XREngine.Scene.Transforms;

namespace XREngine.Components;

public sealed partial class PhysicsChainWorld
{
    private readonly List<PhysicsChainGpuRestInputRange?> _gpuRestInputRanges = [];
    private readonly Dictionary<TransformBase, int> _gpuRestResetNodeOwners =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<TransformBase, int> _gpuRestResetNodeFirstOwnerSlots =
        new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<int> _gpuRestForcedCompatibilitySlots = [];
    private readonly HashSet<TransformBase> _gpuRestPrerequisiteMatrixTargets =
        new(ReferenceEqualityComparer.Instance);
    private readonly long[] _gpuRestCompatibilityCounts =
        new long[(int)PhysicsChainGpuRestInputCompatibilityReason.ColliderDependency + 1];
    private bool _gpuRestOwnershipDirty = true;
    private bool _gpuRestHasOpaqueInputDependencies;
    private ulong _gpuRestInputPhase;
    private long _rigidGpuRestInputCacheHits;
    private long _rigidGpuRestInputCacheMisses;
    private long _gpuRestOwnershipGeneration;

    private static bool ShouldSampleGpuRestInput(int slot, ulong phase)
        => RuntimeWorldTickTelemetry.Enabled &&
            (unchecked((ulong)slot + phase) & 31UL) == 0UL;

    private void RecordRigidGpuRestCaptureSample(in PhysicsChainRigidGpuRestCaptureSample sample)
    {
        Interlocked.Add(ref _rigidRestRootSampleTicks, sample.RootTicks);
        Interlocked.Add(ref _rigidRestRootSampleCount, sample.RootCount);
        Interlocked.Add(ref _rigidRestExpandSampleTicks, sample.ExpandTicks);
        Interlocked.Add(ref _rigidRestExpandSampleCount, sample.ExpandCount);
        Interlocked.Add(ref _rigidRestPublishSampleTicks, sample.PublishTicks);
        Interlocked.Add(ref _rigidRestPublishSampleCount, sample.PublishCount);
    }

    internal void MarkGpuRestOwnershipDirty()
    {
        _gpuRestOwnershipDirty = true;
        Interlocked.Increment(ref _gpuRestOwnershipGeneration);
    }

    internal void InvalidateRigidGpuRestInputCache(
        PhysicsChainRuntimeHandle handle, PhysicsChainComponent component)
    {
        using (_tickGate.EnterScope())
            if (TryResolveRuntimeHandle(handle, out PhysicsChainComponent? resident) &&
                ReferenceEquals(resident, component))
                _gpuRestInputRanges[handle.Slot]?.RigidCache.Clear();
    }

    /// <summary>Gets cumulative rigid rest input capture counts.</summary>
    public PhysicsChainRigidGpuRestInputCacheDiagnostics CaptureRigidGpuRestInputCacheDiagnostics()
    {
        using (_tickGate.EnterScope())
        {
            int blocked = 0;
            for (int index = 0; index < _gpuRestInputRanges.Count; index++)
                if (_gpuRestInputRanges[index]?.RigidCache.IsBlocked == true)
                    blocked++;
            return new(_rigidGpuRestInputCacheHits, _rigidGpuRestInputCacheMisses, blocked);
        }
    }

    private void PrepareGpuRestInputPhase()
    {
        ulong nextPhase = unchecked(_gpuRestInputPhase + 1UL);
        if (nextPhase == 0UL)
            nextPhase = 1UL;
        RefreshGpuRestOwnership();
        _gpuRestForcedCompatibilitySlots.Clear();
        _gpuRestPrerequisiteMatrixTargets.Clear();
        _gpuRestHasOpaqueInputDependencies = false;
        for (int index = 0; index < _liveSlots.Count; ++index)
        {
            int slot = _liveSlots[index];
            PhysicsChainComponent? component = _slots[slot].Component;
            if (component is not { IsActiveInHierarchy: true })
                continue;
            component.CollectGpuRestPrerequisiteMatrixTargets(_gpuRestPrerequisiteMatrixTargets);
            bool sample = ShouldSampleGpuRestInput(slot, nextPhase) &&
                component.CanCaptureGpuRestInputsInWorldPhase;
            long start = sample ? Stopwatch.GetTimestamp() : 0L;
            bool opaque;
            try { opaque = component.HasOpaqueGpuRestInputDependency(); }
            finally
            {
                if (sample)
                {
                    Interlocked.Add(ref _gpuRestOpaqueSampleTicks, Stopwatch.GetTimestamp() - start);
                    Interlocked.Increment(ref _gpuRestOpaqueSampleCount);
                }
            }
            if (opaque)
            {
                // A custom input can read a reset node outside its declared parent chain.
                // Keep all possible source owners on the same hierarchy refresh route.
                _gpuRestHasOpaqueInputDependencies = true;
                return;
            }
            start = sample ? Stopwatch.GetTimestamp() : 0L;
            try
            {
                component.MarkGpuRestInputDependencies(
                    _gpuRestResetNodeFirstOwnerSlots, _gpuRestForcedCompatibilitySlots, slot);
            }
            finally
            {
                if (sample)
                {
                    Interlocked.Add(ref _gpuRestOwnerMarkSampleTicks, Stopwatch.GetTimestamp() - start);
                    Interlocked.Increment(ref _gpuRestOwnerMarkSampleCount);
                }
            }
        }
    }

    /// <summary>Captures compatible rest inputs before any component changes a hierarchy.</summary>
    private void CaptureGpuRestInputsForWorldPhase()
    {
        _gpuRestInputPhase = unchecked(_gpuRestInputPhase + 1);
        if (_gpuRestInputPhase == 0)
        {
            _gpuRestInputPhase = 1;
            for (int index = 0; index < _gpuRestInputRanges.Count; ++index)
                if (_gpuRestInputRanges[index] is { } oldRange)
                    oldRange.ConsumedPhase = oldRange.CapturedPhase = 0;
        }

        for (int index = 0; index < _liveSlots.Count; ++index)
        {
            int slot = _liveSlots[index];
            RuntimeSlot resident = _slots[slot];
            PhysicsChainComponent? component = resident.Component;
            if (component is not { IsActiveInHierarchy: true } ||
                !component.CanCaptureGpuRestInputsInWorldPhase)
                continue;

            PhysicsChainRuntimeHandle handle = new(slot, resident.Generation);
            PhysicsChainGpuRestInputRange range = _gpuRestInputRanges[slot] ??=
                new PhysicsChainGpuRestInputRange();
            range.CapturedPhase = 0;
            range.CaptureFault = null;
            try
            {
                if (!TryCaptureGpuRestInputs(handle, component, range))
                    continue;
                range.CapturedHandle = handle;
                range.CapturedPhase = _gpuRestInputPhase;
            }
            catch (Exception ex)
            {
                // Report this fault at the component's normal serial tick boundary.
                range.CaptureFault = ExceptionDispatchInfo.Capture(ex);
                range.CapturedHandle = handle;
                range.CapturedPhase = _gpuRestInputPhase;
            }
        }
    }

    private void RefreshGpuRestOwnership()
    {
        if (!_gpuRestOwnershipDirty)
            return;

        _gpuRestResetNodeOwners.Clear();
        _gpuRestResetNodeFirstOwnerSlots.Clear();
        for (int i = 0; i < _liveSlots.Count; ++i)
        {
            int slot = _liveSlots[i];
            _slots[slot].Component?.CountGpuRestResetNodes(
                _gpuRestResetNodeOwners, _gpuRestResetNodeFirstOwnerSlots, slot);
        }
        _gpuRestOwnershipDirty = false;
    }

    /// <summary>Gets the number of GPU inputs that used one compatibility reason.</summary>
    public long GetGpuRestInputCompatibilityCount(PhysicsChainGpuRestInputCompatibilityReason reason)
        => (uint)reason < (uint)_gpuRestCompatibilityCounts.Length
            ? Interlocked.Read(ref _gpuRestCompatibilityCounts[(int)reason])
            : 0L;

    private bool TryCaptureGpuRestInputs(
        PhysicsChainRuntimeHandle handle,
        PhysicsChainComponent component,
        PhysicsChainGpuRestInputRange range)
    {
        if (!TryResolveRuntimeHandle(handle, out PhysicsChainComponent? resident) ||
            !ReferenceEquals(resident, component))
            return RejectGpuRestInput(PhysicsChainGpuRestInputCompatibilityReason.InvalidRuntimeHandle);
        if (component.GpuSyncToBones)
            return RejectGpuRestInput(PhysicsChainGpuRestInputCompatibilityReason.CpuBoneMirror);
        if (_gpuRestHasOpaqueInputDependencies)
            return RejectGpuRestInput(PhysicsChainGpuRestInputCompatibilityReason.OpaqueTransformDependency);
        if (_gpuRestForcedCompatibilitySlots.Contains(handle.Slot))
            return RejectGpuRestInput(PhysicsChainGpuRestInputCompatibilityReason.HierarchyInputDependency);

        RefreshGpuRestOwnership();
        if (range.SourceVersion != component.GpuRestSourceVersion)
            component.CaptureGpuRestTopology(range);
        bool sampleCapture = ShouldSampleGpuRestInput(handle.Slot, _gpuRestInputPhase);
        long colliderStart = sampleCapture ? Stopwatch.GetTimestamp() : 0L;
        bool colliderDependency;
        try { colliderDependency = component.HasGpuRestColliderDependency(_gpuRestResetNodeOwners); }
        finally
        {
            if (sampleCapture)
            {
                Interlocked.Add(ref _gpuRestColliderSampleTicks, Stopwatch.GetTimestamp() - colliderStart);
                Interlocked.Increment(ref _gpuRestColliderSampleCount);
            }
        }
        if (colliderDependency)
            return RejectGpuRestInput(PhysicsChainGpuRestInputCompatibilityReason.ColliderDependency);

        range.CachedCaptureGeneration = 0;
        range.CachedOwnershipGeneration = 0;
        long ownershipGeneration = Interlocked.Read(ref _gpuRestOwnershipGeneration);
        bool rigidHit = false;
        if (component.EnableRigidGpuRestInputCache)
        {
            PhysicsChainRigidGpuRestCaptureSample captureSample = default;
            try
            {
                rigidHit = range.RigidCache.TryCapture(range, _gpuRestResetNodeOwners,
                    _gpuRestPrerequisiteMatrixTargets, ownershipGeneration, sampleCapture,
                    out captureSample);
            }
            finally
            {
                if (sampleCapture)
                    RecordRigidGpuRestCaptureSample(in captureSample);
            }
        }
        if (rigidHit)
        {
            range.CachedOwnershipGeneration = ownershipGeneration;
            ++_rigidGpuRestInputCacheHits;
            return true;
        }
        if (component.EnableRigidGpuRestInputCache)
            ++_rigidGpuRestInputCacheMisses;

        PhysicsChainGpuRestInputCompatibilityReason reason =
            range.Capture(_gpuRestResetNodeOwners, _gpuRestPrerequisiteMatrixTargets);
        if (reason != PhysicsChainGpuRestInputCompatibilityReason.None)
            return RejectGpuRestInput(reason);

        if (component.EnableRigidGpuRestInputCache)
            range.RigidCache.Certify(range, ownershipGeneration);

        return true;
    }

    internal bool TryConsumeGpuRestInputs(
        PhysicsChainRuntimeHandle handle,
        PhysicsChainComponent component,
        out ReadOnlySpan<Matrix4x4> matrices)
    {
        matrices = default;
        if (!TryResolveRuntimeHandle(handle, out PhysicsChainComponent? resident) ||
            !ReferenceEquals(resident, component))
            return false;
        PhysicsChainGpuRestInputRange? range = _gpuRestInputRanges[handle.Slot];
        if (range is null || range.CapturedPhase != _gpuRestInputPhase ||
            range.ConsumedPhase == _gpuRestInputPhase ||
            range.CapturedHandle != handle)
            return false;

        range.ConsumedPhase = _gpuRestInputPhase;
        range.CaptureFault?.Throw();
        if (range.SourceVersion != component.GpuRestSourceVersion)
            return false;
        if (range.CachedCaptureGeneration != 0 &&
            (!component.EnableRigidGpuRestInputCache ||
             range.CachedCaptureGeneration != range.RigidCache.InvalidationGeneration ||
             range.CachedOwnershipGeneration != Interlocked.Read(ref _gpuRestOwnershipGeneration)))
            return false;
        component.AcceptGpuRestInputCapture(range, handle);
        matrices = range.Matrices.AsSpan(0, range.Count);
        return true;
    }

    private bool RejectGpuRestInput(PhysicsChainGpuRestInputCompatibilityReason reason)
    {
        Interlocked.Increment(ref _gpuRestCompatibilityCounts[(int)reason]);
        return false;
    }
}
