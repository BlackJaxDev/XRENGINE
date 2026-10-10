namespace XREngine.Rendering.Commands;

public sealed partial class AdvancedGpuScenePublisher
{
    private OrderedRegistrationIdentity[] _orderedRegistrationIdentities =
        new OrderedRegistrationIdentity[InitialCapacity];
    private int _orderedRegistrationIdentityCount;
    private ulong _registrationMembershipGeneration = 1UL;
    private ulong _orderedRegistrationMembershipGeneration;
    private bool _orderedRegistrationIdentitiesValid;
    private bool _registrationLookupNeedsRebuild = true;
    private int _lastRegistrationIdentityReuseCount;
    private int _lastRegistrationLookupRebuildCount;
    private int _validatedOrderedRegistrationRowCount;

    /// <summary>Gets ordered registration entries reused in the last preflight.</summary>
    public int LastRegistrationIdentityReuseCount => _lastRegistrationIdentityReuseCount;

    /// <summary>Gets registration hash rebuilds in the last publication attempt.</summary>
    public int LastRegistrationLookupRebuildCount => _lastRegistrationLookupRebuildCount;

    private bool TryFindOrderedRegistration(
        int commandIndex,
        IRenderCommandMesh source,
        int primitiveIndex,
        int sourcePrimitiveCount,
        out int registrationIndex)
    {
        registrationIndex = -1;
        if (!_orderedRegistrationIdentitiesValid ||
            _orderedRegistrationMembershipGeneration != _registrationMembershipGeneration ||
            _orderedRegistrationIdentityCount != _plannedCommandCount ||
            (uint)commandIndex >= (uint)_orderedRegistrationIdentityCount)
            return false;

        ref readonly OrderedRegistrationIdentity identity =
            ref _orderedRegistrationIdentities[commandIndex];
        if (!ReferenceEquals(identity.Source, source) ||
            identity.PrimitiveIndex != primitiveIndex ||
            identity.SourcePrimitiveCount != sourcePrimitiveCount)
        {
            _orderedRegistrationIdentitiesValid = false;
            InvalidateSourceGroups();
            return false;
        }

        int index = identity.RegistrationIndex;
        if (index < 0)
        {
            if (identity.Draw.IsValid)
            {
                _orderedRegistrationIdentitiesValid = false;
                InvalidateSourceGroups();
                return false;
            }
        }
        else if ((uint)index >= (uint)_registrationCount ||
                 !_registrations[index].Active ||
                 !ReferenceEquals(_registrations[index].Source, source) ||
                 _registrations[index].PrimitiveIndex != primitiveIndex ||
                 _registrations[index].Draw != identity.Draw ||
                 !identity.Draw.IsValid)
        {
            _orderedRegistrationIdentitiesValid = false;
            InvalidateSourceGroups();
            return false;
        }

        registrationIndex = index;
        ++_validatedOrderedRegistrationRowCount;
        ++_lastRegistrationIdentityReuseCount;
        return true;
    }

    private void ValidateAbsentOrderedRegistration(int commandIndex)
    {
        if (!_orderedRegistrationIdentitiesValid ||
            _orderedRegistrationMembershipGeneration != _registrationMembershipGeneration ||
            _orderedRegistrationIdentityCount != _plannedCommandCount ||
            (uint)commandIndex >= (uint)_orderedRegistrationIdentityCount)
            return;

        ref readonly OrderedRegistrationIdentity identity =
            ref _orderedRegistrationIdentities[commandIndex];
        if (identity.Source is not null || identity.RegistrationIndex >= 0 ||
            identity.Draw.IsValid)
        {
            _orderedRegistrationIdentitiesValid = false;
            InvalidateSourceGroups();
            return;
        }

        ++_validatedOrderedRegistrationRowCount;
    }

    private void PromoteOrderedRegistrations()
    {
        if (_publicationStartMembershipGeneration != _registrationMembershipGeneration)
            return;
        if (_useRetainedIdentityGroups && _orderedRegistrationIdentitiesValid &&
            _orderedRegistrationMembershipGeneration == _registrationMembershipGeneration &&
            _orderedRegistrationIdentityCount == _plannedCommandCount &&
            _validatedOrderedRegistrationRowCount == _plannedCommandCount)
            return;

        if (_plannedCommandCount > _orderedRegistrationIdentities.Length)
        {
            _orderedRegistrationIdentitiesValid = false;
            return;
        }

        // Keep the full cleanup extent if promotion fails after it writes new rows.
        _orderedRegistrationIdentitiesValid = false;
        _orderedRegistrationIdentityCount = Math.Max(
            _orderedRegistrationIdentityCount, _plannedCommandCount);

        for (int commandIndex = 0; commandIndex < _plannedCommandCount; ++commandIndex)
        {
            ref readonly AdvancedGpuSceneCommandTransition plan =
                ref _plannedCommands[commandIndex];
            int registrationIndex = plan.Supported ? plan.RegistrationIndex : -1;
            if (plan.Supported && registrationIndex < 0 && plan.Source is { } newSource)
                registrationIndex = FindRegistration(newSource, plan.PrimitiveIndex);
            AdvancedGpuHandle draw = registrationIndex >= 0
                ? _registrations[registrationIndex].Draw
                : AdvancedGpuHandle.Invalid;
            if (plan.Supported &&
                (registrationIndex < 0 || draw != _commandDrawHandles[commandIndex]))
            {
                _orderedRegistrationIdentitiesValid = false;
                return;
            }
            int groupIndex = plan.Source is { } source
                ? _useRetainedIdentityGroups
                    ? _orderedRegistrationIdentities[commandIndex].GroupIndex
                    : FindPlannedIdentitySource(source)
                : -1;
            if (plan.Source is not null && groupIndex < 0)
            {
                _orderedRegistrationIdentitiesValid = false;
                return;
            }
            _orderedRegistrationIdentities[commandIndex] = new OrderedRegistrationIdentity(
                plan.Source, plan.PrimitiveIndex, plan.SourcePrimitiveCount,
                plan.Supported, groupIndex, registrationIndex, draw);
        }

        if (_plannedCommandCount < _orderedRegistrationIdentityCount)
            Array.Clear(_orderedRegistrationIdentities,
                _plannedCommandCount,
                _orderedRegistrationIdentityCount - _plannedCommandCount);
        _orderedRegistrationIdentityCount = _plannedCommandCount;
        _orderedRegistrationMembershipGeneration = _registrationMembershipGeneration;
        _orderedRegistrationIdentitiesValid = true;
    }

    private void InvalidateRegistrationIdentityCache()
    {
        _orderedRegistrationIdentitiesValid = false;
        _registrationLookupNeedsRebuild = true;
        InvalidateSourceGroups();
    }

    private void MarkRegistrationMembershipChanged()
    {
        AdvanceNonZero(ref _registrationMembershipGeneration);
        InvalidateRegistrationIdentityCache();
    }

    private readonly record struct OrderedRegistrationIdentity(
        IRenderCommandMesh? Source,
        int PrimitiveIndex,
        int SourcePrimitiveCount,
        bool Supported,
        int GroupIndex,
        int RegistrationIndex,
        AdvancedGpuHandle Draw);
}
