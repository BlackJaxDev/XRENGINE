namespace XREngine.Rendering.Commands;

public sealed partial class AdvancedGpuScenePublisher
{
    /// <summary>
    /// Reuses the retained canonical image when whole-scene preflight proves
    /// that neither resident ownership nor payload content changed. View and
    /// frame state live in the backend-ready package, so sealing another copy of
    /// the same scene image would only consume a bounded publication-ring slot.
    /// </summary>
    private bool TryReuseUnchangedPublication(ulong frameId)
    {
        if (!_currentPublication.IsValid)
        {
            S13aPublicationTelemetry.PublicationMissing();
            S13aPublicationTelemetry.Trace(S13aPublicationTraceEventKind.PublicationMissing,
                publicationSequence: _currentPublication.Sequence, frameId: frameId);
            return false;
        }
        AdvancedGpuScenePublicationReference candidate = _currentPublication;
        if (!Database.TryAcquirePublicationLease(
                in candidate,
                EAdvancedGpuScenePublicationPinKind.Package,
                out AdvancedGpuScenePublicationLease inspectionLease))
        {
            S13aPublicationTelemetry.PublicationExpired();
            S13aPublicationTelemetry.Trace(S13aPublicationTraceEventKind.PublicationExpired,
                publicationSequence: candidate.Sequence, frameId: frameId);
            return false;
        }

        bool hasMutation;
        try
        {
            // Reclamation clears managed sidecars after the final pin is released.
            // Keep this inspection lease until the retained span comparison ends.
            if (!Database.TryGetPublicationSnapshot(in candidate, out AdvancedGpuScenePublicationSnapshot retainedPublication))
                return false;
            hasMutation = HasPlannedPublicationMutation(frameId, retainedPublication.Submission.DeformationSources);
        }
        finally
        {
            inspectionLease.Dispose();
        }

        if (hasMutation)
            return false;
        // The inspection lease may have been the last pin. A retired candidate
        // must be republished before its identity can reach the next package.
        if (!Database.TryGetPublicationSnapshot(in candidate, out _))
        {
            S13aPublicationTelemetry.PublicationExpired();
            S13aPublicationTelemetry.Trace(S13aPublicationTraceEventKind.PublicationExpired,
                publicationSequence: candidate.Sequence, frameId: frameId);
            return false;
        }

        _sequence = candidate.Sequence;
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
            registration.LastSeenFrameId = frameId;
            registration.LegacyCommandIndex = checked((uint)commandIndex);
            _commandDrawHandles[commandIndex] = registration.Draw;
            AppendLegacyMapping(
                checked((uint)commandIndex),
                plan.PrimitiveIndex,
                in plan.Command,
                in registration);
        }

