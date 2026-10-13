using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Compute;
using XREngine.Rendering.Materials;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Skips the whole-scene plan when every command row has the inputs of the
/// retained plan. The gate reads the same inputs as the full plan and compares
/// them with the retained values. It does no material resolution, no variant
/// lookup, and no planned-row hashing, and it reads the bounds route epoch one
/// time. A frame without global changes reuses the current publication. A frame
/// with global changes, or a frame whose publication the ring drained, commits
/// the retained rows again. Any changed input runs the full plan unchanged.
/// </summary>
public sealed partial class AdvancedGpuScenePublisher
{
    // True while the retained plan, the ordered identities, the legacy mappings,
    // and the command draw handles describe the current publication.
    private bool _unchangedRowBaselineValid;
    // True while the retained submission records and deformation rows are the
    // exact rows of the current publication.
    private bool _committedSubmissionRowsValid;
    // The dispatcher epoch read before the first bounds capture of the last full plan.
    private long _planBoundsEpoch;
    private bool _planBoundsEpochCaptured;
    private uint _lastBoundaryRequired;
    private int _lastUnchangedRowGateHitCount;
    private bool _lastPublicationCommittedRetainedRows;

    /// <summary>
    /// Gets the command rows that the most recent publication reused without
    /// the whole-scene plan. Zero means that the whole-scene plan ran, or that
    /// the publication rejected before it reused the rows.
    /// </summary>
    public int LastUnchangedRowGateHitCount => _lastUnchangedRowGateHitCount;

    /// <summary>
    /// Gets whether the most recent publication committed the retained command
    /// rows without the whole-scene plan. This occurs when a global resource
    /// changes or when the ring drained the unchanged publication.
    /// </summary>
    public bool LastPublicationCommittedRetainedRows
        => _lastPublicationCommittedRetainedRows;

    /// <summary>
    /// Releases the retained plan and submission rows. The full plan calls this
    /// before it rebuilds them, so a rejection cannot keep old source references.
    /// </summary>
    private void ReleasePlanScratch()
    {
        Array.Clear(_plannedCommands, 0, _plannedCommandCount);
        Array.Clear(_plannedDeformationSources, 0, _legacyMappingCount);
        _plannedCommandCount = 0;
        _legacyMappingCount = 0;
        _unchangedRowBaselineValid = false;
        _committedSubmissionRowsValid = false;
    }

    /// <summary>Records that the retained plan describes the current publication.</summary>
    private void MarkUnchangedRowBaseline(bool submissionRowsValid)
    {
        _unchangedRowBaselineValid = true;
        _committedSubmissionRowsValid = submissionRowsValid;
    }

