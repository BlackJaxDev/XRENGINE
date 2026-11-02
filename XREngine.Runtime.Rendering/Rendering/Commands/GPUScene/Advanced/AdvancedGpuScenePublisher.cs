using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Data.Rendering;
using XREngine.Rendering.Compute;
using XREngine.Rendering.Materials;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Dual-publishes legacy <see cref="GPUScene"/> residents into the canonical
/// shared database at the scene swap boundary. Registrations retain handles
/// allocated by the canonical tables; this type never manufactures logical IDs.
/// </summary>
public sealed partial class AdvancedGpuScenePublisher : IDisposable
{
    private const uint InitialCapacity = 64u;

    private AdvancedResidentRegistration[] _registrations =
        new AdvancedResidentRegistration[InitialCapacity];
    private AdvancedGpuHandle[] _commandDrawHandles =
        new AdvancedGpuHandle[InitialCapacity];
    private LegacyCanonicalDrawMapping[] _legacyMappings =
        new LegacyCanonicalDrawMapping[InitialCapacity];
    private int[] _registrationLookupIndices = new int[InitialCapacity * 2u];
    private uint[] _registrationLookupStamps = new uint[InitialCapacity * 2u];
    private uint[] _preflightSeenStamps = new uint[InitialCapacity];
    private readonly AdvancedGpuDirtyOwnerRange[] _dirtyOwnerRanges =
        new AdvancedGpuDirtyOwnerRange[18];
    private int _registrationCount;
    private int _legacyMappingCount;
    private ulong _sequence;
    private ulong _topologyGeneration;
    private ulong _contentGeneration;
    private ulong _lookupGeneration;
    private int _topologyDeltaCount;
    private int _contentDeltaCount;
    private int _reusedStaticDrawPlanCount;
    private int _materialResolveAttemptCount;
    private int _lastGeometryCompactionReplacementCount;
    private ulong _lastGeometryCompactionReclaimedBytes;
    private ulong _geometryCompactionCount;
    private ulong _geometryCompactionReclaimedBytes;
    private bool _publicationRejected;
    private string? _lastPublicationFailure;
    private AdvancedGpuScenePublicationReference _currentPublication;
    private bool _identityDeliveryIncomplete;
    private int _publishInProgress;
    // Advances in the finally block of each Publish call, before the call
    // clears _publishInProgress. A value read while no publication runs stays
    // equal until the next Publish call completes.
    private long _publisherEpoch;
    // The frame id of the most recent publication that stamped its rows. Each
    // stamp site sets it after its row loop, also when no row has a
    // registration, so it equals LastSeenFrameId of each row that the loop
    // stamped. A rejection clears it to zero, and it is zero before the first stamp.
    private ulong _lastStampedFrameId;
    private int _dirtyOwnerRangeCount;
    private uint _registrationLookupGeneration;
    private uint _preflightSeenGeneration;
    private readonly AdvancedGpuResourcePublisher _resourcePublisher;
    private readonly AdvancedGpuMaterialPublisher _materialPublisher;

    public AdvancedGpuScenePublisher()
    {
        Database = new AdvancedSharedGpuSceneDatabase(
            CreateCapacityProfile(InitialCapacity));
        _resourcePublisher = new AdvancedGpuResourcePublisher(Database.Resources);
        _materialPublisher = new AdvancedGpuMaterialPublisher(
            Database.Materials,
            InitialCapacity);
    }

    public AdvancedSharedGpuSceneDatabase Database { get; }

    /// <summary>
    /// Owns renderer-neutral texture and sampler identities at the same scene
    /// publication boundary.
    /// </summary>
    public AdvancedGpuResourcePublisher ResourcePublisher => _resourcePublisher;

    /// <summary>Owns shared canonical material variants and their draw references.</summary>
    public AdvancedGpuMaterialPublisher MaterialPublisher => _materialPublisher;

    public ulong Sequence => _sequence;

    public ulong TopologyGeneration => _topologyGeneration;

    public ulong ContentGeneration => _contentGeneration;

    public ulong LookupGeneration => _lookupGeneration;

    public int TopologyDeltaCount => _topologyDeltaCount;

    public int ContentDeltaCount => _contentDeltaCount;

    /// <summary>Covered draw plans reused in the most recent preflight.</summary>
    public int ReusedStaticDrawPlanCount => _reusedStaticDrawPlanCount;

    /// <summary>Material resolver calls in the most recent preflight.</summary>
    public int MaterialResolveAttemptCount => _materialResolveAttemptCount;

    /// <summary>Geometry rows rebound by the most recently applied compaction.</summary>
    public int LastGeometryCompactionReplacementCount
        => _lastGeometryCompactionReplacementCount;

    /// <summary>Canonical arena bytes reclaimed by the most recent compaction.</summary>
    public ulong LastGeometryCompactionReclaimedBytes
        => _lastGeometryCompactionReclaimedBytes;

    /// <summary>Cumulative geometry arena generation replacements.</summary>
    public ulong GeometryCompactionCount => _geometryCompactionCount;

    /// <summary>Cumulative dead arena bytes omitted from successor generations.</summary>
    public ulong GeometryCompactionReclaimedBytes
        => _geometryCompactionReclaimedBytes;

    public bool PublicationRejected => _publicationRejected || _identityDeliveryIncomplete;

    /// <summary>
    /// Gets the most recent reason a canonical scene publication was rejected.
    /// The value remains available until a later publication commits or reuses
    /// a verified unchanged resident image.
    /// </summary>
    public string? LastPublicationFailure => _lastPublicationFailure;

    public bool PublicationFaulted => Database.PublicationFaulted;

    /// <summary>
    /// Gets the number of completed <see cref="Publish"/> calls. Each call
    /// advances it at its end, so two equal reads prove that no publication
    /// completed between them.
    /// </summary>
    internal long PublisherEpoch => System.Threading.Interlocked.Read(ref _publisherEpoch);

    public AdvancedGpuScenePublicationReference CurrentPublication
        => _identityDeliveryIncomplete ? default : _currentPublication;

    public ReadOnlySpan<LegacyCanonicalDrawMapping> LegacyMappings
        => _publicationRejected || _identityDeliveryIncomplete || Database.PublicationFaulted
            ? ReadOnlySpan<LegacyCanonicalDrawMapping>.Empty
            : _legacyMappings.AsSpan(0, _legacyMappingCount);

    public ReadOnlySpan<AdvancedGpuDirtyOwnerRange> DirtyOwnerRanges
        => _publicationRejected || _identityDeliveryIncomplete || Database.PublicationFaulted
            ? ReadOnlySpan<AdvancedGpuDirtyOwnerRange>.Empty
            : _dirtyOwnerRanges.AsSpan(0, _dirtyOwnerRangeCount);

    /// <summary>
    /// Publishes one immutable render-side scene snapshot. Callers must invoke
    /// this only from <see cref="GPUScene.SwapCommandBuffers"/> while holding the
    /// scene mutation lock.
    /// </summary>
    public void Publish(
        GPUScene scene,
        ulong frameId,
        in AdvancedGlobalResourceCapture globalResources)
    {
        // Property notifications during identity delivery can call back into
        // the publisher. A nested publication would overwrite the outer plan.
        if (System.Threading.Interlocked.CompareExchange(ref _publishInProgress, 1, 0) != 0)
            throw new InvalidOperationException("Canonical scene publication is already in progress.");
        // PublishCore sets this mark at the gate decision. It stays 0 when the
        // publication stops before that point, or when the world swap section
        // is not open on this thread.
        long stageMark = 0L;
        try
        {
            PublishCore(scene, frameId, in globalResources, ref stageMark);
        }
        finally
        {
            try
            {
                ReleasePlannedMirrorSnapshots();
            }
            finally
            {
                // Boundary sizing can retain source payloads when a later stage
                // rejects. Only copied arena bytes belong to a publication.
                Array.Clear(_stagedGeometry, 0, Math.Min(_stagedGeometry.Length, _plannedCommandCount));
                // PublishCore took the prepared verdict. Its witnesses must not
                // keep materials or options reachable after this publication.
                ReleasePreparedWitnesses();
                // Advance the epoch before the in-progress flag clears, so a
                // reader that sees no publication in progress also sees the epoch
                // of every completed publication.
                System.Threading.Interlocked.Increment(ref _publisherEpoch);
                System.Threading.Volatile.Write(ref _publishInProgress, 0);
            }
        }
        // The commit stage ends after the scratch release, so that it holds all
        // of the work after the gate decision.
        CollectVisibleStageTimings.End(ECollectVisibleStage.SwapAdvancedCommit, stageMark);
    }

