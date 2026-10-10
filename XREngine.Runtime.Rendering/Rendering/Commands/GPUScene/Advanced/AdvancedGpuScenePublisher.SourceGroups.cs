using System.Runtime.CompilerServices;

namespace XREngine.Rendering.Commands;

public sealed partial class AdvancedGpuScenePublisher
{
    private IRenderCommandMesh?[] _plannedIdentitySources = [];
    private int[] _plannedIdentityPrimitiveCounts = [];
    private int[] _plannedIdentityOffsets = [];
    private AdvancedGpuHandle[] _plannedIdentityHandles = [];
    private int[] _plannedIdentitySourceSlots = [];
    private uint[] _plannedIdentitySourceSlotStamps = [];
    private int _plannedIdentitySourceCount;
    private int _plannedIdentityHandleCount;
    private uint _plannedIdentitySourceSlotGeneration;

    private IRenderCommandMesh?[] _retainedIdentitySources = [];
    private int[] _retainedIdentityPrimitiveCounts = [];
    private int[] _retainedIdentityOffsets = [];
    private AdvancedGpuHandle[] _retainedIdentityHandles = [];
    private int _retainedIdentitySourceCount;
    private int _retainedIdentityHandleCount;
    private ulong _retainedIdentityMembershipGeneration;
    private bool _retainedIdentityGroupsValid;
    private bool _useRetainedIdentityGroups;
    private bool _sourceGroupsIncludedRetryRecipients;
    private ulong _publicationStartMembershipGeneration;

    private IRenderCommandMesh?[] _pendingIdentitySources = [];
    private int[] _pendingIdentityPrimitiveCounts = [];
    private int _pendingIdentitySourceCount;
    private int _lastSourceGroupReuseCount;
    private int _lastSourceGroupRebuildCount;

    /// <summary>Gets source groups reused in the last preflight.</summary>
    public int LastSourceGroupReuseCount => _lastSourceGroupReuseCount;

    /// <summary>Gets source-group rebuilds in the last publication attempt.</summary>
    public int LastSourceGroupRebuildCount => _lastSourceGroupRebuildCount;

    /// <summary>Gets recipients that still need a complete identity delivery.</summary>
    public int PendingIdentityRecipientCount => _pendingIdentitySourceCount;

    private void EnsureSourceGroupCapacity(int groupCapacity)
    {
        int required = checked(groupCapacity + _pendingIdentitySourceCount);
        if (_plannedIdentitySources.Length < required)
        {
            int capacity = checked((int)NextPowerOfTwo(checked((uint)Math.Max(required, 1))));
            Array.Resize(ref _plannedIdentitySources, capacity);
            Array.Resize(ref _plannedIdentityPrimitiveCounts, capacity);
            Array.Resize(ref _plannedIdentityOffsets, capacity);
        }
        if (_retainedIdentitySources.Length < groupCapacity)
        {
            int capacity = checked((int)NextPowerOfTwo(checked((uint)Math.Max(groupCapacity, 1))));
            Array.Resize(ref _retainedIdentitySources, capacity);
            Array.Resize(ref _retainedIdentityPrimitiveCounts, capacity);
            Array.Resize(ref _retainedIdentityOffsets, capacity);
            InvalidateSourceGroups();
        }
        int pendingRequired = checked(_pendingIdentitySourceCount + required);
        if (_pendingIdentitySources.Length < pendingRequired)
        {
            int capacity = checked((int)NextPowerOfTwo(checked((uint)Math.Max(pendingRequired, 1))));
            Array.Resize(ref _pendingIdentitySources, capacity);
            Array.Resize(ref _pendingIdentityPrimitiveCounts, capacity);
        }
    }

    private void EnsureSourceGroupLookupCapacity(int capacity)
        => GrowStampedSlots(ref _plannedIdentitySourceSlots,
            ref _plannedIdentitySourceSlotStamps, capacity,
            ref _plannedIdentitySourceSlotGeneration);