        _identityDeliveryIncomplete = true;
        try
        {
            PublishSourceDrawIdentities(in _currentPublication);
            _identityDeliveryIncomplete = false;
        }
        catch (Exception exception)
        {
            _identityDeliveryIncomplete = true;
            RejectPublication(exception.Message);
            throw;
        }
        // A reuse is successful only after mappings and source identities have
        // been refreshed; a failure during either step must not count as reuse.
        S13aPublicationTelemetry.PublicationReuse();
        S13aPublicationTelemetry.Trace(S13aPublicationTraceEventKind.PublicationReused,
            publicationSequence: _currentPublication.Sequence, frameId: frameId,
            databaseEpoch: _currentPublication.Publication.DatabaseEpoch,
            frameGeneration: _currentPublication.Publication.FrameGeneration,
            topologyGeneration: _currentPublication.Publication.TopologyGeneration,
            contentGeneration: _currentPublication.Publication.ContentGeneration,
            lookupGeneration: _currentPublication.Publication.LookupGeneration);
        return true;
    }

    private bool HasPlannedPublicationMutation(
        ulong frameId,
        ReadOnlySpan<AdvancedManagedDeformationSourceRow> retainedSources)
    {
        if (_plannedLightMutationCount != 0 ||
            _plannedShadowPayloadUpdateCount != 0 ||
            _plannedMaterialReleaseCount != 0 ||
            _resourceAcquireCount != 0 ||
            _resourceReleaseCount != 0)
        {
            S13aPublicationTelemetry.PublicationResourceMutation();
            S13aPublicationTelemetry.Trace(S13aPublicationTraceEventKind.PublicationResourceMutation,
                publicationSequence: _currentPublication.Sequence, frameId: frameId);
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
                S13aPublicationTelemetry.PublicationMaterialMutation();
                S13aPublicationTelemetry.Trace(S13aPublicationTraceEventKind.PublicationMaterialMutation,
                    publicationSequence: _currentPublication.Sequence, frameId: frameId,
                    detail: materialIndex);
                return true;
            }
        }

        int retainedSourceIndex = 0;
        for (int commandIndex = 0;
             commandIndex < _plannedCommandCount;
             ++commandIndex)
        {
            ref readonly AdvancedGpuSceneCommandTransition plan =
                ref _plannedCommands[commandIndex];
            if (!plan.Supported)
                continue;
            if (plan.RegistrationIndex < 0)
            {
                S13aPublicationTelemetry.PublicationCommandMutation();
                S13aPublicationTelemetry.Trace(S13aPublicationTraceEventKind.PublicationCommandMutation,
                    plan.Source is RenderCommand source ? source.StableQueryKey : 0u,
                    publicationSequence: _currentPublication.Sequence, frameId: frameId,
                    detail: commandIndex);
                return true;
            }

            ref readonly AdvancedResidentRegistration registration =
                ref _registrations[plan.RegistrationIndex];
            AdvancedGpuHandle material =
                _plannedMaterialRequests[plan.MaterialPlanIndex].MaterialHandle;
            if (!registration.Active ||
                !material.IsValid ||
                registration.Material != material ||
                registration.StructuralSignature != plan.StructuralSignature ||
                registration.ContentSignature != plan.ContentSignature ||
                registration.LegacyCommandIndex != checked((uint)commandIndex) ||
                retainedSourceIndex >= retainedSources.Length ||
                !ReferenceEquals(retainedSources[retainedSourceIndex].Renderer, plan.Renderer) ||
                !ReferenceEquals(retainedSources[retainedSourceIndex].Mesh, plan.Mesh))
            {
                S13aPublicationTelemetry.PublicationCommandMutation();
                S13aPublicationTelemetry.Trace(S13aPublicationTraceEventKind.PublicationCommandMutation,
                    plan.Source is RenderCommand source ? source.StableQueryKey : 0u,
                    publicationSequence: _currentPublication.Sequence, frameId: frameId,
                    detail: commandIndex);
                return true;
            }
            ++retainedSourceIndex;
            if (plan.TemporalEventReason != EAdvancedVelocityValidityReason.Valid)
            {
                S13aPublicationTelemetry.PublicationTemporalMutation();
                S13aPublicationTelemetry.Trace(S13aPublicationTraceEventKind.PublicationTemporalMutation,
                    plan.Source is RenderCommand source ? source.StableQueryKey : 0u,
                    publicationSequence: _currentPublication.Sequence, frameId: frameId,
                    detail: (int)plan.TemporalEventReason);
                return true;
            }
        }

        if (retainedSourceIndex != retainedSources.Length)
            return true;

        for (int registrationIndex = 0;
             registrationIndex < _registrationCount;
             ++registrationIndex)
        {
            if (_registrations[registrationIndex].Active &&
                _preflightSeenStamps[registrationIndex] != _preflightSeenGeneration)
            {
                S13aPublicationTelemetry.PublicationRegistrationRemoval();
                S13aPublicationTelemetry.Trace(S13aPublicationTraceEventKind.PublicationRegistrationRemoval,
                    publicationSequence: _currentPublication.Sequence, frameId: frameId,
                    detail: registrationIndex);
                return true;
            }
        }

        return false;
    }
}