    /// <summary>
    /// Returns true when the full plan would produce the retained plan again.
    /// Each row compares the inputs that the full plan reads: the scene command
    /// row and source lookup, the command render snapshot, the primitive mesh
    /// and material, the raster state, the bounds route, and the registration.
    /// </summary>
    private bool TryPassUnchangedRowGate(
        GPUScene scene,
        ulong frameId,
        out int reusedStaticPlans,
        out int materialResolveAttempts)
    {
        reusedStaticPlans = 0;
        materialResolveAttempts = 0;
        int rowCount = _plannedCommandCount;
        if (!_unchangedRowBaselineValid ||
            !_currentPublication.IsValid ||
            _identityDeliveryIncomplete ||
            _pendingIdentitySourceCount != 0 ||
            !_committedMaterialPlanCacheValid ||
            _registrationLookupNeedsRebuild ||
            !_orderedRegistrationIdentitiesValid ||
            _orderedRegistrationMembershipGeneration != _registrationMembershipGeneration ||
            _orderedRegistrationIdentityCount != rowCount ||
            !_retainedIdentityGroupsValid ||
            _retainedIdentityMembershipGeneration != _registrationMembershipGeneration ||
            scene.TotalCommandCount != (uint)rowCount ||
            rowCount > _plannedCommands.Length)
            return false;

        GPUPhysicsChainDispatcher? boundsDispatcher = null;
        XRMaterial? checkedHeaderMaterial = null;
        AdvancedGpuHandle checkedHeaderHandle = AdvancedGpuHandle.Invalid;
        int supportedRows = 0;
        for (int commandIndex = 0; commandIndex < rowCount; ++commandIndex)
        {
            ref readonly AdvancedGpuSceneCommandTransition plan =
                ref _plannedCommands[commandIndex];
            ref readonly OrderedRegistrationIdentity identity =
                ref _orderedRegistrationIdentities[commandIndex];
            uint row = (uint)commandIndex;
            if (plan.Source is not { } planSource)
            {
                // A missing source stays missing only while a scene lookup still fails.
                if (identity.Source is not null || identity.GroupIndex >= 0 ||
                    identity.RegistrationIndex >= 0 || identity.Draw.IsValid ||
                    scene.TryGetAdvancedPreparationCommand(row, out _) &&
                    scene.TryGetSourceCommand(row, out IRenderCommandMesh? missing, out _) &&
                    missing is not null)
                    return false;
                continue;
            }

            if (!ReferenceEquals(identity.Source, planSource) ||
                identity.PrimitiveIndex != plan.PrimitiveIndex ||
                identity.SourcePrimitiveCount != plan.SourcePrimitiveCount ||
                identity.Supported != plan.Supported ||
                !IsRetainedSourceGroupCurrent(in identity, planSource, plan.SourcePrimitiveCount) ||
                !scene.TryGetSourceCommand(row, out IRenderCommandMesh? source, out int primitiveIndex) ||
                !ReferenceEquals(source, planSource) ||
                primitiveIndex != plan.PrimitiveIndex ||
                !scene.TryGetAdvancedPreparationCommand(row, out DrawMetadata command))
                return false;

            AdvancedMeshRenderSnapshot snapshot = CaptureSourceSnapshot(planSource);
            XRMeshRenderer? renderer = snapshot.Renderer;
            if (Math.Max(1, renderer?.Submeshes.Count ?? 0) != plan.SourcePrimitiveCount ||
                !IsGpuBoundsSourceUnchanged(renderer, in plan.GpuBoundsSource, ref boundsDispatcher))
                return false;
            ResolveSourcePrimitive(
                in snapshot,
                plan.PrimitiveIndex,
                out XRMesh? mesh,
                out XRMaterial? material,
                out RenderingParameters? renderOptions);

            if (!plan.Supported)
            {
                if (!IsUnchangedUnsupportedRow(in plan, in identity, in command, mesh, material,
                        ref materialResolveAttempts))
                    return false;
                continue;
            }

            Matrix4x4 world = snapshot.WorldMatrixIsModelMatrix
                ? snapshot.CurrentWorld
                : Matrix4x4.Identity;
            Matrix4x4 previousWorld = snapshot.WorldMatrixIsModelMatrix
                ? snapshot.PreviousWorld
                : Matrix4x4.Identity;
            if (!command.Equals(plan.Command) ||
                !ReferenceEquals(renderer, plan.Renderer) ||
                world != plan.World ||
                previousWorld != plan.PreviousWorld ||
                !IsUnchangedSupportedRow(
                    scene,
                    commandIndex,
                    frameId,
                    in plan,
                    in identity,
                    in command,
                    mesh,
                    material,
                    renderOptions,
                    ref checkedHeaderMaterial,
                    ref checkedHeaderHandle,
                    ref reusedStaticPlans))
                return false;
            ++supportedRows;
        }

        // Every active registration must belong to one row. Otherwise the full
        // plan would release and tombstone the unseen registrations.
        if (supportedRows != _legacyMappingCount ||
            supportedRows != CountActiveRegistrations())
            return false;

        // One read of the dispatcher epoch covers every covered row. The epoch
        // advances when a committed bounds source changes, and the read fails
        // while the published output page is not usable.
        return boundsDispatcher is null ||
            _planBoundsEpochCaptured &&
            ReferenceEquals(boundsDispatcher, GPUPhysicsChainDispatcher.Instance) &&
            boundsDispatcher.TryReadCommittedBoundsEpoch(out long epoch) &&
            epoch == _planBoundsEpoch;
    }