    private bool TryPrepareSourceGroups(out string reason)
    {
        int previousSourceCount = _plannedIdentitySourceCount;
        int previousHandleCount = _plannedIdentityHandleCount;
        _useRetainedIdentityGroups = false;
        _plannedIdentitySourceCount = 0;
        _plannedIdentityHandleCount = 0;
        _sourceGroupsIncludedRetryRecipients = _pendingIdentitySourceCount != 0;
        if (CanReuseSourceGroups())
        {
            Array.Clear(_plannedIdentitySources, 0, previousSourceCount);
            Array.Clear(_plannedIdentityHandles, 0, previousHandleCount);
            _useRetainedIdentityGroups = true;
            _lastSourceGroupReuseCount = _retainedIdentitySourceCount;
            reason = string.Empty;
            return true;
        }

        Array.Clear(_plannedIdentitySources, 0, previousSourceCount);
        Array.Clear(_plannedIdentityHandles, 0, previousHandleCount);
        BeginStampedPlan(ref _plannedIdentitySourceSlotGeneration,
            _plannedIdentitySourceSlotStamps);
        for (int index = 0; index < _registrationCount; ++index)
        {
            ref readonly AdvancedResidentRegistration registration = ref _registrations[index];
            if (registration.Active && registration.Source is { } source &&
                !TryAppendPlannedIdentitySource(source, checked(registration.PrimitiveIndex + 1)))
            {
                reason = "The planned canonical identity-source table is full.";
                return false;
            }
        }
        for (int index = 0; index < _plannedCommandCount; ++index)
        {
            ref readonly AdvancedGpuSceneCommandTransition plan = ref _plannedCommands[index];
            if (plan.Source is { } source &&
                !TryAppendPlannedIdentitySource(source, plan.SourcePrimitiveCount))
            {
                reason = "The planned canonical identity-source table is full.";
                return false;
            }
        }
        for (int index = 0; index < _pendingIdentitySourceCount; ++index)
        {
            if (_pendingIdentitySources[index] is { } source &&
                !TryAppendPlannedIdentitySource(source, _pendingIdentityPrimitiveCounts[index]))
            {
                reason = "The pending canonical identity-source table is full.";
                return false;
            }
        }

        int handleCount = 0;
        for (int index = 0; index < _plannedIdentitySourceCount; ++index)
        {
            _plannedIdentityOffsets[index] = handleCount;
            handleCount = checked(handleCount + _plannedIdentityPrimitiveCounts[index]);
        }
        EnsureSourceHandleCapacity(handleCount);
        _plannedIdentityHandleCount = handleCount;
        ++_lastSourceGroupRebuildCount;
        reason = string.Empty;
        return true;
    }

    private bool CanReuseSourceGroups()
    {
        if (!_retainedIdentityGroupsValid || _pendingIdentitySourceCount != 0 ||
            !_orderedRegistrationIdentitiesValid ||
            _retainedIdentityMembershipGeneration != _registrationMembershipGeneration ||
            _orderedRegistrationMembershipGeneration != _registrationMembershipGeneration ||
            _orderedRegistrationIdentityCount != _plannedCommandCount ||
            _validatedOrderedRegistrationRowCount != _plannedCommandCount)
            return false;

        for (int commandIndex = 0; commandIndex < _plannedCommandCount; ++commandIndex)
        {
            ref readonly AdvancedGpuSceneCommandTransition plan =
                ref _plannedCommands[commandIndex];
            ref readonly OrderedRegistrationIdentity identity =
                ref _orderedRegistrationIdentities[commandIndex];
            if (!ReferenceEquals(identity.Source, plan.Source) ||
                identity.PrimitiveIndex != plan.PrimitiveIndex ||
                identity.SourcePrimitiveCount != plan.SourcePrimitiveCount ||
                identity.Supported != plan.Supported)
                return false;
            if (plan.Source is null)
            {
                if (identity.GroupIndex >= 0)
                    return false;
                continue;
            }
            int groupIndex = identity.GroupIndex;
            if ((uint)groupIndex >= (uint)_retainedIdentitySourceCount ||
                !ReferenceEquals(_retainedIdentitySources[groupIndex], plan.Source) ||
                _retainedIdentityPrimitiveCounts[groupIndex] != plan.SourcePrimitiveCount)
                return false;
            if (plan.Supported &&
                (plan.RegistrationIndex < 0 ||
                 _registrations[plan.RegistrationIndex].Draw != identity.Draw))
                return false;
        }
        for (int index = 0; index < _registrationCount; ++index)
            if (_registrations[index].Active &&
                _preflightSeenStamps[index] != _preflightSeenGeneration)
                return false;
        return true;
    }

