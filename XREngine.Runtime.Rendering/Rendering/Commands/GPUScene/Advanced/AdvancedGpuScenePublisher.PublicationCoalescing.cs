namespace XREngine.Rendering.Commands;

public sealed partial class AdvancedGpuScenePublisher
{
    /// <summary>
    /// Reuses the retained canonical image when whole-scene preflight proves
    /// that neither resident ownership nor payload content changed. View and
    /// frame state live in the backend-ready package, so sealing another copy of
    /// the same scene image would only consume a bounded publication-ring slot.
    /// </summary>
    private bool TryReuseUnchangedPublication()
    {
        if (!_currentPublication.IsValid ||
            !Database.TryGetPublicationSnapshot(_currentPublication, out _) ||
            HasPlannedPublicationMutation())
        {
            return false;
        }

        _sequence = _currentPublication.Sequence;
        for (int commandIndex = 0;
             commandIndex < _plannedCommandCount;
             ++commandIndex)
        {
            ref readonly AdvancedGpuSceneCommandTransition plan =
                ref _plannedCommands[commandIndex];
            if (!plan.Supported || plan.RegistrationIndex < 0)
            {
                _commandDrawHandles[commandIndex] = AdvancedGpuHandle.Invalid;
                continue;
            }

            ref AdvancedResidentRegistration registration =
                ref _registrations[plan.RegistrationIndex];
            registration.LastSeenSequence = _sequence;
            registration.LegacyCommandIndex = checked((uint)commandIndex);
            _commandDrawHandles[commandIndex] = registration.Draw;
            AppendLegacyMapping(
                checked((uint)commandIndex),
                plan.PrimitiveIndex,
                in plan.Command,
                in registration);
        }

        PublishSourceDrawIdentities(in _currentPublication);
        return true;
    }

    private bool HasPlannedPublicationMutation()
    {
        if (_plannedLightMutationCount != 0 ||
            _plannedMaterialReleaseCount != 0 ||
            _resourceAcquireCount != 0 ||
            _resourceReleaseCount != 0)
        {
            return true;
        }

        for (int materialIndex = 0;
             materialIndex < _plannedMaterialCount;
             ++materialIndex)
        {
            ref readonly AdvancedGpuMaterialTransitionRequest request =
                ref _plannedMaterialRequests[materialIndex];
            if (!request.MaterialHandle.IsValid ||
                request.AcquireCount != 0u ||
                request.RequiresPayloadUpdate)
            {
                return true;
            }
        }

        for (int commandIndex = 0;
             commandIndex < _plannedCommandCount;
             ++commandIndex)
        {
            ref readonly AdvancedGpuSceneCommandTransition plan =
                ref _plannedCommands[commandIndex];
            if (!plan.Supported)
                continue;
            if (plan.RegistrationIndex < 0)
                return true;

            ref readonly AdvancedResidentRegistration registration =
                ref _registrations[plan.RegistrationIndex];
            AdvancedGpuHandle material =
                _plannedMaterialRequests[plan.MaterialPlanIndex].MaterialHandle;
            if (!registration.Active ||
                !material.IsValid ||
                registration.Material != material ||
                registration.StructuralSignature != plan.StructuralSignature ||
                registration.ContentSignature != plan.ContentSignature ||
                registration.LegacyCommandIndex != checked((uint)commandIndex))
            {
                return true;
            }
        }

        for (int registrationIndex = 0;
             registrationIndex < _registrationCount;
             ++registrationIndex)
        {
            if (_registrations[registrationIndex].Active &&
                _preflightSeenStamps[registrationIndex] != _preflightSeenGeneration)
            {
                return true;
            }
        }

        return false;
    }
}