    /// <summary>
    /// Returns true when an unregistered row is rejected again with the same
    /// reason. The full plan validates the geometry first and then selects the
    /// material layout. Both steps depend only on the inputs read here.
    /// A rejection in a later step, such as resource encoding, fails the gate.
    /// </summary>
    private static bool IsUnchangedUnsupportedRow(
        in AdvancedGpuSceneCommandTransition plan,
        in OrderedRegistrationIdentity identity,
        in DrawMetadata command,
        XRMesh? mesh,
        XRMaterial? material,
        ref int materialResolveAttempts)
    {
        if (identity.RegistrationIndex >= 0 || identity.Draw.IsValid)
            return false;
        if (!TryValidateCanonicalGeometry(mesh, out EAdvancedCanonicalCompatibilityReason geometryReason))
            return geometryReason == plan.CompatibilityReason;

        ++materialResolveAttempts;
        return !TrySelectCanonicalMaterialLayout(
                material,
                in command,
                out _,
                out _,
                out _,
                out EAdvancedCanonicalCompatibilityReason layoutReason) &&
            layoutReason == plan.CompatibilityReason;
    }

    /// <summary>
    /// Returns true when a supported row has the inputs of its retained plan and
    /// its registration still holds the committed result of that plan.
    /// </summary>
    private bool IsUnchangedSupportedRow(
        GPUScene scene,
        int commandIndex,
        ulong frameId,
        in AdvancedGpuSceneCommandTransition plan,
        in OrderedRegistrationIdentity identity,
        in DrawMetadata command,
        XRMesh? mesh,
        XRMaterial? material,
        RenderingParameters? renderOptions,
        ref XRMaterial? checkedHeaderMaterial,
        ref AdvancedGpuHandle checkedHeaderHandle,
        ref int reusedStaticPlans)
    {
        int registrationIndex = identity.RegistrationIndex;
        // Only rows that reused the committed material plan prove that the
        // committed material row is current for these material inputs.
        if (!plan.ReusedCommittedMaterial ||
            registrationIndex < 0 ||
            registrationIndex != plan.RegistrationIndex ||
            registrationIndex >= _registrationCount ||
            mesh is null ||
            !ReferenceEquals(mesh, plan.Mesh) ||
            !ReferenceEquals(material, plan.MaterialSource) ||
            !IsUntexturedCanonicalMaterial(material))
            return false;

        if (Math.Max(0, mesh.VertexCount) != plan.MeshVertexCount ||
            Math.Max(0, mesh.IndexCount) != plan.MeshIndexCount ||
            mesh.GeometryRevision != plan.MeshGeometryRevision ||
            mesh.Type != plan.MeshPrimitiveTopology ||
            mesh.Triangles is not { } triangles ||
            (long)triangles.Count * 3L != plan.MeshIndexCount ||
            (material?.BindingLayoutVersion ?? 0u) != plan.MaterialBindingLayoutVersion ||
            (material?.BindingValueVersion ?? 0u) != plan.MaterialBindingValueVersion ||
            (material?.BindingResourceVersion ?? 0u) != plan.MaterialBindingResourceVersion ||
            (material?.ShaderStateRevision ?? 0L) != plan.MaterialShaderStateRevision ||
            (material?.UberStateRevision ?? 0L) != plan.MaterialUberStateRevision ||
            !SameRenderState(CreateRenderState(mesh, renderOptions, in command), in plan.RenderState))
            return false;

        // A CPU-owned bound enters the content signature through its version.
        if (plan.GpuBoundsSource is null &&
            ReadCullBounds(scene, in command).BoundsVersion != plan.Bounds.BoundsVersion)
            return false;

        ref readonly AdvancedResidentRegistration registration =
            ref _registrations[registrationIndex];
        ref readonly AdvancedCommittedMaterialPlan cache =
            ref registration.CommittedMaterialPlan;
        // A frame gap or a changed temporal input gives a temporal event,
        // which the full plan publishes as a mutation.
        if (!registration.Active ||
            !ReferenceEquals(registration.Source, plan.Source) ||
            registration.PrimitiveIndex != plan.PrimitiveIndex ||
            !identity.Draw.IsValid ||
            registration.Draw != identity.Draw ||
            registration.LegacyCommandIndex != (uint)commandIndex ||
            registration.LastSeenFrameId == 0UL ||
            unchecked(registration.LastSeenFrameId + 1UL) != frameId ||
            registration.StructuralSignature != plan.StructuralSignature ||
            registration.ContentSignature != plan.ContentSignature ||
            !ReferenceEquals(registration.TemporalMesh, mesh) ||
            registration.TemporalGeometryRevision != plan.MeshGeometryRevision ||
            registration.TemporalVertexCount != plan.MeshVertexCount ||
            registration.TemporalIndexCount != plan.MeshIndexCount ||
            registration.TemporalPrimitiveTopology != plan.MeshPrimitiveTopology ||
            registration.TemporalSkinned != plan.MeshIsSkinned ||
            !ReferenceEquals(registration.MaterialSource, material) ||
            registration.MaterialBindingLayoutVersion != plan.MaterialBindingLayoutVersion ||
            registration.MaterialBindingValueVersion != plan.MaterialBindingValueVersion ||
            registration.MaterialBindingResourceVersion != plan.MaterialBindingResourceVersion ||
            registration.MaterialShaderStateRevision != plan.MaterialShaderStateRevision ||
            registration.MaterialUberStateRevision != plan.MaterialUberStateRevision ||
            !cache.Valid ||
            cache.Layout is not { } layout ||
            cache.Draw != registration.Draw ||
            cache.MaterialHandle != registration.Material ||
            !IsRegistrationCurrent(in registration))
            return false;

        // The material versions do not cover every header input, for example a
        // vertex uniform handler. The full plan checks the header for each
        // material, so check it one time for each run of rows with one material.
        if (!ReferenceEquals(material, checkedHeaderMaterial) ||
            registration.Material != checkedHeaderHandle)
        {
            if (!_materialPublisher.HeaderMatches(
                    registration.Material,
                    material,
                    layout,
                    cache.Coverage,
                    cache.State,
                    ReadOnlySpan<AdvancedMaterialTextureBinding>.Empty))
                return false;
            checkedHeaderMaterial = material;
            checkedHeaderHandle = registration.Material;
        }

        if (plan.GpuBoundsSource is not null &&
            plan.World == Matrix4x4.Identity &&
            plan.PreviousWorld == Matrix4x4.Identity &&
            cache.GpuOwnedBounds &&
            cache.IdentityWorld)
            ++reusedStaticPlans;
        return true;
    }