    private bool TryAppendPlannedIdentitySource(IRenderCommandMesh source, int primitiveCount)
    {
        primitiveCount = Math.Max(1, primitiveCount);
        uint mask = checked((uint)_plannedIdentitySourceSlots.Length - 1u);
        uint start = IdentitySourceHash(source) & mask;
        for (uint probe = 0u; probe < (uint)_plannedIdentitySourceSlots.Length; ++probe)
        {
            int slot = checked((int)((start + probe) & mask));
            if (_plannedIdentitySourceSlotStamps[slot] != _plannedIdentitySourceSlotGeneration)
            {
                if (_plannedIdentitySourceCount >= _plannedIdentitySources.Length)
                    return false;
                int index = _plannedIdentitySourceCount++;
                _plannedIdentitySources[index] = source;
                _plannedIdentityPrimitiveCounts[index] = primitiveCount;
                _plannedIdentitySourceSlots[slot] = index;
                _plannedIdentitySourceSlotStamps[slot] = _plannedIdentitySourceSlotGeneration;
                return true;
            }
            int existingIndex = _plannedIdentitySourceSlots[slot];
            if (!ReferenceEquals(_plannedIdentitySources[existingIndex], source))
                continue;
            _plannedIdentityPrimitiveCounts[existingIndex] = Math.Max(
                _plannedIdentityPrimitiveCounts[existingIndex], primitiveCount);
            return true;
        }
        return false;
    }

    private int FindPlannedIdentitySource(IRenderCommandMesh source)
    {
        uint mask = checked((uint)_plannedIdentitySourceSlots.Length - 1u);
        uint start = IdentitySourceHash(source) & mask;
        for (uint probe = 0u; probe < (uint)_plannedIdentitySourceSlots.Length; ++probe)
        {
            int slot = checked((int)((start + probe) & mask));
            if (_plannedIdentitySourceSlotStamps[slot] != _plannedIdentitySourceSlotGeneration)
                return -1;
            int index = _plannedIdentitySourceSlots[slot];
            if (ReferenceEquals(_plannedIdentitySources[index], source))
                return index;
        }
        return -1;
    }

    private void EnsureSourceHandleCapacity(int handleCount)
    {
        if (_plannedIdentityHandles.Length < handleCount)
            Array.Resize(ref _plannedIdentityHandles,
                checked((int)NextPowerOfTwo(checked((uint)handleCount))));
        if (_retainedIdentityHandles.Length < handleCount)
        {
            Array.Resize(ref _retainedIdentityHandles,
                checked((int)NextPowerOfTwo(checked((uint)handleCount))));
            InvalidateSourceGroups();
        }
    }

    private static uint IdentitySourceHash(IRenderCommandMesh source)
    {
        uint hash = unchecked((uint)RuntimeHelpers.GetHashCode(source));
        hash ^= hash >> 16;
        hash *= 0x7FEB352Du;
        return hash ^ (hash >> 15);
    }

    private void PublishPreparedSourceDrawIdentities(
        in AdvancedGpuScenePublicationReference publication)
    {
        if (_useRetainedIdentityGroups)
        {
            for (int index = 0; index < _retainedIdentitySourceCount; ++index)
            {
                if (_retainedIdentitySources[index] is RenderCommandMesh3D command)
                    command.PublishCanonicalDrawIdentities(
                        Database, publication,
                        _retainedIdentityHandles.AsSpan(
                            _retainedIdentityOffsets[index],
                            _retainedIdentityPrimitiveCounts[index]));
            }
            return;
        }

        for (int index = 0; index < _plannedIdentitySourceCount; ++index)
        {
            if (_plannedIdentitySources[index] is not { } source)
                continue;
            int count = _plannedIdentityPrimitiveCounts[index];
            Span<AdvancedGpuHandle> handles =
                _plannedIdentityHandles.AsSpan(_plannedIdentityOffsets[index], count);
            handles.Clear();
            for (int primitiveIndex = 0; primitiveIndex < count; ++primitiveIndex)
            {
                int registrationIndex = FindRegistration(source, primitiveIndex);
                if (registrationIndex >= 0)
                    handles[primitiveIndex] = _registrations[registrationIndex].Draw;
            }
        }
        for (int index = 0; index < _plannedIdentitySourceCount; ++index)
        {
            if (_plannedIdentitySources[index] is RenderCommandMesh3D command)
                command.PublishCanonicalDrawIdentities(
                    Database, publication,
                    _plannedIdentityHandles.AsSpan(
                        _plannedIdentityOffsets[index],
                        _plannedIdentityPrimitiveCounts[index]));
        }
    }