    /// <summary>
    /// Runs one publication. <paramref name="stageMark"/> receives the swap stage
    /// mark at the gate decision, and it stays 0 outside the world swap section.
    /// </summary>
    private void PublishCore(
        GPUScene scene,
        ulong frameId,
        in AdvancedGlobalResourceCapture globalResources,
        ref long stageMark)
    {
        // A prepared verdict is single use. Take it before any check, so that
        // no exit path leaves it for a later publication.
        AdvancedPreparedUnchangedRowGate prepared = TakePreparedUnchangedRowGate();
        // Every global-resource preflight rebuilds the authored decal rows.
        Array.Clear(_plannedDecals, 0, _plannedDecalCount);
        _plannedDecalCount = 0;
        _topologyDeltaCount = 0;
        _contentDeltaCount = 0;
        _lastGeometryCompactionReplacementCount = 0;
        _lastGeometryCompactionReclaimedBytes = 0u;
        _dirtyOwnerRangeCount = 0;
        _publicationRejected = false;
        _lastRegistrationIdentityReuseCount = 0;
        _lastRegistrationLookupRebuildCount = 0;
        _validatedOrderedRegistrationRowCount = 0;
        _lastSourceGroupReuseCount = 0;
        _lastSourceGroupRebuildCount = 0;
        _lastUnchangedRowGateHitCount = 0;
        _lastPublicationCommittedRetainedRows = false;
        _publicationStartMembershipGeneration = _registrationMembershipGeneration;
        _useRetainedIdentityGroups = false;

        if (Database.PublicationFaulted)
        {
            ReleasePlanScratch();
            RejectPublication("The canonical scene publication database is faulted.");
            return;
        }

        int capturedGlobalResourceCount = globalResources.FrameId == frameId
            ? Math.Max(Math.Max(globalResources.Lights.Length, globalResources.Probes.Length), globalResources.AuthoredDecals.Count)
            : 0;
        bool hasBoundaryCapacity;
        bool boundaryGrew;
        using (RuntimeEngine.Profiler.Start("GpuIndirect.AdvancedPublication.BoundaryCapacity"))
            hasBoundaryCapacity = EnsureBoundaryCapacity(
                scene.TotalCommandCount,
                checked((uint)capturedGlobalResourceCount),
                out boundaryGrew);
        if (!hasBoundaryCapacity)
        {
            ReleasePlanScratch();
            RejectPublication("The canonical resident tables cannot grow at this frame boundary.");
            return;
        }
        // Preflight checks each table's delta-journal capacity before opening
        // the next publication. Reclaim acknowledged deltas here so a full
        // journal cannot prevent the transaction that would normally reclaim it.
        Database.ReclaimAcknowledgedTombstones();
        AdvancedGlobalResourceCapture acceptedGlobalResources =
            globalResources.FrameId == frameId
                ? globalResources
                : AdvancedGlobalResourceCapture.Empty(frameId);

        // The swap stage timers split the publication here. Only the collect
        // thread in the world swap section gets a nonzero mark, the same gate
        // as the parent publication stage of the GPU-scene swap.
        stageMark = CollectVisibleStageTimings.StartIn(ECollectVisibleSection.WorldSwap);

        // The retained plan, rows, and draw handles describe the current
        // publication. When every row input is unchanged, skip the whole-scene plan.
        // A verdict that the collect thread prepared replaces the row walk; any
        // miss runs the swap-time gate. The verify mode runs the swap-time gate
        // inside the consume, so the gate then does not run a second time.
        if (!boundaryGrew)
        {
            bool rowsUnchanged;
            int reusedStaticPlans;
            int materialResolveAttempts;
            using (RuntimeEngine.Profiler.Start("GpuIndirect.AdvancedPublication.UnchangedRowGate"))
            {
                rowsUnchanged = TryConsumePreparedUnchangedRowGate(
                    scene,
                    frameId,
                    in prepared,
                    recordTelemetry: stageMark != 0L,
                    out bool gateDecided,
                    out reusedStaticPlans,
                    out materialResolveAttempts);
                if (!gateDecided)
                    rowsUnchanged = TryPassUnchangedRowGate(
                        scene, frameId, out reusedStaticPlans, out materialResolveAttempts);
            }
            CollectVisibleStageTimings.MarkAdvancedRowGate(ref stageMark, rowsUnchanged);
            if (rowsUnchanged && TryPublishUnchangedRows(
                    frameId, in acceptedGlobalResources, reusedStaticPlans, materialResolveAttempts))
            {
                CollectVisibleStageTimings.CountAdvancedUnchangedRowPublication(stageMark);
                return;
            }
        }

        // Plans and submission sources are scratch closures, not publication leases.
        // Release the prior frame's references even if this boundary rejects early.
        ReleasePlanScratch();
        _useRetainedIdentityGroups = false;
        Array.Clear(
            _commandDrawHandles,
            0,
            checked((int)scene.TotalCommandCount));
        bool hasScenePlan;
        string planFailure;
        using (RuntimeEngine.Profiler.Start("GpuIndirect.AdvancedPublication.ScenePlan"))
        {
            if (_registrationLookupNeedsRebuild)
                RebuildRegistrationLookup();
            hasScenePlan = TryBuildAndPreflightWholeScenePlan(scene, frameId, out planFailure);
        }
        if (!hasScenePlan)
        {
            RejectPublication(planFailure);
            return;
        }
        AdvancedGeometryCompactionPlan? geometryCompaction;
        bool hasGeometryCapacity;
        using (RuntimeEngine.Profiler.Start("GpuIndirect.AdvancedPublication.GeometryCapacity"))
            hasGeometryCapacity = TryEnsurePlannedGeometryBoundaryCapacity(out geometryCompaction);
        if (!hasGeometryCapacity)
        {
            RejectPublication("The canonical geometry arenas cannot satisfy the planned append.");
            return;
        }
        if (!CanApplyPlannedSceneMutations(
                geometryCompaction?.ReplacementCount ?? 0))
        {
            RejectPublication("The canonical geometry compaction exceeds the bounded publication journal.");
            return;
        }
        if (!TryStagePlannedGeometry(out string geometryCaptureFailure))
        {
            RejectPublication(geometryCaptureFailure);
            return;
        }
        bool hasGlobalResources;
        string globalResourceFailure;
        using (RuntimeEngine.Profiler.Start("GpuIndirect.AdvancedPublication.GlobalPreflight"))
            hasGlobalResources = TryPreflightGlobalResources(in acceptedGlobalResources, out globalResourceFailure);
        if (!hasGlobalResources)
        {
            RejectPublication(globalResourceFailure);
            return;
        }
        bool reusedPublication;
        using (RuntimeEngine.Profiler.Start("GpuIndirect.AdvancedPublication.ReuseCheck"))
            reusedPublication = TryReuseUnchangedPublication(frameId);
        if (reusedPublication)
        {
            ClearPublicationFailure();
            // A reuse writes no submission rows, so only the plan is retained.
            MarkUnchangedRowBaseline(submissionRowsValid: false);
            return;
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
        AdvancedGpuScenePublicationReference provisional = default;
        bool publicationCommitted = false;

        try
        {
            using (RuntimeEngine.Profiler.Start("GpuIndirect.AdvancedPublication.ApplyTransitions"))
            {
                if (geometryCompaction is not null)
                {
                    if (!Database.TryApplyGeometryCompaction(
                            in transaction,
                            geometryCompaction))
                    {
                        Database.FaultActivePublication(
                            in transaction,
                            EAdvancedGpuScenePublicationFault.InvariantFailure);
                        throw new InvalidOperationException(
                            "Canonical geometry compaction failed after a successful publication preflight.");
                    }

                    ++_contentDeltaCount;
                    AdvanceNonZero(ref _contentGeneration);
                    _lastGeometryCompactionReplacementCount = geometryCompaction.ReplacementCount;
                    _lastGeometryCompactionReclaimedBytes = geometryCompaction.ReclaimedBytes;
                    _geometryCompactionCount++;
                    _geometryCompactionReclaimedBytes += geometryCompaction.ReclaimedBytes;
                }
                ApplyPreflightedMaterialTransitions();
                ApplyPreflightedGlobalResources();

                for (uint commandIndex = 0u;
                     commandIndex < scene.TotalCommandCount;
                     ++commandIndex)
                {
                    ref readonly AdvancedGpuSceneCommandTransition plan =
                        ref _plannedCommands[checked((int)commandIndex)];
                    if (!plan.Supported || plan.Source is not { })
                    {
                        _commandDrawHandles[commandIndex] =
                            AdvancedGpuHandle.Invalid;
                        continue;
                    }

                    AdvancedGpuHandle material =
                        _plannedMaterialRequests[plan.MaterialPlanIndex].MaterialHandle;
                    int registrationIndex = plan.RegistrationIndex;
                    if (registrationIndex < 0)
                    {
                        registrationIndex = TryAddRegistration(checked((int)commandIndex), in plan, material, out string? registrationFailure);
                        if (registrationIndex < 0)
                        {
                            throw new InvalidOperationException(
                                $"Canonical registration failed for command {commandIndex}, primitive {plan.PrimitiveIndex}: {registrationFailure ?? "unknown stage"}.");
                        }
                    }
                    else
                    {
                        UpdateRegistration(checked((int)commandIndex), registrationIndex, in plan, material);
                    }

                    ref AdvancedResidentRegistration registration =
                        ref _registrations[registrationIndex];
                    registration.LastSeenSequence = _sequence;
                    registration.LastSeenFrameId = frameId;
                    registration.LegacyCommandIndex = commandIndex;
                    _commandDrawHandles[commandIndex] = registration.Draw;
                    int submissionIndex = _legacyMappingCount;
                    AppendLegacyMapping(
                        commandIndex,
                        plan.PrimitiveIndex,
                        in plan.Command,
                        in registration);
                    _plannedSubmissionRecords[submissionIndex] = new AdvancedDrawSubmissionRecord
                    {
                        Draw = registration.Draw,
                        Geometry = registration.Geometry,
                        Material = registration.Material,
                        Deformation = registration.Deformation,
                        StableQueryKey = plan.Source.StableQueryKey,
                        LegacyCommandIndex = commandIndex,
                        PrimitiveIndex = checked((uint)Math.Max(0, plan.PrimitiveIndex)),
                        PassIndex = plan.Command.RenderPass,
                        InstanceCount = Math.Max(1u, plan.Command.InstanceCount),
                        Flags = plan.Command.Flags,
                        StateClass = plan.Command.StateClassID,
                        CompatibilityReason = plan.CompatibilityReason,
                        TemporalEventReason = plan.TemporalEventReason,
                        SourceOrder = ((ulong)commandIndex << 32) | unchecked((uint)plan.PrimitiveIndex),
                        DependencySignature = Mix(plan.StructuralSignature, plan.ContentSignature),
                    };
                    _plannedDeformationSources[submissionIndex] = new AdvancedManagedDeformationSourceRow(
                        plan.Source,
                        plan.Source is RenderCommand renderCommand
                            ? renderCommand.OwnerRenderInfo
                            : null,
                        plan.Renderer,
                        plan.Mesh,
                        checked((uint)Math.Max(0, plan.MeshVertexCount)),
                        plan.ContentSignature, plan.StructuralSignature, plan.GpuBoundsSource,
                        new PhysicsChainDrawMaterialSnapshot(plan.MaterialSource,
                            plan.MaterialBindingLayoutVersion,
                            plan.MaterialBindingValueVersion,
                            plan.MaterialBindingResourceVersion,
                            plan.MaterialShaderStateRevision,
                            plan.MaterialUberStateRevision))
                    {
                        BrowserBasisSource = (_plannedMaterialRequests[plan.MaterialPlanIndex].EngineSurface.SurfaceKind == AdvancedEngineSurfaceRecord.AuthoredTexturedKind ||
                            _plannedMaterialRequests[plan.MaterialPlanIndex].UberBaseSurface.SchemaVersion != 0)
                            ? AdvancedBrowserGeometryBasisSource.Capture(plan.Mesh, plan.MeshGeometryRevision) : null,
                        BrowserUberAttributesSource = _plannedMaterialRequests[plan.MaterialPlanIndex].UberBaseSurface.SchemaVersion != 0
                            ? AdvancedBrowserUberAttributeSource.Capture(plan.Mesh, plan.MeshGeometryRevision) : null,
                        BrowserUberAttributesProducerRejection = plan.Renderer is { } attributeRenderer &&
                            (attributeRenderer.HasSettingUniformsHandlers || attributeRenderer.HasRenderDataPreparation || attributeRenderer.BindingPublishers.Count != 0 ||
                             attributeRenderer.Buffers.ContainsKey("TexCoord0") || attributeRenderer.Buffers.ContainsKey("TexCoord1") ||
                             attributeRenderer.Buffers.ContainsKey("TexCoord2") || attributeRenderer.Buffers.ContainsKey("TexCoord3") ||
                             attributeRenderer.Buffers.ContainsKey("Color0"))
                            ? "WebGPU.Advanced.UberAttributeOverrideUnsupported: renderer callbacks, publishers or UV/color overrides require their own frozen source companion." : null,
                        BrowserBasisProducerRejection = plan.Renderer is { } basisRenderer &&
                            (basisRenderer.Buffers.ContainsKey("Normal") || basisRenderer.Buffers.ContainsKey("Tangent"))
                            ? "WebGPU.Advanced.AuthoredBasisExternalStreamMetadataMissing: renderer normal/tangent overrides require a frozen basis-validity companion from that producer."
                            : null,
                    };
                }
                // Record the frame also when no row has a registration, so that
                // the next prepare expects the frame after this publication.
                _lastStampedFrameId = frameId;

                TombstoneMissingRegistrations();
                CompletePreflightedMaterialTransitions();
                bool lookupDirty = HasDirtyLogicalLookups();
                if (lookupDirty)
                    AdvanceNonZero(ref _lookupGeneration);
            }

            SealAndDeliverPublication(
                in transaction,
                frameId,
                ref provisional,
                ref publicationCommitted);
            CaptureCommittedMaterialPlans();
            _identityDeliveryIncomplete = false;
            ClearPendingIdentityRecipients();
            PromoteSourceGroups();
            PromoteOrderedRegistrations();
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

    /// <summary>
    /// Seals the prepared transaction with the planned submission rows and
    /// delivers the accepted publication to every source identity group.
    /// </summary>
    private void SealAndDeliverPublication(
        in AdvancedGpuScenePublicationTransaction transaction,
        ulong frameId,
        ref AdvancedGpuScenePublicationReference provisional,
        ref bool publicationCommitted)
    {
        AdvancedGpuScenePublicationReference committed;
        using (RuntimeEngine.Profiler.Start("GpuIndirect.AdvancedPublication.CaptureAndCommit"))
        {
            if (!Database.TryPreparePublication(
                    in transaction,
                    frameId,
                    _topologyGeneration,
                    _contentGeneration,
                    _lookupGeneration,
                    out provisional))
            {
                Database.FaultActivePublication(
                    in transaction,
                    EAdvancedGpuScenePublicationFault.SnapshotCaptureFailed);
                throw new InvalidOperationException(
                    $"Canonical scene snapshot capture failed after a successful whole-frame preflight: {Database.LastPreparationFailure ?? "unknown stage"}.");
            }

            if (!_resourcePublisher.TryCapturePublication(
                    provisional.Sequence,
                    provisional.Snapshot!.ResourcePayloads,
                    _plannedMirrorSnapshots.AsSpan(0, _plannedMirrorSnapshotCount)))
            {
                provisional.Snapshot.ResourcePayloads.AbortSourceCapture();
                Database.FaultActivePublication(
                    in transaction,
                    EAdvancedGpuScenePublicationFault.SnapshotCaptureFailed);
                throw new InvalidOperationException(
                    "Canonical resource source capture failed after sealing the logical resource image.");
            }

            if (!Database.TryCaptureSubmissionPublication(
                    in transaction,
                    _plannedSubmissionRecords.AsSpan(0, _legacyMappingCount),
                    _plannedDeformationSources.AsSpan(0, _legacyMappingCount)))
            {
                provisional.Snapshot!.ResourcePayloads.AbortSourceCapture();
                Database.FaultActivePublication(in transaction, EAdvancedGpuScenePublicationFault.SnapshotCaptureFailed);
                throw new InvalidOperationException("Canonical submission sidecar capture failed after table sealing.");
            }

            if (!Database.TryCommitPreparedPublication(
                    in transaction,
                    out committed))
            {
                provisional.Snapshot!.ResourcePayloads.AbortSourceCapture();
                Database.FaultActivePublication(
                    in transaction,
                    EAdvancedGpuScenePublicationFault.InvariantFailure);
                throw new InvalidOperationException(
                    "Canonical scene commit failed after preparing a complete publication.");
            }
            // Record acceptance before a host profiler scope can throw on disposal.
            publicationCommitted = true;
            // Hide the accepted image until every command has received its
            // corresponding identity. Notifications can call back into readers.
            _identityDeliveryIncomplete = true;
            _currentPublication = committed;
        }

        // Command snapshots become consumable only after the exact scene
        // publication has been accepted by the database.
        using (RuntimeEngine.Profiler.Start("GpuIndirect.AdvancedPublication.IdentityDelivery"))
        {
            CaptureAndClearDirtyOwnerRanges();
            // Report only accepted database publications, after the commit has
            // installed the exact tuple consumed by subsequent scene readers.
            S13aPublicationTelemetry.TracePublicationCommitted(
                committed.Publication, frameId);
            PublishSourceDrawIdentities(in committed);
        }
    }

    /// <summary>
    /// Aborts an open transaction, or hides an accepted publication whose
    /// identity delivery did not complete, and then rejects the publication.
    /// </summary>
    private void HandleOpenTransactionFailure(
        Exception exception,
        in AdvancedGpuScenePublicationTransaction transaction,
        in AdvancedGpuScenePublicationReference provisional,
        bool publicationCommitted)
    {
        _committedMaterialPlanCacheValid = false;
        if (!publicationCommitted)
        {
            provisional.Snapshot?.ResourcePayloads.AbortSourceCapture();
            Database.FaultActivePublication(
                in transaction,
                EAdvancedGpuScenePublicationFault.InvariantFailure);
        }
        else
        {
            // The database has accepted this publication. Hide it from
            // frame-package consumers until a later retry delivers every
            // command identity; it cannot be aborted as an active transaction.
            _identityDeliveryIncomplete = true;
            RetainFailedIdentityRecipients();
        }
        RejectPublication(exception.Message);
    }

    private void RejectPublication(string reason)
    {
        _publicationRejected = true;
        // A rejected boundary does not describe the current publication.
        _unchangedRowBaselineValid = false;
        _committedSubmissionRowsValid = false;
        _lastStampedFrameId = 0UL;
        InvalidateRegistrationIdentityCache();
        if (string.Equals(_lastPublicationFailure, reason, StringComparison.Ordinal))
            return;

        _lastPublicationFailure = reason;
        Debug.RenderingWarning(
            "[CanonicalPublication] Rejected: {0}",
            reason);
    }

    private void ClearPublicationFailure()
        => _lastPublicationFailure = null;

    /// <summary>
    /// Assigns renderer-facing identities only after the database sealed the
    /// publication. Primitive zero is the primary identity while the immutable
    /// snapshot retains every exact primitive handle for Vulkan submission.
    /// </summary>
    private void PublishSourceDrawIdentities(
        in AdvancedGpuScenePublicationReference publication)
        => PublishPreparedSourceDrawIdentities(in publication);

    private void CaptureAndClearDirtyOwnerRanges()
    {
        AdvancedGpuSceneDatabase scene = Database.Scene;
        _dirtyOwnerRangeCount = 0;
        Capture(EAdvancedGpuRecordOwner.Draw, scene.Draws);
        Capture(EAdvancedGpuRecordOwner.Instance, scene.Instances);
        Capture(EAdvancedGpuRecordOwner.Transform, scene.Transforms);
        Capture(EAdvancedGpuRecordOwner.Deformation, scene.Deformations);
        Capture(EAdvancedGpuRecordOwner.RenderState, scene.RenderStates);
        Capture(EAdvancedGpuRecordOwner.Material, Database.Materials.Materials);
        Capture(EAdvancedGpuRecordOwner.MaterialLayout, Database.Materials.Layouts);
        Capture(EAdvancedGpuRecordOwner.ShadingKernel, Database.Materials.Kernels);
        Capture(EAdvancedGpuRecordOwner.Texture, Database.Resources.Textures);
        Capture(EAdvancedGpuRecordOwner.Sampler, Database.Resources.Samplers);
        Capture(EAdvancedGpuRecordOwner.Light, Database.Resources.Lights);
        Capture(EAdvancedGpuRecordOwner.Shadow, Database.Resources.Shadows);
        Capture(EAdvancedGpuRecordOwner.Probe, Database.Resources.Probes);
        Capture(EAdvancedGpuRecordOwner.Environment, Database.Resources.Environments);
        Capture(EAdvancedGpuRecordOwner.Decal, Database.Resources.Decals);
        Capture(EAdvancedGpuRecordOwner.GiResource, Database.Resources.GiResources);
        Capture(EAdvancedGpuRecordOwner.Geometry, scene.Geometry.Records);
        Capture(EAdvancedGpuRecordOwner.EditorIdentity, scene.EditorIdentities);
    }

    private bool HasDirtyLogicalLookups()
    {
        AdvancedGpuSceneDatabase scene = Database.Scene;
        return !scene.Draws.LogicalLookupDirtyRange.IsEmpty ||
            !scene.Instances.LogicalLookupDirtyRange.IsEmpty ||
            !scene.Transforms.LogicalLookupDirtyRange.IsEmpty ||
            !scene.Deformations.LogicalLookupDirtyRange.IsEmpty ||
            !scene.RenderStates.LogicalLookupDirtyRange.IsEmpty ||
            !scene.EditorIdentities.LogicalLookupDirtyRange.IsEmpty ||
            !scene.Geometry.Records.LogicalLookupDirtyRange.IsEmpty ||
            !Database.Materials.Materials.LogicalLookupDirtyRange.IsEmpty ||
            !Database.Materials.Kernels.LogicalLookupDirtyRange.IsEmpty ||
            !Database.Materials.Layouts.LogicalLookupDirtyRange.IsEmpty ||
            !Database.Resources.Textures.LogicalLookupDirtyRange.IsEmpty ||
            !Database.Resources.Samplers.LogicalLookupDirtyRange.IsEmpty ||
            !Database.Resources.Lights.LogicalLookupDirtyRange.IsEmpty ||
            !Database.Resources.Shadows.LogicalLookupDirtyRange.IsEmpty ||
            !Database.Resources.Probes.LogicalLookupDirtyRange.IsEmpty ||
            !Database.Resources.Environments.LogicalLookupDirtyRange.IsEmpty ||
            !Database.Resources.Decals.LogicalLookupDirtyRange.IsEmpty ||
            !Database.Resources.GiResources.LogicalLookupDirtyRange.IsEmpty;
    }

    private void Capture<T>(
        EAdvancedGpuRecordOwner owner,
        AdvancedGpuRecordTable<T> table)
        where T : unmanaged
    {
        AdvancedGpuDirtyRange range = table.DirtyRange;
        if (!range.IsEmpty)
        {
            _dirtyOwnerRanges[_dirtyOwnerRangeCount++] =
                new AdvancedGpuDirtyOwnerRange(
                    owner,
                    range,
                    table.Generations.Content);
        }
        table.ClearDirtyRange();
    }

    public bool TryGetCanonicalHandles(
        uint commandIndex,
        out AdvancedGpuHandle draw,
        out AdvancedGpuHandle geometry,
        out AdvancedGpuHandle material)
        => TryGetCanonicalHandles(
            commandIndex,
            out draw,
            out geometry,
            out material,
            out _);

    public bool TryGetCanonicalHandles(
        uint commandIndex,
        out AdvancedGpuHandle draw,
        out AdvancedGpuHandle geometry,
        out AdvancedGpuHandle material,
        out AdvancedGpuHandle deformation)
    {
        draw = AdvancedGpuHandle.Invalid;
        geometry = AdvancedGpuHandle.Invalid;
        material = AdvancedGpuHandle.Invalid;
        deformation = AdvancedGpuHandle.Invalid;
        if (PublicationRejected || Database.PublicationFaulted ||
            commandIndex >= (uint)_commandDrawHandles.Length)
            return false;

        draw = _commandDrawHandles[commandIndex];
        if (!draw.IsValid ||
            !Database.Scene.Draws.TryGet(draw, out AdvancedDrawRecord record))
            return false;

        geometry = record.Geometry;
        material = record.Material;
        deformation = record.Deformation;
        return geometry.IsValid && material.IsValid && deformation.IsValid;
    }

    public bool TryGetCanonicalDraw(
        IRenderCommandMesh source,
        out AdvancedGpuHandle draw)
        => TryGetCanonicalDraw(source, 0, out draw);

    public bool TryGetCanonicalDraw(
        IRenderCommandMesh source,
        int primitiveIndex,
        out AdvancedGpuHandle draw)
    {
        if (PublicationRejected || Database.PublicationFaulted)
        {
            draw = AdvancedGpuHandle.Invalid;
            return false;
        }

        int index = FindRegistration(source, primitiveIndex);
        if (index >= 0)
        {
            draw = _registrations[index].Draw;
            return draw.IsValid;
        }

        draw = AdvancedGpuHandle.Invalid;
        return false;
    }

    public bool WasDrawAddedThisPublication(AdvancedGpuHandle draw)
    {
        if (PublicationRejected || Database.PublicationFaulted)
            return false;

        ReadOnlySpan<AdvancedGpuRecordPublicationDelta> deltas =
            Database.Scene.Draws.PublishedDeltas;
        for (int index = deltas.Length - 1; index >= 0; --index)
        {
            AdvancedGpuRecordPublicationDelta delta = deltas[index];
            if (delta.PublicationGeneration != _sequence || delta.Handle != draw)
                continue;
            return delta.Change == EAdvancedGpuRecordPublicationChange.Added;
        }
        return false;
    }

    private int TryAddRegistration(
        int commandIndex,
        in AdvancedGpuSceneCommandTransition plan,
        AdvancedGpuHandle material,
        out string? failureReason)
    {
        failureReason = null;
        IRenderCommandMesh source = plan.Source ??
            throw new InvalidOperationException("A supported command plan lost its source.");
        int primitiveIndex = plan.PrimitiveIndex;
        ref readonly DrawMetadata command = ref plan.Command;
        Matrix4x4 world = plan.World;
        Matrix4x4 previousWorld = plan.PreviousWorld;
        BoundsGpu bounds = plan.Bounds;

        AdvancedGpuSceneDatabase tables = Database.Scene;
        AdvancedGpuHandle currentTransform = AdvancedGpuHandle.Invalid;
        AdvancedGpuHandle previousTransform = AdvancedGpuHandle.Invalid;
        AdvancedGpuHandle instance = AdvancedGpuHandle.Invalid;
        AdvancedGpuHandle geometry = AdvancedGpuHandle.Invalid;
        AdvancedGpuHandle deformation = AdvancedGpuHandle.Invalid;
        AdvancedGpuHandle renderState = AdvancedGpuHandle.Invalid;
        AdvancedGpuHandle editorIdentity = AdvancedGpuHandle.Invalid;
        if (!material.IsValid)
        {
            failureReason = $"Material handle {material.Index}:{material.Generation} is invalid";
            return -1;
        }
        if (!Database.Materials.Materials.IsCurrent(material))
        {
            failureReason = $"Material handle {material.Index}:{material.Generation} is stale; materials={Database.Materials.Materials.Count}/{Database.Materials.Materials.Capacity}";
            return -1;
        }
        if (!CanAddRegistration(tables))
        {
            failureReason = DescribeRegistrationPreflightFailure(tables);
            return -1;
        }

        string? mutationFailure = null;
        if (!TryRegisterCanonicalGeometry(
                commandIndex,
                in bounds,
                in command,
                out AdvancedGpuHandle residentGeometry))
            mutationFailure = DescribeCanonicalGeometryRegistrationFailure(commandIndex, plan.MeshGeometryRevision);
        else if (!tables.Transforms.TryAdd(CreateTransform(world, plan.Source as RenderCommand), out currentTransform))
            mutationFailure = DescribeRegistrationTableFailure("current transform", tables.Transforms, 1);
        else if (!tables.Transforms.TryAdd(CreateTransform(previousWorld), out previousTransform))
            mutationFailure = DescribeRegistrationTableFailure("previous transform", tables.Transforms, 1);
        else if (!tables.Instances.TryAdd(CreateInstance(world, previousWorld, in bounds, in command), out instance))
            mutationFailure = DescribeRegistrationTableFailure("instance", tables.Instances, 1);
        else if (!(geometry = residentGeometry).IsValid)
            mutationFailure = "Geometry registration returned an invalid handle";
        else if (!tables.Deformations.TryAdd(CreateDeformation(geometry, plan.MeshVertexCount), out deformation))
            mutationFailure = DescribeRegistrationTableFailure("deformation", tables.Deformations, 1);
        else if (!tables.RenderStates.TryAdd(plan.RenderState, out renderState))
            mutationFailure = DescribeRegistrationTableFailure("render state", tables.RenderStates, 1);
        else if (!tables.EditorIdentities.TryAdd(CreateEditorIdentity(source, in command), out editorIdentity))
            mutationFailure = DescribeRegistrationTableFailure("editor identity", tables.EditorIdentities, 1);
        if (mutationFailure is not null)
        {
            RollBackPartialRegistration(
                currentTransform,
                previousTransform,
                instance,
                geometry,
                deformation,
                renderState,
                editorIdentity);
            failureReason = mutationFailure;
            return -1;
        }

        AdvancedDrawRecord drawRecord = new()
        {
            Instance = instance,
            Geometry = geometry,
            Material = material,
            Deformation = deformation,
            RenderState = renderState,
            EditorIdentity = editorIdentity,
            CurrentTransform = currentTransform,
            PreviousTransform = previousTransform,
            PrimitiveSection = checked((uint)Math.Max(0, primitiveIndex)),
            Flags = command.Flags & ~(uint)GPUIndirectRenderFlags.EditorHighlightMask,
            SourceFlatDirectionOct = plan.SourceFlatDirectionOct,
            SourceFlatDirectionValid = plan.SourceFlatDirectionValid,
        };
        if (!tables.Draws.TryAdd(drawRecord, out AdvancedGpuHandle draw))
        {
            RollBackPartialRegistration(
                currentTransform,
                previousTransform,
                instance,
                geometry,
                deformation,
                renderState,
                editorIdentity);
            failureReason = DescribeRegistrationTableFailure("draw", tables.Draws, 1);
            return -1;
        }

        int index = FindReusableRegistrationIndex();
        if (index < 0)
            index = _registrationCount++;
        _registrations[index] = new AdvancedResidentRegistration
        {
            Source = source,
            PrimitiveIndex = primitiveIndex,
            Draw = draw,
            Instance = instance,
            Geometry = geometry,
            Deformation = deformation,
            Material = material,
            RenderState = renderState,
            EditorIdentity = editorIdentity,
            CurrentTransform = currentTransform,
            PreviousTransform = previousTransform,
            World = world,
            StructuralSignature = plan.StructuralSignature,
            ContentSignature = plan.ContentSignature,
            MaterialSource = plan.MaterialSource,
            MaterialBindingLayoutVersion = plan.MaterialBindingLayoutVersion,
            MaterialBindingValueVersion = plan.MaterialBindingValueVersion,
            MaterialBindingResourceVersion = plan.MaterialBindingResourceVersion,
            MaterialShaderStateRevision = plan.MaterialShaderStateRevision,
            MaterialUberStateRevision = plan.MaterialUberStateRevision,
            TemporalMesh = plan.Mesh,
            TemporalGeometryRevision = plan.MeshGeometryRevision,
            TemporalVertexCount = plan.MeshVertexCount,
            TemporalIndexCount = plan.MeshIndexCount,
            TemporalPrimitiveTopology = plan.MeshPrimitiveTopology,
            TemporalSkinned = plan.MeshIsSkinned,
            Active = true,
        };
        if (!TryInsertRegistrationLookup(index))
        {
            Database.Scene.Draws.TryRemoveImmediatelyBeforePublication(draw);
            RollBackPartialRegistration(
                currentTransform,
                previousTransform,
                instance,
                geometry,
                deformation,
                renderState,
                editorIdentity);
            _registrations[index] = default;
            if (index == _registrationCount - 1)
                --_registrationCount;
            InvalidateRegistrationIdentityCache();
            failureReason = $"Registration lookup rejected index {index}; registrations={_registrationCount}/{_registrations.Length}, slots={_registrationLookupIndices.Length}, generation={_registrationLookupGeneration}";
            return -1;
        }
        MarkRegistrationMembershipChanged();
        ++_topologyDeltaCount;
        AdvanceNonZero(ref _topologyGeneration);
        AdvanceNonZero(ref _contentGeneration);
        return index;
    }

    private void UpdateRegistration(
        int commandIndex,
        int registrationIndex,
        in AdvancedGpuSceneCommandTransition plan,
        AdvancedGpuHandle material)
    {
        ref AdvancedResidentRegistration registration =
            ref _registrations[registrationIndex];
        if (!registration.Active)
            return;

        ref readonly DrawMetadata command = ref plan.Command;
        int primitiveIndex = plan.PrimitiveIndex;
        Matrix4x4 world = plan.World;
        Matrix4x4 previousWorld = plan.PreviousWorld;
        BoundsGpu bounds = plan.Bounds;
        ulong structural = plan.StructuralSignature;
        ulong content = plan.ContentSignature;
        if (structural != registration.StructuralSignature ||
            material != registration.Material)
        {
            if (!material.IsValid ||
                !Database.Materials.Materials.IsCurrent(material) ||
                !CanUpdateRegistrationStructure(registration) ||
                !Database.Scene.Draws.TryGet(registration.Draw, out AdvancedDrawRecord draw))
            {
                throw new InvalidOperationException("Canonical structural update preflight failed.");
            }

            if (!TryRegisterCanonicalGeometry(
                    commandIndex,
                    in bounds,
                    in command,
                    out AdvancedGpuHandle replacementGeometry) ||
                !Database.Scene.Geometry.Records.TryTombstone(
                    registration.Geometry,
                    _sequence) ||
                !Database.Scene.Deformations.TryReplace(
                    registration.Deformation,
                    CreateDeformation(replacementGeometry, plan.MeshVertexCount),
                    EAdvancedGpuMutationDomain.ResourceBinding) ||
                !Database.Scene.RenderStates.TryReplace(
                registration.RenderState,
                plan.RenderState,
                EAdvancedGpuMutationDomain.LayoutTopology))
            {
                throw new InvalidOperationException("Canonical structural update failed after successful preflight.");
            }

            draw.Geometry = replacementGeometry;
            draw.PrimitiveSection = checked((uint)Math.Max(0, primitiveIndex));
            draw.Flags = command.Flags & ~(uint)GPUIndirectRenderFlags.EditorHighlightMask;
            draw.Material = material;
            draw.SourceFlatDirectionOct = plan.SourceFlatDirectionOct;
            draw.SourceFlatDirectionValid = plan.SourceFlatDirectionValid;
            if (!Database.Scene.Draws.TryReplace(
                    registration.Draw,
                    draw,
                    EAdvancedGpuMutationDomain.RecordingTopology))
                throw new InvalidOperationException("Canonical draw update failed after successful preflight.");
            registration.StructuralSignature = structural;
            registration.Material = material;
            registration.Geometry = replacementGeometry;
            ++_topologyDeltaCount;
            AdvanceNonZero(ref _topologyGeneration);
        }

        registration.TemporalMesh = plan.Mesh;
        registration.TemporalGeometryRevision = plan.MeshGeometryRevision;
        registration.TemporalVertexCount = plan.MeshVertexCount;
        registration.TemporalIndexCount = plan.MeshIndexCount;
        registration.TemporalPrimitiveTopology = plan.MeshPrimitiveTopology;
        registration.TemporalSkinned = plan.MeshIsSkinned;
        registration.MaterialSource = plan.MaterialSource;
        registration.MaterialBindingLayoutVersion = plan.MaterialBindingLayoutVersion;
        registration.MaterialBindingValueVersion = plan.MaterialBindingValueVersion;
        registration.MaterialBindingResourceVersion = plan.MaterialBindingResourceVersion;
        registration.MaterialShaderStateRevision = plan.MaterialShaderStateRevision;
        registration.MaterialUberStateRevision = plan.MaterialUberStateRevision;
        if (content == registration.ContentSignature)
            return;

        if (!Database.Scene.Draws.TryGet(registration.Draw, out AdvancedDrawRecord contentDraw))
            throw new InvalidOperationException("Canonical content draw is unavailable after successful preflight.");
        uint contentDrawFlags = command.Flags & ~(uint)GPUIndirectRenderFlags.EditorHighlightMask;
        bool drawFlagsChanged = contentDraw.Flags != contentDrawFlags;
        bool flatDirectionChanged =
            contentDraw.SourceFlatDirectionOct != plan.SourceFlatDirectionOct ||
            contentDraw.SourceFlatDirectionValid != plan.SourceFlatDirectionValid;
        if (!CanUpdateRegistrationContent(registration) ||
            ((drawFlagsChanged || flatDirectionChanged) && !Database.Scene.Draws.CanApply(0, 1, 0)) ||
            !Database.Scene.Transforms.TryReplace(
            registration.PreviousTransform,
            CreateTransform(previousWorld)) ||
            !Database.Scene.Transforms.TryReplace(
            registration.CurrentTransform,
            CreateTransform(world, plan.Source as RenderCommand)) ||
            !Database.Scene.Instances.TryReplace(
            registration.Instance,
            CreateInstance(world, previousWorld, in bounds, in command)) ||
            !Database.Scene.EditorIdentities.TryReplace(
            registration.EditorIdentity,
            CreateEditorIdentity(plan.Source, in command)))
        {
            throw new InvalidOperationException("Canonical content update failed after successful preflight.");
        }
        if (drawFlagsChanged || flatDirectionChanged)
        {
            contentDraw.Flags = contentDrawFlags;
            contentDraw.SourceFlatDirectionOct = plan.SourceFlatDirectionOct;
            contentDraw.SourceFlatDirectionValid = plan.SourceFlatDirectionValid;
            if (!Database.Scene.Draws.TryReplace(
                    registration.Draw, contentDraw,
                    drawFlagsChanged
                        ? EAdvancedGpuMutationDomain.RecordingTopology
                        : EAdvancedGpuMutationDomain.Content))
                throw new InvalidOperationException("Canonical draw flags update failed after successful preflight.");
            if (drawFlagsChanged)
            {
                ++_topologyDeltaCount;
                AdvanceNonZero(ref _topologyGeneration);
            }
        }
        registration.World = world;
        registration.ContentSignature = content;
        ++_contentDeltaCount;
        AdvanceNonZero(ref _contentGeneration);
    }

    private void TombstoneMissingRegistrations()
    {
        for (int index = 0; index < _registrationCount; ++index)
        {
            ref AdvancedResidentRegistration registration = ref _registrations[index];
            if (!registration.Active || registration.LastSeenSequence == _sequence)
                continue;

            if (!CanTombstoneRegistration(registration))
                throw new InvalidOperationException("Canonical tombstone transaction exceeded its bounded journal capacity.");

            bool tombstoned =
                Database.Scene.Draws.TryTombstone(registration.Draw, _sequence) &&
                Database.Scene.Instances.TryTombstone(registration.Instance, _sequence) &&
                Database.Scene.Geometry.Records.TryTombstone(registration.Geometry, _sequence) &&
                Database.Scene.Deformations.TryTombstone(registration.Deformation, _sequence) &&
                Database.Scene.RenderStates.TryTombstone(registration.RenderState, _sequence) &&
                Database.Scene.EditorIdentities.TryTombstone(registration.EditorIdentity, _sequence) &&
                Database.Scene.Transforms.TryTombstone(registration.CurrentTransform, _sequence) &&
                Database.Scene.Transforms.TryTombstone(registration.PreviousTransform, _sequence);
            if (!tombstoned)
                throw new InvalidOperationException("Canonical tombstone transaction failed after successful preflight.");

            registration.Active = false;
            registration.TombstoneSequence = _sequence;
            MarkRegistrationMembershipChanged();
            ++_topologyDeltaCount;
            AdvanceNonZero(ref _topologyGeneration);
            AdvanceNonZero(ref _contentGeneration);
        }
    }

    private void AppendLegacyMapping(
        uint commandIndex,
        int primitiveIndex,
        in DrawMetadata command,
        in AdvancedResidentRegistration registration)
    {
        _legacyMappings[_legacyMappingCount++] = new LegacyCanonicalDrawMapping(
            commandIndex,
            command.MeshID,
            command.MaterialID,
            command.RenderPass,
            primitiveIndex,
            registration.Draw,
            registration.Geometry,
            registration.Material,
            Mix(
                registration.StructuralSignature,
                registration.ContentSignature));
    }

    /// <summary>
    /// Grows the boundary-sized storage. <paramref name="grew"/> is true when
    /// the required boundary changed or any owner grew; the retained plan
    /// lookups can then be stale, so the caller must run the whole-scene plan.
    /// </summary>
    private bool EnsureBoundaryCapacity(uint commandCount, uint globalResourceCount, out bool grew)
    {
        uint required = ComputeBoundaryRequirement(commandCount, globalResourceCount);
        // Every boundary-sized scratch array grows only from this value, so an
        // unchanged value leaves the retained plan lookups intact.
        grew = required != _lastBoundaryRequired;
        _lastBoundaryRequired = required;
        _lastBoundaryGlobalResourceCount = globalResourceCount;
        if (required > (uint)_commandDrawHandles.Length)
        {
            Array.Resize(ref _commandDrawHandles, checked((int)required));
            Array.Resize(ref _legacyMappings, checked((int)required));
            Array.Resize(ref _orderedRegistrationIdentities, checked((int)required));
            grew = true;
        }
        if (required > (uint)_registrations.Length)
        {
            Array.Resize(ref _registrations, checked((int)required));
            Array.Resize(ref _preflightSeenStamps, checked((int)required));
            grew = true;
        }

        uint lookupCapacity = NextPowerOfTwo(checked(required * 2u));
        if (lookupCapacity > (uint)_registrationLookupIndices.Length)
        {
            Array.Resize(ref _registrationLookupIndices, checked((int)lookupCapacity));
            Array.Resize(ref _registrationLookupStamps, checked((int)lookupCapacity));
            Array.Clear(_registrationLookupStamps);
            _registrationLookupGeneration = 0u;
            InvalidateRegistrationIdentityCache();
            grew = true;
        }

        AdvancedSharedGpuSceneCapacityProfile profile = CreateCapacityProfile(required);
        // Independent scene or material growth can leave resource tables below
        // this boundary profile. Grow all required database owners before their registries.
        if (RequiresBoundaryGrowth(Database.Capacities, profile))
        {
            grew = true;
            if (!Database.TryGrowAtFrameBoundary(profile))
                return false;
        }

        _resourcePublisher.GrowRegistryAtFrameBoundary(
            profile.TextureRecords,
            profile.SamplerRecords);
        _materialPublisher.GrowAtFrameBoundary(profile.MaterialRecords);
        EnsureMaterialTransitionCapacity(required);
        return true;
    }

    private static bool RequiresBoundaryGrowth(
        in AdvancedSharedGpuSceneCapacityProfile current,
        in AdvancedSharedGpuSceneCapacityProfile requested)
    {
        AdvancedGpuSceneCapacityProfile scene = current.Scene;
        AdvancedGpuSceneCapacityProfile target = requested.Scene;
        return target.DrawRecords > scene.DrawRecords ||
            target.InstanceRecords > scene.InstanceRecords ||
            target.TransformRecords > scene.TransformRecords ||
            target.DeformationRecords > scene.DeformationRecords ||
            target.RenderStateRecords > scene.RenderStateRecords ||
            target.EditorIdentityRecords > scene.EditorIdentityRecords ||
            target.GeometryRecords > scene.GeometryRecords ||
            target.StaticVertexBytes > scene.StaticVertexBytes ||
            target.IndexBytes > scene.IndexBytes ||
            target.PreSkinnedCurrentBytes > scene.PreSkinnedCurrentBytes ||
            target.PreSkinnedPreviousBytes > scene.PreSkinnedPreviousBytes ||
            target.MeshletBytes > scene.MeshletBytes ||
            target.MeshletDescriptorBytes > scene.MeshletDescriptorBytes ||
            target.MeshletVertexIndexBytes > scene.MeshletVertexIndexBytes ||
            target.MeshletTriangleWordBytes > scene.MeshletTriangleWordBytes ||
            requested.MaterialRecords > current.MaterialRecords ||
            requested.ShadingKernels > current.ShadingKernels ||
            requested.MaterialLayouts > current.MaterialLayouts ||
            requested.MaterialLayoutMembers > current.MaterialLayoutMembers ||
            requested.MaterialConstantWords > current.MaterialConstantWords ||
            requested.MaterialTextureBindings > current.MaterialTextureBindings ||
            requested.TextureRecords > current.TextureRecords ||
            requested.SamplerRecords > current.SamplerRecords ||
            requested.LightRecords > current.LightRecords ||
            requested.ShadowRecords > current.ShadowRecords ||
            requested.ProbeRecords > current.ProbeRecords ||
            requested.EnvironmentRecords > current.EnvironmentRecords ||
            requested.DecalRecords > current.DecalRecords ||
            requested.GiResourceRecords > current.GiResourceRecords;
    }

    private int FindRegistration(IRenderCommandMesh source, int primitiveIndex)
    {
        uint mask = checked((uint)_registrationLookupIndices.Length - 1u);
        uint start = HashRegistration(source, primitiveIndex) & mask;
        for (uint probe = 0u; probe < (uint)_registrationLookupIndices.Length; ++probe)
        {
            int slot = checked((int)((start + probe) & mask));
            if (_registrationLookupStamps[slot] != _registrationLookupGeneration)
                return -1;

            int index = _registrationLookupIndices[slot];
            ref readonly AdvancedResidentRegistration registration = ref _registrations[index];
            if (registration.Active &&
                ReferenceEquals(registration.Source, source) &&
                registration.PrimitiveIndex == primitiveIndex)
                return index;
        }
        return -1;
    }

    private void RebuildRegistrationLookup()
    {
        _registrationLookupNeedsRebuild = true;
        ++_registrationLookupGeneration;
        if (_registrationLookupGeneration == 0u)
        {
            Array.Clear(_registrationLookupStamps);
            _registrationLookupGeneration = 1u;
        }

        for (int index = 0; index < _registrationCount; ++index)
            if (_registrations[index].Active && !TryInsertRegistrationLookup(index))
                throw new InvalidOperationException("The fixed resident-registration lookup table is undersized.");
        _registrationLookupNeedsRebuild = false;
        ++_lastRegistrationLookupRebuildCount;
    }

    private bool TryInsertRegistrationLookup(int registrationIndex)
    {
        ref readonly AdvancedResidentRegistration registration =
            ref _registrations[registrationIndex];
        if (!registration.Active || registration.Source is null)
            return false;

        uint mask = checked((uint)_registrationLookupIndices.Length - 1u);
        uint start = HashRegistration(registration.Source, registration.PrimitiveIndex) & mask;
        for (uint probe = 0u; probe < (uint)_registrationLookupIndices.Length; ++probe)
        {
            int slot = checked((int)((start + probe) & mask));
            if (_registrationLookupStamps[slot] == _registrationLookupGeneration)
                continue;

            _registrationLookupIndices[slot] = registrationIndex;
            _registrationLookupStamps[slot] = _registrationLookupGeneration;
            return true;
        }

        return false;
    }

    private static uint HashRegistration(IRenderCommandMesh source, int primitiveIndex)
    {
        uint hash = unchecked((uint)RuntimeHelpers.GetHashCode(source));
        hash ^= unchecked((uint)primitiveIndex) * 0x9E3779B9u;
        hash ^= hash >> 16;
        hash *= 0x7FEB352Du;
        hash ^= hash >> 15;
        return hash;
    }

    private int FindReusableRegistrationIndex()
    {
        for (int index = 0; index < _registrationCount; ++index)
            if (!_registrations[index].Active)
                return index;
        return -1;
    }

    private static void CaptureSourceState(
        GPUScene scene,
        IRenderCommandMesh source,
        int primitiveIndex,
        in DrawMetadata command,
        out Matrix4x4 world,
        out Matrix4x4 previousWorld,
        out Matrix4x4 sourceFlatBasis,
        out BoundsGpu bounds,
        out XRMeshRenderer? capturedRenderer,
        out XRMesh? mesh,
        out XRMaterial? material,
        out RenderingParameters? renderOptions,
        out int sourcePrimitiveCount)
    {
        AdvancedMeshRenderSnapshot snapshot = CaptureSourceSnapshot(source);
        world = snapshot.WorldMatrixIsModelMatrix
            ? snapshot.CurrentWorld
            : Matrix4x4.Identity;
        previousWorld = snapshot.WorldMatrixIsModelMatrix
            ? snapshot.PreviousWorld
            : Matrix4x4.Identity;
        sourceFlatBasis = snapshot.SourceFlatBasis;
        bounds = ReadCullBounds(scene, in command);
        capturedRenderer = snapshot.Renderer;
        sourcePrimitiveCount = Math.Max(1, snapshot.Renderer?.Submeshes.Count ?? 0);
        ResolveSourcePrimitive(in snapshot, primitiveIndex, out mesh, out material, out renderOptions);
    }

    /// <summary>Captures the render-buffer state of one source command.</summary>
    private static AdvancedMeshRenderSnapshot CaptureSourceSnapshot(IRenderCommandMesh source)
        => source is RenderCommandMesh3D mesh3D
            ? mesh3D.CaptureAdvancedPreparationSnapshot()
            : new AdvancedMeshRenderSnapshot(
                source.Mesh,
                source.WorldMatrix,
                source.WorldMatrix,
                source.Instances,
                source.WorldMatrixIsModelMatrix,
                EAdvancedVelocityValidityReason.HistoryReset,
                source.ForceCpuRendering,
                source.MaterialOverride,
                source.RenderOptionsOverride,
                source.WorldMatrixIsModelMatrix ? source.WorldMatrix : Matrix4x4.Identity);

    /// <summary>Resolves the mesh, effective material, and raster options of one source primitive.</summary>
    private static void ResolveSourcePrimitive(
        in AdvancedMeshRenderSnapshot snapshot,
        int primitiveIndex,
        out XRMesh? mesh,
        out XRMaterial? material,
        out RenderingParameters? renderOptions)
    {
        if (snapshot.Renderer is not { } renderer ||
            !renderer.TryGetMesh(primitiveIndex, out mesh, out material))
        {
            mesh = null;
            material = null;
        }
        material = snapshot.MaterialOverride ?? material;
        renderOptions = snapshot.RenderOptionsOverride ?? material?.RenderOptions;
    }

    /// <summary>Reads the published CPU cull bounds row of one command.</summary>
    private static BoundsGpu ReadCullBounds(GPUScene scene, in DrawMetadata command)
        => command.BoundsID < scene.CullBoundsBuffer.ElementCount
            ? scene.CullBoundsBuffer.GetDataRawAtIndex<BoundsGpu>(command.BoundsID)
            : default;

    private bool CanAddRegistration(AdvancedGpuSceneDatabase tables)
        => tables.Transforms.CanApply(2, 0, 0) &&
           tables.Instances.CanApply(1, 0, 0) &&
           tables.Geometry.Records.CanApply(1, 0, 0) &&
           tables.Deformations.CanApply(1, 0, 0) &&
           tables.RenderStates.CanApply(1, 0, 0) &&
           tables.EditorIdentities.CanApply(1, 0, 0) &&
           tables.Draws.CanApply(1, 0, 0);

    private static string DescribeRegistrationPreflightFailure(AdvancedGpuSceneDatabase tables)
    {
        if (!tables.Transforms.CanApply(2, 0, 0))
            return DescribeRegistrationTableFailure("transform preflight", tables.Transforms, 2);
        if (!tables.Instances.CanApply(1, 0, 0))
            return DescribeRegistrationTableFailure("instance preflight", tables.Instances, 1);
        if (!tables.Geometry.Records.CanApply(1, 0, 0))
            return DescribeRegistrationTableFailure("geometry preflight", tables.Geometry.Records, 1);
        if (!tables.Deformations.CanApply(1, 0, 0))
            return DescribeRegistrationTableFailure("deformation preflight", tables.Deformations, 1);
        if (!tables.RenderStates.CanApply(1, 0, 0))
            return DescribeRegistrationTableFailure("render state preflight", tables.RenderStates, 1);
        if (!tables.EditorIdentities.CanApply(1, 0, 0))
            return DescribeRegistrationTableFailure("editor identity preflight", tables.EditorIdentities, 1);
        if (!tables.Draws.CanApply(1, 0, 0))
            return DescribeRegistrationTableFailure("draw preflight", tables.Draws, 1);
        return "Registration preflight changed after its first capacity check";
    }

    private static string DescribeRegistrationTableFailure<T>(
        string stage,
        AdvancedGpuRecordTable<T> table,
        int requestedAdds)
        where T : unmanaged
        => $"{stage} rejected {requestedAdds} add(s); live={table.Count}, retired={table.RetiredCount}, " +
           $"capacity={table.Capacity}, availableAdds={table.AvailableAdditions}, " +
           $"availableDeltas={table.AvailablePublicationDeltas}, availableRemaps={table.AvailableRemaps}";

    private bool CanUpdateRegistrationStructure(
        in AdvancedResidentRegistration registration)
        => Database.Scene.Geometry.Records.IsCurrent(registration.Geometry) &&
           // Geometry payloads are immutable. A structural replacement adds
           // a new record and tombstones the already-published old record;
           // immediate removal is reserved strictly for unpublished rollback.
           Database.Scene.Geometry.Records.CanApply(1, 0, 1) &&
           Database.Scene.Deformations.IsCurrent(registration.Deformation) &&
           Database.Scene.Deformations.CanApply(0, 1, 0) &&
           Database.Scene.RenderStates.IsCurrent(registration.RenderState) &&
           Database.Scene.RenderStates.CanApply(0, 1, 0) &&
           Database.Scene.Draws.IsCurrent(registration.Draw) &&
           Database.Scene.Draws.CanApply(0, 1, 0);

    private bool CanUpdateRegistrationContent(
        in AdvancedResidentRegistration registration)
        => Database.Scene.Transforms.IsCurrent(registration.CurrentTransform) &&
           Database.Scene.Transforms.IsCurrent(registration.PreviousTransform) &&
           Database.Scene.Transforms.CanApply(0, 2, 0) &&
           Database.Scene.Instances.IsCurrent(registration.Instance) &&
           Database.Scene.Instances.CanApply(0, 1, 0) &&
           Database.Scene.EditorIdentities.IsCurrent(registration.EditorIdentity) &&
           Database.Scene.EditorIdentities.CanApply(0, 1, 0);

    private bool CanTombstoneRegistration(
        in AdvancedResidentRegistration registration)
        => Database.Scene.Draws.IsCurrent(registration.Draw) &&
           Database.Scene.Draws.CanApply(0, 0, 1) &&
           Database.Scene.Instances.IsCurrent(registration.Instance) &&
           Database.Scene.Instances.CanApply(0, 0, 1) &&
           Database.Scene.Geometry.Records.IsCurrent(registration.Geometry) &&
           Database.Scene.Geometry.Records.CanApply(0, 0, 1) &&
           Database.Scene.Deformations.IsCurrent(registration.Deformation) &&
           Database.Scene.Deformations.CanApply(0, 0, 1) &&
           Database.Scene.RenderStates.IsCurrent(registration.RenderState) &&
           Database.Scene.RenderStates.CanApply(0, 0, 1) &&
           Database.Scene.EditorIdentities.IsCurrent(registration.EditorIdentity) &&
           Database.Scene.EditorIdentities.CanApply(0, 0, 1) &&
           Database.Scene.Transforms.IsCurrent(registration.CurrentTransform) &&
           Database.Scene.Transforms.IsCurrent(registration.PreviousTransform) &&
           Database.Scene.Transforms.CanApply(0, 0, 2);

    private void RollBackPartialRegistration(
        AdvancedGpuHandle currentTransform,
        AdvancedGpuHandle previousTransform,
        AdvancedGpuHandle instance,
        AdvancedGpuHandle geometry,
        AdvancedGpuHandle deformation,
        AdvancedGpuHandle renderState,
        AdvancedGpuHandle editorIdentity)
    {
        if (currentTransform.IsValid)
            Database.Scene.Transforms.TryRemoveImmediatelyBeforePublication(currentTransform);
        if (previousTransform.IsValid)
            Database.Scene.Transforms.TryRemoveImmediatelyBeforePublication(previousTransform);
        if (instance.IsValid)
            Database.Scene.Instances.TryRemoveImmediatelyBeforePublication(instance);
        if (geometry.IsValid)
            Database.Scene.Geometry.Records.TryRemoveImmediatelyBeforePublication(geometry);
        if (deformation.IsValid)
            Database.Scene.Deformations.TryRemoveImmediatelyBeforePublication(deformation);
        if (renderState.IsValid)
            Database.Scene.RenderStates.TryRemoveImmediatelyBeforePublication(renderState);
        if (editorIdentity.IsValid)
            Database.Scene.EditorIdentities.TryRemoveImmediatelyBeforePublication(editorIdentity);
    }

    private static AdvancedTransformRecord CreateTransform(in Matrix4x4 world, RenderCommand? source = null)
        => TransformPublicationRecords.TryGet(source, world, out _, out AdvancedTransformRecord record)
            ? record : new() { World = world };

    private static AdvancedInstanceRecord CreateInstance(
        in Matrix4x4 world,
        in Matrix4x4 previousWorld,
        in BoundsGpu bounds,
        in DrawMetadata command)
        => new()
        {
            CurrentWorld = world,
            PreviousWorld = previousWorld,
            BoundsSphere = bounds.BoundingSphere,
            BoundsMin = bounds.AabbMin,
            BoundsMax = bounds.AabbMax,
            VisibilityFlags =
                ((command.Flags & (uint)GPUIndirectRenderFlags.PrimaryDisabled) == 0u
                    ? EAdvancedInstanceVisibilityFlags.Enabled : EAdvancedInstanceVisibilityFlags.None) |
                ((command.Flags & (uint)GPUIndirectRenderFlags.CastShadow) != 0u
                    ? EAdvancedInstanceVisibilityFlags.CastsShadows : EAdvancedInstanceVisibilityFlags.None) |
                ((command.Flags & (uint)GPUIndirectRenderFlags.ReceiveShadows) != 0u
                    ? EAdvancedInstanceVisibilityFlags.ReceivesShadows : EAdvancedInstanceVisibilityFlags.None),
            LayerMask = command.LayerMask,
            RenderPassMask = command.RenderPassMask,
            Reserved0 = RuntimeEngineMaterialConstructionServices.Target == EngineMaterialConstructionTarget.WebGpuCooked
                ? command.RenderPass : 0u,
        };

    private static AdvancedDeformationRecord CreateDeformation(
        AdvancedGpuHandle geometry,
        int vertexCount)
        => new()
        {
            SourceGeometry = geometry,
            CurrentGeometry = geometry,
            PreviousGeometry = geometry,
            Animation = AdvancedGpuHandle.Invalid,
            VertexCount = checked((uint)Math.Max(0, vertexCount)),
        };

    private static EAdvancedVelocityValidityReason ResolveTemporalEvent(
        in AdvancedResidentRegistration registration,
        in AdvancedGpuSceneCommandTransition plan,
        ulong frameId)
    {
        if (!registration.Active)
            return EAdvancedVelocityValidityReason.NewlyVisible;
        if (registration.LastSeenFrameId == 0UL ||
            unchecked(registration.LastSeenFrameId + 1UL) != frameId)
            return EAdvancedVelocityValidityReason.FrameGap;
        if (registration.TemporalVertexCount != plan.MeshVertexCount)
            return EAdvancedVelocityValidityReason.VertexCountChanged;
        if (!ReferenceEquals(registration.TemporalMesh, plan.Mesh) ||
            registration.TemporalGeometryRevision != plan.MeshGeometryRevision ||
            registration.TemporalIndexCount != plan.MeshIndexCount ||
            registration.TemporalPrimitiveTopology != plan.MeshPrimitiveTopology ||
            registration.TemporalSkinned != plan.MeshIsSkinned)
        {
            return EAdvancedVelocityValidityReason.TopologyChanged;
        }
        return EAdvancedVelocityValidityReason.Valid;
    }

    private static AdvancedGeometryRecord CreateGeometry(
        GPUScene scene,
        XRMesh? mesh,
        in BoundsGpu bounds,
        in DrawMetadata command)
    {
        return new AdvancedGeometryRecord
        {
            // Geometry publication is deliberately independent of the legacy atlas.
            // A resident record is created only through AdvancedGeometryDatabase,
            // which supplies immutable stream references and generation closure.
            VertexCount = checked((uint)Math.Max(0, mesh?.VertexCount ?? 0)),
            IndexCount = checked((uint)Math.Max(0, mesh?.IndexCount ?? 0)),
            BoundsSphere = bounds.BoundingSphere,
            BoundsMin = bounds.AabbMin,
            BoundsMax = bounds.AabbMax,
            MaterialSectionFirst = command.SubmeshID,
            MaterialSectionCount = 1u,
            PrimitiveTopology = mesh?.Type ?? EPrimitiveType.Triangles,
            Source = EAdvancedGeometrySource.Static,
            Residency = EAdvancedGeometryResidency.Pending,
            MissingBehavior = EAdvancedMissingGeometryBehavior.SkipDraw,
            CookedLayoutVersion = AdvancedGeometryCookedLayout.CurrentVersion,
            // Shadow participation belongs to the draw, not immutable geometry.
            Flags = StructuralDrawFlags(command.Flags),
        };
    }

    private static AdvancedRenderStateRecord CreateRenderState(
        XRMesh? mesh,
        RenderingParameters? renderOptions,
        in DrawMetadata command)
        => new()
        {
            StateClass = command.StateClassID,
            PrimitiveTopology = checked((uint)(mesh?.Type ?? EPrimitiveType.Triangles)),
            CoverageMode = (command.Flags & (uint)GPUIndirectRenderFlags.Transparent) != 0u ? 1u : 0u,
            CullMode = checked((uint)(renderOptions?.CullMode ?? ECullMode.Back)),
            // Existing GPU draw flags occupy the low 24 bits. Native admission
            // retains the actual command override's unsupported raster features.
            // StructuralDrawFlags removes PrimaryDisabled (bit 24), so that draw
            // flag cannot alias a raster-state flag in the high byte.
            Flags = StructuralDrawFlags(command.Flags) | (uint)WebGpuAdvancedRasterStateContract.Capture(renderOptions),
        };

    private static AdvancedEditorIdentityRecord CreateEditorIdentity(
        IRenderCommandMesh? source,
        in DrawMetadata command)
    {
        ulong stableComponentIdentity = source is RenderCommand renderCommand &&
            renderCommand.OwnerRenderInfo is { } renderInfo
                ? renderInfo.StableInstanceId
                : command.RenderIdentityID;
        return new AdvancedEditorIdentityRecord
        {
            StableInstanceId = stableComponentIdentity,
            IdentityLow = stableComponentIdentity,
            IdentityHigh = command.LogicalMeshID,
            SelectionId = command.RenderIdentityID,
            Flags =
                ((command.Flags & (uint)GPUIndirectRenderFlags.EditorHovered) != 0u ? 1u : 0u) |
                ((command.Flags & (uint)GPUIndirectRenderFlags.EditorSelected) != 0u ? 2u : 0u),
        };
    }

    private static uint StructuralDrawFlags(uint flags)
        => flags & ~((uint)GPUIndirectRenderFlags.EditorHighlightMask |
            (uint)GPUIndirectRenderFlags.PrimaryDisabled |
            (uint)GPUIndirectRenderFlags.CastShadow |
            (uint)GPUIndirectRenderFlags.ReceiveShadows);

    private static ulong ComputeStructuralSignature(
        in DrawMetadata command,
        XRMesh? mesh,
        XRMaterial? material,
        int primitiveIndex)
    {
        ulong hash = 14695981039346656037ul;
        hash = Mix(hash, command.MeshID);
        hash = Mix(hash, command.MaterialID);
        hash = Mix(hash, command.SubmeshID);
        hash = Mix(hash, command.RenderPass);
        hash = Mix(hash, command.RenderPassMask);
        hash = Mix(hash, command.StateClassID);
        // Hover/selection updates the editor identity in place; it must not
        // retire geometry or change native draw handles on a pointer move.
        hash = Mix(hash, StructuralDrawFlags(command.Flags));
        hash = Mix(hash, checked((uint)Math.Max(0, primitiveIndex)));
        hash = Mix(hash, checked((uint)Math.Max(0, mesh?.VertexCount ?? 0)));
        hash = Mix(hash, checked((uint)Math.Max(0, mesh?.IndexCount ?? 0)));
        hash = Mix(hash, unchecked((ulong)(mesh?.GeometryRevision ?? 0L)));
        hash = Mix(hash, material?.BindingLayoutVersion ?? 0u);
        hash = Mix(hash, unchecked((ulong)(material?.ShaderStateRevision ?? 0L)));
        hash = Mix(hash, unchecked((ulong)(material?.UberStateRevision ?? 0L)));
        return hash;
    }

    private bool TryCaptureGpuBoundsSource(XRMeshRenderer? renderer,
        out PhysicsChainGpuBoundsSource? boundsSource)
    {
        boundsSource = null;
        if (renderer is null || !renderer.HasCompleteGpuDrivenBoneCoverage)
            return true;
        GPUPhysicsChainDispatcher dispatcher = GPUPhysicsChainDispatcher.Instance;
        if (!_planBoundsEpochCaptured)
        {
            // Read the epoch before the first capture. A later change to the
            // committed bounds sources advances it, so the unchanged-row gate fails.
            _planBoundsEpoch = dispatcher.CommittedSpatialEpoch;
            _planBoundsEpochCaptured = true;
        }
        if (!dispatcher.TryCaptureGpuBoundsSource(renderer, out PhysicsChainGpuBoundsSource source))
            return false;
        boundsSource = source;
        return true;
    }

    private static ulong ComputeContentSignature(
        in DrawMetadata command,
        in BoundsGpu bounds,
        in Matrix4x4 world,
        in Matrix4x4 previousWorld,
        XRMaterial? material,
        bool gpuOwnedBounds,
        uint sourceFlatDirectionOct,
        uint sourceFlatDirectionValid)
    {
        ulong hash = Mix(command.TransformID, command.InstanceCount);
        hash = Mix(hash, command.Flags & ((uint)GPUIndirectRenderFlags.EditorHighlightMask |
            (uint)GPUIndirectRenderFlags.PrimaryDisabled |
            (uint)GPUIndirectRenderFlags.CastShadow |
            (uint)GPUIndirectRenderFlags.ReceiveShadows));
        hash = Mix(hash, command.LayerMask);
        hash = Mix(hash, gpuOwnedBounds ? 1u : 0u);
        hash = Mix(hash, sourceFlatDirectionOct);
        hash = Mix(hash, sourceFlatDirectionValid);
        if (!gpuOwnedBounds)
            hash = Mix(hash, bounds.BoundsVersion);
        hash = Mix(hash, material?.BindingValueVersion ?? 0u);
        hash = Mix(hash, material?.BindingResourceVersion ?? 0u);
        // A covered draw with identity transforms gets its motion from the
        // current physics output page. Nonidentity transforms remain content.
        if (!gpuOwnedBounds || world != Matrix4x4.Identity ||
            previousWorld != Matrix4x4.Identity)
        {
            hash = MixMatrix(hash, in world);
            hash = MixMatrix(hash, in previousWorld);
        }
        return hash;
    }

    private static bool TryEncodeSourceFlatDirection(XRMaterial? material,
        in Matrix4x4 sourceBasis, out uint packed, out uint valid)
    {
        bool captured = SourceMultilayerFlatDirection.TryCapture(material,
            sourceBasis, out Vector4 direction);
        SourceMultilayerFlatDirection.Pack(direction, out packed, out valid);
        return captured;
    }

    private static ulong MixMatrix(ulong hash, in Matrix4x4 value)
    {
        hash = MixFloat(hash, value.M11); hash = MixFloat(hash, value.M12);
        hash = MixFloat(hash, value.M13); hash = MixFloat(hash, value.M14);
        hash = MixFloat(hash, value.M21); hash = MixFloat(hash, value.M22);
        hash = MixFloat(hash, value.M23); hash = MixFloat(hash, value.M24);
        hash = MixFloat(hash, value.M31); hash = MixFloat(hash, value.M32);
        hash = MixFloat(hash, value.M33); hash = MixFloat(hash, value.M34);
        hash = MixFloat(hash, value.M41); hash = MixFloat(hash, value.M42);
        hash = MixFloat(hash, value.M43); hash = MixFloat(hash, value.M44);
        return hash;
    }

    private static ulong MixFloat(ulong hash, float value)
        => Mix(hash, unchecked((uint)BitConverter.SingleToInt32Bits(value)));

    private static AdvancedSharedGpuSceneCapacityProfile CreateCapacityProfile(uint capacity)
    {
        uint materialLayoutCapacity = 6u;
        uint materialLayoutMemberCapacity = checked((uint)(
            MaterialBindingLayouts.OpaqueDeferred.PackedMembers.Count +
            MaterialBindingLayouts.OpaqueDeferred.Textures.Count +
            MaterialBindingLayouts.ForwardOpaque.PackedMembers.Count +
            MaterialBindingLayouts.ForwardOpaque.Textures.Count +
            MaterialBindingLayouts.MaskedForward.PackedMembers.Count +
            MaterialBindingLayouts.MaskedForward.Textures.Count +
            MaterialBindingLayouts.ProjectiveMirror.PackedMembers.Count +
            MaterialBindingLayouts.ProjectiveMirror.Textures.Count +
            NativeUberMaterialSchema.Opaque.PackedMembers.Count +
            NativeUberMaterialSchema.Opaque.Textures.Count +
            NativeUberMaterialSchema.Masked.PackedMembers.Count +
            NativeUberMaterialSchema.Masked.Textures.Count));
        uint maximumConstantWords = Math.Max(
            MaterialBindingLayouts.OpaqueDeferred.RowWordCount,
            Math.Max(
                MaterialBindingLayouts.ForwardOpaque.RowWordCount,
                Math.Max(
                    MaterialBindingLayouts.MaskedForward.RowWordCount,
                    Math.Max(MaterialBindingLayouts.ProjectiveMirror.RowWordCount,
                        NativeUberMaterialSchema.Opaque.RowWordCount))));
        uint maximumTextureBindings = checked((uint)Math.Max(
            MaterialBindingLayouts.OpaqueDeferred.Textures.Count,
            Math.Max(
                MaterialBindingLayouts.ForwardOpaque.Textures.Count,
                Math.Max(
                    MaterialBindingLayouts.MaskedForward.Textures.Count,
                    Math.Max(MaterialBindingLayouts.ProjectiveMirror.Textures.Count,
                        NativeUberMaterialSchema.TextureRoleCount)))));
        uint materialConstantWords = checked(capacity * maximumConstantWords);
        uint materialTextureBindings = checked(capacity * maximumTextureBindings);
        uint materialResourceReferences = checked(capacity *
            (maximumTextureBindings + AdvancedEngineSurfaceRecord.RoleCount + AdvancedUberBaseSurfaceRecord.RoleCount));

        return new(
            new AdvancedGpuSceneCapacityProfile(
                capacity,
                capacity,
                checked(capacity * 2u),
                capacity,
                capacity,
                capacity,
                capacity,
                checked(capacity * 65536u),
                checked(capacity * 12288u),
                0u,
                0u,
                checked(capacity * 4096u),
                checked(capacity * 1280u),
                checked(capacity * 16384u),
                checked(capacity * 16384u)),
            capacity,
            capacity,
            materialLayoutCapacity,
            materialLayoutMemberCapacity,
            materialConstantWords,
            materialTextureBindings,
            materialResourceReferences,
            materialResourceReferences,
            capacity,
            capacity,
            capacity,
            capacity,
            capacity,
            capacity);
    }

    private static uint NextPowerOfTwo(uint value)
    {
        if (value <= 1u)
            return 1u;
        return BitOperations.RoundUpToPowerOf2(value);
    }

    private static void AdvanceNonZero(ref ulong value)
    {
        unchecked { ++value; }
        if (value == 0ul)
            value = 1ul;
    }

    private static ulong Mix(ulong hash, ulong value)
    {
        hash ^= value;
        hash *= 1099511628211ul;
        return hash;
    }

    private struct AdvancedResidentRegistration
    {
        public IRenderCommandMesh? Source;
        public int PrimitiveIndex;
        public uint LegacyCommandIndex;
        public AdvancedGpuHandle Draw;
        public AdvancedGpuHandle Instance;
        public AdvancedGpuHandle Geometry;
        public AdvancedGpuHandle Deformation;
        public AdvancedGpuHandle Material;
        public AdvancedGpuHandle RenderState;
        public AdvancedGpuHandle EditorIdentity;
        public AdvancedGpuHandle CurrentTransform;
        public AdvancedGpuHandle PreviousTransform;
        public Matrix4x4 World;
        public ulong StructuralSignature;
        public ulong ContentSignature;
        public XRMaterial? MaterialSource;
        public ulong MaterialBindingLayoutVersion;
        public ulong MaterialBindingValueVersion;
        public ulong MaterialBindingResourceVersion;
        public long MaterialShaderStateRevision;
        public long MaterialUberStateRevision;
        public AdvancedCommittedMaterialPlan CommittedMaterialPlan;
        public ulong LastSeenSequence;
        public ulong LastSeenFrameId;
        public ulong TombstoneSequence;
        public XRMesh? TemporalMesh;
        public long TemporalGeometryRevision;
        public int TemporalVertexCount;
        public int TemporalIndexCount;
        public EPrimitiveType TemporalPrimitiveTopology;
        public bool TemporalSkinned;
        public bool Active;
    }
}