    /// <summary>
    /// Returns true when the full plan would capture the same bounds route.
    /// A covered row needs the same renderer and bone set; the caller then
    /// checks the dispatcher epoch one time for all covered rows.
    /// </summary>
    private static bool IsGpuBoundsSourceUnchanged(
        XRMeshRenderer? renderer,
        in PhysicsChainGpuBoundsSource? retained,
        ref GPUPhysicsChainDispatcher? dispatcher)
    {
        bool covered = renderer is not null && renderer.HasCompleteGpuDrivenBoneCoverage;
        if (retained is not { } source)
            return !covered;
        if (!covered ||
            !ReferenceEquals(source.Renderer, renderer) ||
            renderer!.BoneBufferGeneration != source.RendererBoneBufferGeneration ||
            dispatcher is not null && !ReferenceEquals(dispatcher, source.Dispatcher))
            return false;
        dispatcher = source.Dispatcher;
        return true;
    }

    /// <summary>Matches the material rule of the committed-plan reuse path.</summary>
    private static bool IsUntexturedCanonicalMaterial(XRMaterial? material)
        => material is not AdvancedProjectiveMirrorMaterial &&
           (material is null ||
            material.SurfaceTextureBindings.Length == 0 && material.Textures.Count == 0);

    private bool IsRetainedSourceGroupCurrent(
        in OrderedRegistrationIdentity identity,
        IRenderCommandMesh source,
        int sourcePrimitiveCount)
    {
        int groupIndex = identity.GroupIndex;
        return (uint)groupIndex < (uint)_retainedIdentitySourceCount &&
            ReferenceEquals(_retainedIdentitySources[groupIndex], source) &&
            _retainedIdentityPrimitiveCounts[groupIndex] == sourcePrimitiveCount;
    }