    private void RetainFailedIdentityRecipients()
    {
        int sourceCount = _useRetainedIdentityGroups
            ? _retainedIdentitySourceCount
            : _plannedIdentitySourceCount;
        if (_pendingIdentitySources.Length < checked(_pendingIdentitySourceCount + sourceCount))
            throw new InvalidOperationException(
                "The pending identity-recipient capacity changed after publication preflight.");
        for (int index = 0; index < sourceCount; ++index)
        {
            IRenderCommandMesh? source = _useRetainedIdentityGroups
                ? _retainedIdentitySources[index]
                : _plannedIdentitySources[index];
            if (source is null)
                continue;
            int count = _useRetainedIdentityGroups
                ? _retainedIdentityPrimitiveCounts[index]
                : _plannedIdentityPrimitiveCounts[index];
            int existing = -1;
            for (int pendingIndex = 0; pendingIndex < _pendingIdentitySourceCount; ++pendingIndex)
            {
                if (ReferenceEquals(_pendingIdentitySources[pendingIndex], source))
                {
                    existing = pendingIndex;
                    break;
                }
            }
            if (existing >= 0)
                _pendingIdentityPrimitiveCounts[existing] = Math.Max(
                    _pendingIdentityPrimitiveCounts[existing], count);
            else
            {
                int destination = _pendingIdentitySourceCount++;
                _pendingIdentitySources[destination] = source;
                _pendingIdentityPrimitiveCounts[destination] = count;
            }
        }
        InvalidateSourceGroups();
    }

    private void ClearPendingIdentityRecipients()
    {
        Array.Clear(_pendingIdentitySources, 0, _pendingIdentitySourceCount);
        Array.Clear(_pendingIdentityPrimitiveCounts, 0, _pendingIdentitySourceCount);
        _pendingIdentitySourceCount = 0;
    }

    private void PromoteSourceGroups()
    {
        if (_useRetainedIdentityGroups)
            return;
        if (_sourceGroupsIncludedRetryRecipients ||
            _publicationStartMembershipGeneration != _registrationMembershipGeneration ||
            _pendingIdentitySourceCount != 0)
        {
            InvalidateSourceGroups();
            return;
        }
        if (_plannedIdentitySourceCount > _retainedIdentitySources.Length ||
            _plannedIdentityHandleCount > _retainedIdentityHandles.Length)
        {
            InvalidateSourceGroups();
            return;
        }

        _plannedIdentitySources.AsSpan(0, _plannedIdentitySourceCount)
            .CopyTo(_retainedIdentitySources);
        _plannedIdentityPrimitiveCounts.AsSpan(0, _plannedIdentitySourceCount)
            .CopyTo(_retainedIdentityPrimitiveCounts);
        _plannedIdentityOffsets.AsSpan(0, _plannedIdentitySourceCount)
            .CopyTo(_retainedIdentityOffsets);
        _plannedIdentityHandles.AsSpan(0, _plannedIdentityHandleCount)
            .CopyTo(_retainedIdentityHandles);
        if (_plannedIdentitySourceCount < _retainedIdentitySourceCount)
            Array.Clear(_retainedIdentitySources, _plannedIdentitySourceCount,
                _retainedIdentitySourceCount - _plannedIdentitySourceCount);
        if (_plannedIdentityHandleCount < _retainedIdentityHandleCount)
            Array.Clear(_retainedIdentityHandles, _plannedIdentityHandleCount,
                _retainedIdentityHandleCount - _plannedIdentityHandleCount);
        _retainedIdentitySourceCount = _plannedIdentitySourceCount;
        _retainedIdentityHandleCount = _plannedIdentityHandleCount;
        _retainedIdentityMembershipGeneration = _registrationMembershipGeneration;
        _retainedIdentityGroupsValid = true;
    }

    private void ClearSourceGroupReferences()
    {
        Array.Clear(_plannedIdentitySources);
        Array.Clear(_retainedIdentitySources);
        Array.Clear(_pendingIdentitySources);
        _plannedIdentitySourceCount = 0;
        _retainedIdentitySourceCount = 0;
        _pendingIdentitySourceCount = 0;
        _retainedIdentityGroupsValid = false;
    }

    private void InvalidateSourceGroups() => _retainedIdentityGroupsValid = false;
}