    private int CountActiveRegistrations()
    {
        int count = 0;
        for (int index = 0; index < _registrationCount; ++index)
            if (_registrations[index].Active)
                ++count;
        return count;
    }

    /// <summary>
    /// Publishes a frame whose rows passed the gate. Returns false, before any
    /// mutation, when only the full plan can publish this frame.
    /// </summary>
    private bool TryPublishUnchangedRows(
        ulong frameId,
        in AdvancedGlobalResourceCapture globalResources,
        int reusedStaticPlans,
        int materialResolveAttempts)
    {
        AdvancedGpuScenePublicationReference candidate = _currentPublication;
        // A reuse needs the retained image; a commit needs the retained rows.
        bool publicationRetained = IsPublicationRetainedForReuse(in candidate);
        if (!publicationRetained && !_committedSubmissionRowsValid)
            return false;

        // No row changes material or resource ownership in this publication.
        _plannedMaterialCount = 0;
        _plannedMaterialReleaseCount = 0;
        _resourceAcquireCount = 0;
        _resourceReleaseCount = 0;
        bool hasGlobalResources;
        string globalResourceFailure;
        using (RuntimeEngine.Profiler.Start("GpuIndirect.AdvancedPublication.GlobalPreflight"))
            hasGlobalResources = TryPreflightGlobalResources(in globalResources, out globalResourceFailure);
        bool resourceMutation = hasGlobalResources && HasPlannedResourceMutation();
        bool requiresCommit = resourceMutation || !publicationRetained;
        // The full plan repeats the global preflight; it has no other effect.
        if (hasGlobalResources && requiresCommit && !_committedSubmissionRowsValid)
            return false;

        _useRetainedIdentityGroups = true;
        _validatedOrderedRegistrationRowCount = _plannedCommandCount;
        _lastRegistrationIdentityReuseCount = _plannedCommandCount;
        _lastSourceGroupReuseCount = _retainedIdentitySourceCount;
        _reusedStaticDrawPlanCount = reusedStaticPlans;
        _materialResolveAttemptCount = materialResolveAttempts;
        if (!hasGlobalResources)
        {
            RejectPublication(globalResourceFailure);
            return true;
        }

        _lastUnchangedRowGateHitCount = _plannedCommandCount;
        if (requiresCommit)
            CommitRetainedRows(frameId, resourceMutation);
        else
            ReuseUnchangedRows(frameId, in candidate);
        return true;
    }

    /// <summary>
    /// Applies the retention rule of the full reuse check. The lease release
    /// drains acknowledged publications that have no other lease, so a
    /// publication that the ring would drain is never reused.
    /// </summary>
    private bool IsPublicationRetainedForReuse(in AdvancedGpuScenePublicationReference candidate)
    {
        if (!Database.TryAcquirePublicationLease(
                in candidate,
                EAdvancedGpuScenePublicationPinKind.Package,
                out AdvancedGpuScenePublicationLease inspectionLease))
            return false;

        inspectionLease.Dispose();
        // The inspection lease may have been the last pin.
        return Database.TryGetPublicationSnapshot(in candidate, out _);
    }

    /// <summary>
    /// Reuses the current publication and refreshes every source identity, as
    /// the full plan does when it proves that nothing changed.
    /// </summary>
    private void ReuseUnchangedRows(
        ulong frameId,
        in AdvancedGpuScenePublicationReference publication)
    {
        _sequence = publication.Sequence;
        StampUnchangedRows(frameId);
        _identityDeliveryIncomplete = true;
        try
        {
            PublishSourceDrawIdentities(in publication);
            _identityDeliveryIncomplete = false;
            ClearPendingIdentityRecipients();
        }
        catch (Exception exception)
        {
            _committedMaterialPlanCacheValid = false;
            _identityDeliveryIncomplete = true;
            RetainFailedIdentityRecipients();
            InvalidateRegistrationIdentityCache();
            RejectPublication(exception.Message);
            throw;
        }

        ClearPublicationFailure();
        // A reuse counts only after every source identity has been refreshed.
        S13aPublicationTelemetry.PublicationReuse();
        S13aPublicationTelemetry.Trace(S13aPublicationTraceEventKind.PublicationReused,
            publicationSequence: publication.Sequence, frameId: frameId,
            databaseEpoch: publication.Publication.DatabaseEpoch,
            frameGeneration: publication.Publication.FrameGeneration,
            topologyGeneration: publication.Publication.TopologyGeneration,
            contentGeneration: publication.Publication.ContentGeneration,
            lookupGeneration: publication.Publication.LookupGeneration);
    }

    /// <summary>
    /// Commits the retained submission rows with the preflighted global resource
    /// transition. Row owners, draw handles, and legacy mappings do not change,
    /// so the per-command apply, tombstone, and plan-capture passes are skipped.
    /// Every source still receives the new publication identity.
    /// </summary>
    private void CommitRetainedRows(ulong frameId, bool resourceMutation)
    {
        if (resourceMutation)
        {
            S13aPublicationTelemetry.PublicationResourceMutation();
            S13aPublicationTelemetry.Trace(S13aPublicationTraceEventKind.PublicationResourceMutation,
                publicationSequence: _currentPublication.Sequence, frameId: frameId);
        }
        else
        {
            S13aPublicationTelemetry.PublicationExpired();
            S13aPublicationTelemetry.Trace(S13aPublicationTraceEventKind.PublicationExpired,
                publicationSequence: _currentPublication.Sequence, frameId: frameId);
        }

        if (AdvancedPublicationFaultInjection.TryConsumePreflightRejection())
        {
            RejectPublication("Injected validation rejection before opening a publication.");
            return;
        }
        if (!Database.TryBeginPublication(
                out AdvancedGpuScenePublicationTransaction transaction))
        {
            RejectPublication("The canonical publication ring has no reusable slot.");
            return;
        }
        _sequence = transaction.Sequence;
        _lastPublicationCommittedRetainedRows = true;
        AdvancedGpuScenePublicationReference provisional = default;
        bool publicationCommitted = false;

        try
        {
            using (RuntimeEngine.Profiler.Start("GpuIndirect.AdvancedPublication.ApplyTransitions"))
            {
                // With no planned material, these steps apply only the global
                // resource acquisitions, rows, and releases.
                ApplyPreflightedMaterialTransitions();
                ApplyPreflightedGlobalResources();
                StampUnchangedRows(frameId);
                CompletePreflightedMaterialTransitions();
                if (HasDirtyLogicalLookups())
                    AdvanceNonZero(ref _lookupGeneration);
            }

            // The gate proved frame continuity and unchanged temporal inputs, so
            // the full plan would mark every retained row as temporally valid.
            Span<AdvancedDrawSubmissionRecord> records =
                _plannedSubmissionRecords.AsSpan(0, _legacyMappingCount);
            for (int index = 0; index < records.Length; ++index)
                records[index].TemporalEventReason = EAdvancedVelocityValidityReason.Valid;

            SealAndDeliverPublication(
                in transaction,
                frameId,
                ref provisional,
                ref publicationCommitted);
            _identityDeliveryIncomplete = false;
            ClearPendingIdentityRecipients();
            ClearPublicationFailure();
            MarkUnchangedRowBaseline(submissionRowsValid: true);
        }
        catch (Exception exception)
        {
            HandleOpenTransactionFailure(
                exception,
                in transaction,
                in provisional,
                publicationCommitted);
            throw;
        }
    }

    /// <summary>Marks every registered row as seen at this boundary.</summary>
    private void StampUnchangedRows(ulong frameId)
    {
        for (int commandIndex = 0; commandIndex < _plannedCommandCount; ++commandIndex)
        {
            int registrationIndex = _orderedRegistrationIdentities[commandIndex].RegistrationIndex;
            if (registrationIndex < 0)
                continue;
            ref AdvancedResidentRegistration registration = ref _registrations[registrationIndex];
            registration.LastSeenSequence = _sequence;
            registration.LastSeenFrameId = frameId;
        }
    }
}
