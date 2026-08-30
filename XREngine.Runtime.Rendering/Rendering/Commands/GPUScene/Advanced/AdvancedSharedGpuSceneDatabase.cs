namespace XREngine.Rendering.Commands;

/// <summary>
/// Pipeline-neutral owner of the canonical scene/material records and their
/// shared logical-handle lookup image. Desktop and eye pipelines consume this
/// data independently while retaining separate outputs and temporal histories.
/// </summary>
public sealed class AdvancedSharedGpuSceneDatabase
{
    private static long s_nextDatabaseEpoch;
    private readonly object _publicationSync = new();
    private readonly AdvancedGpuScenePublication[] _publicationRing;
    private readonly AdvancedGpuScenePublicationSnapshot[] _publicationSnapshots;
    private readonly uint[] _packagePinCounts;
    private readonly uint[] _gpuPinCounts;
    private readonly ulong[] _consumerAcknowledgements;
    private readonly uint[] _consumerGenerations;
    private readonly byte[] _consumerActive;
    private readonly AdvancedGpuScenePublicationReference[] _leaseReferences;
    private readonly EAdvancedGpuScenePublicationPinKind[] _leaseKinds;
    private readonly uint[] _leaseGenerations;
    private readonly byte[] _leaseActive;
    private readonly ulong _databaseEpoch;
    private AdvancedSharedGpuSceneCapacityProfile _capacities;
    private int _publicationHead;
    private int _publicationCount;
    private ulong _nextPublicationSequence = 1u;
    private ulong _activePublicationSequence;
    private int _activePublicationRingIndex = -1;
    private AdvancedGpuScenePublication _preparedPublication;
    private ulong _lastSealedPublicationSequence;
    private ulong _lastReclaimedPublicationSequence;
    private ulong _publicationFaultSequence;
    private EAdvancedGpuScenePublicationFault _publicationFault;
    private bool _publicationPrepared;
    private bool _publicationFaulted;

    public AdvancedSharedGpuSceneDatabase(
        in AdvancedSharedGpuSceneCapacityProfile capacities,
        uint publicationCapacity = 8u,
        uint consumerCapacity = 8u)
    {
        if (publicationCapacity == 0u)
            throw new ArgumentOutOfRangeException(nameof(publicationCapacity));
        if (consumerCapacity == 0u || consumerCapacity >= int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(consumerCapacity));

        _capacities = capacities;
        Scene = new AdvancedGpuSceneDatabase(capacities.Scene);
        Materials = new AdvancedMaterialDatabase(
            capacities.MaterialRecords,
            capacities.ShadingKernels,
            capacities.MaterialLayouts,
            capacities.MaterialLayoutMembers,
            capacities.MaterialConstantWords,
            capacities.MaterialTextureBindings);
        Resources = new AdvancedGlobalResourceDatabase(
            capacities.TextureRecords,
            capacities.SamplerRecords,
            capacities.LightRecords,
            capacities.ShadowRecords,
            capacities.ProbeRecords,
            capacities.EnvironmentRecords,
            capacities.DecalRecords,
            capacities.GiResourceRecords);
        HandleLookups = new AdvancedGpuSceneLookupTable(Scene, Materials, Resources);
        _publicationRing = new AdvancedGpuScenePublication[checked((int)publicationCapacity)];
        _publicationSnapshots = new AdvancedGpuScenePublicationSnapshot[_publicationRing.Length];
        _packagePinCounts = new uint[_publicationRing.Length];
        _gpuPinCounts = new uint[_publicationRing.Length];
        _consumerAcknowledgements = new ulong[checked((int)consumerCapacity + 1)];
        _consumerGenerations = new uint[_consumerAcknowledgements.Length];
        _consumerActive = new byte[_consumerAcknowledgements.Length];
        int leaseCapacity = checked(Math.Max(16, (int)publicationCapacity * 8));
        _leaseReferences = new AdvancedGpuScenePublicationReference[leaseCapacity + 1];
        _leaseKinds = new EAdvancedGpuScenePublicationPinKind[leaseCapacity + 1];
        _leaseGenerations = new uint[leaseCapacity + 1];
        _leaseActive = new byte[leaseCapacity + 1];
        _databaseEpoch = CreateDatabaseEpoch();
        for (int index = 0; index < _publicationSnapshots.Length; ++index)
            _publicationSnapshots[index] = new AdvancedGpuScenePublicationSnapshot(this, _capacities);
    }

    /// <summary>
    /// Current structural capacities used by the live tables and retained
    /// publication snapshots. Producers use this as the base for boundary-only
    /// growth so an independently grown arena never shrinks another domain's
    /// snapshot budget.
    /// </summary>
    public AdvancedSharedGpuSceneCapacityProfile Capacities => _capacities;

    public AdvancedGpuSceneDatabase Scene { get; }

    public AdvancedMaterialDatabase Materials { get; }

    /// <summary>Canonical texture and sampler records shared by all frame consumers.</summary>
    public AdvancedGlobalResourceDatabase Resources { get; }

    public AdvancedGpuSceneLookupTable HandleLookups { get; }

    /// <summary>Current identity epoch. A reference from another epoch is never accepted.</summary>
    public ulong DatabaseEpoch => _databaseEpoch;

    /// <summary>Sequence currently receiving structural mutations, or zero outside a batch.</summary>
    public ulong ActivePublicationSequence
    {
        get
        {
            lock (_publicationSync)
                return _activePublicationSequence;
        }
    }

    public ulong LastSealedPublicationSequence
    {
        get
        {
            lock (_publicationSync)
                return _lastSealedPublicationSequence;
        }
    }

    public bool PublicationFaulted
    {
        get
        {
            lock (_publicationSync)
                return _publicationFaulted;
        }
    }

    public EAdvancedGpuScenePublicationFault PublicationFault
    {
        get
        {
            lock (_publicationSync)
                return _publicationFault;
        }
    }

    public ulong PublicationFaultSequence
    {
        get
        {
            lock (_publicationSync)
                return _publicationFaultSequence;
        }
    }

    public ulong MinimumAcknowledgedPublicationSequence
    {
        get
        {
            lock (_publicationSync)
                return GetMinimumAcknowledgedSequence();
        }
    }

    public ulong MinimumReclaimablePublicationSequence
    {
        get
        {
            lock (_publicationSync)
                return GetMinimumReclaimableSequence();
        }
    }

    public bool TryRegisterPublicationConsumer(out AdvancedGpuScenePublicationConsumerToken token)
    {
        lock (_publicationSync)
        {
            if (_publicationFaulted)
            {
                token = AdvancedGpuScenePublicationConsumerToken.Invalid;
                return false;
            }

            for (uint index = 1u; index < (uint)_consumerActive.Length; ++index)
            {
                if (_consumerActive[index] != 0)
                    continue;

                uint generation = NextGeneration(_consumerGenerations[index]);
                _consumerGenerations[index] = generation;
                _consumerAcknowledgements[index] = _lastSealedPublicationSequence;
                _consumerActive[index] = 1;
                token = new AdvancedGpuScenePublicationConsumerToken(index, generation);
                return true;
            }

            token = AdvancedGpuScenePublicationConsumerToken.Invalid;
            return false;
        }
    }

    public bool UnregisterPublicationConsumer(AdvancedGpuScenePublicationConsumerToken token)
    {
        lock (_publicationSync)
        {
            if (!IsConsumerTokenCurrent(token))
                return false;

            _consumerActive[token.Index] = 0;
            _consumerAcknowledgements[token.Index] = 0u;
            DrainAcknowledgedPublicationsAndReclaimTombstones();
            return true;
        }
    }

    /// <summary>
    /// Begins a coherent structural publication. All table journal entries made
    /// before sealing are stamped with the returned active sequence.
    /// </summary>
    public bool BeginPublication()
        => TryBeginPublication(out _);

    internal bool TryBeginPublication(
        out AdvancedGpuScenePublicationTransaction transaction)
    {
        lock (_publicationSync)
        {
            transaction = default;
            if (_publicationFaulted || _activePublicationSequence != 0u ||
                !EnsurePublicationCapacity())
                return false;

            int ringIndex =
                (_publicationHead + _publicationCount) % _publicationRing.Length;
            if (!CanPreparePublicationCore(ringIndex))
            {
                // A retained entry may have been sealed before the live tables
                // grew. The selected ring entry is free, so refresh only that
                // snapshot at this legal boundary and leave retained snapshots
                // untouched for their current consumers.
                _publicationSnapshots[ringIndex] =
                    new AdvancedGpuScenePublicationSnapshot(this, _capacities);
                if (!CanPreparePublicationCore(ringIndex))
                    return false;
            }

            _activePublicationSequence = _nextPublicationSequence;
            _activePublicationRingIndex = ringIndex;
            _preparedPublication = default;
            _publicationPrepared = false;
            BeginTablePublicationGeneration(_activePublicationSequence);
            BeginStructuralUpdateCore();
            transaction = new AdvancedGpuScenePublicationTransaction(
                _databaseEpoch,
                _activePublicationSequence,
                ringIndex);
            return true;
        }
    }

    /// <summary>
    /// Seals the active batch into the ordered ring. The supplied generations
    /// describe independently uploadable images and do not imply backend state.
    /// </summary>
    public bool SealPublication(
        ulong frameGeneration,
        ulong topologyGeneration,
        ulong contentGeneration,
        ulong lookupGeneration,
        out AdvancedGpuScenePublicationReference reference)
    {
        AdvancedGpuScenePublicationTransaction transaction;
        lock (_publicationSync)
        {
            reference = default;
            if (_activePublicationSequence == 0u || _activePublicationRingIndex < 0)
                return false;
            transaction = new AdvancedGpuScenePublicationTransaction(
                _databaseEpoch,
                _activePublicationSequence,
                _activePublicationRingIndex);
        }

        if (!TryPreparePublication(
                in transaction,
                frameGeneration,
                topologyGeneration,
                contentGeneration,
                lookupGeneration,
                out _))
        {
            FaultActivePublication(
                in transaction,
                EAdvancedGpuScenePublicationFault.SnapshotCaptureFailed);
            return false;
        }

        return TryCommitPreparedPublication(in transaction, out reference);
    }

    internal bool TryPreparePublication(
        in AdvancedGpuScenePublicationTransaction transaction,
        ulong frameGeneration,
        ulong topologyGeneration,
        ulong contentGeneration,
        ulong lookupGeneration,
        out AdvancedGpuScenePublicationReference reference)
    {
        lock (_publicationSync)
        {
            reference = default;
            if (!IsActiveTransactionCurrent(in transaction) ||
                _publicationFaulted || _publicationPrepared)
            {
                return false;
            }

            AdvancedGpuScenePublication publication = new(
                _databaseEpoch,
                transaction.Sequence,
                frameGeneration,
                topologyGeneration,
                contentGeneration,
                lookupGeneration);
            AdvancedGpuScenePublicationSnapshot snapshot =
                _publicationSnapshots[transaction.RingIndex];
            if (!TrySealTablePublication(transaction.Sequence, snapshot) ||
                !snapshot.TryCaptureCanonicalDependencyState(transaction.Sequence, this))
                return false;

            _preparedPublication = publication;
            _publicationPrepared = true;
            reference = new AdvancedGpuScenePublicationReference(
                publication,
                snapshot);
            return true;
        }
    }

    internal bool TryCommitPreparedPublication(
        in AdvancedGpuScenePublicationTransaction transaction,
        out AdvancedGpuScenePublicationReference reference)
    {
        lock (_publicationSync)
        {
            reference = default;
            if (!IsActiveTransactionCurrent(in transaction) ||
                _publicationFaulted || !_publicationPrepared)
            {
                return false;
            }

            try
            {
                HandleLookups.PublishAfterPreflight(Scene, Materials, Resources);
            }
            catch
            {
                FaultActivePublicationCore(
                    in transaction,
                    EAdvancedGpuScenePublicationFault.LookupPublicationFailed);
                throw;
            }

            int ringIndex = transaction.RingIndex;
            _publicationRing[ringIndex] = _preparedPublication;
            _packagePinCounts[ringIndex] = 0u;
            _gpuPinCounts[ringIndex] = 0u;
            ++_publicationCount;
            _lastSealedPublicationSequence = _preparedPublication.Sequence;
            reference = new AdvancedGpuScenePublicationReference(
                _preparedPublication,
                _publicationSnapshots[ringIndex]);
            _activePublicationSequence = 0u;
            _activePublicationRingIndex = -1;
            _preparedPublication = default;
            _publicationPrepared = false;
            IncrementSequence(ref _nextPublicationSequence);
            return true;
        }
    }

    internal bool TryCaptureSubmissionPublication(
        in AdvancedGpuScenePublicationTransaction transaction,
        ReadOnlySpan<AdvancedDrawSubmissionRecord> records,
        ReadOnlySpan<AdvancedManagedDeformationSourceRow> deformationSources)
    {
        lock (_publicationSync)
        {
            return IsActiveTransactionCurrent(in transaction) && _publicationPrepared &&
                _publicationSnapshots[transaction.RingIndex].Submission.TryCapture(
                    transaction.Sequence, records, deformationSources);
        }
    }

    /// <summary>
    /// Captures bounded publication-retention diagnostics without exposing the
    /// ring or lease tables themselves.
    /// </summary>
    public AdvancedGpuSceneRetentionSnapshot CaptureRetentionSnapshot()
    {
        lock (_publicationSync)
        {
            ulong oldestSequence = _publicationCount == 0
                ? 0u
                : _publicationRing[_publicationHead].Sequence;
            uint oldestPackagePins = _publicationCount == 0
                ? 0u
                : _packagePinCounts[_publicationHead];
            uint oldestGpuPins = _publicationCount == 0
                ? 0u
                : _gpuPinCounts[_publicationHead];
            uint totalPackagePins = 0u;
            uint totalGpuPins = 0u;
            for (int offset = 0; offset < _publicationCount; ++offset)
            {
                int index = (_publicationHead + offset) % _publicationRing.Length;
                totalPackagePins = checked(totalPackagePins + _packagePinCounts[index]);
                totalGpuPins = checked(totalGpuPins + _gpuPinCounts[index]);
            }

            return new AdvancedGpuSceneRetentionSnapshot(
                _publicationCount,
                oldestSequence,
                oldestPackagePins,
                oldestGpuPins,
                totalPackagePins,
                totalGpuPins);
        }
    }

    internal void FaultActivePublication(
        in AdvancedGpuScenePublicationTransaction transaction,
        EAdvancedGpuScenePublicationFault fault)
    {
        lock (_publicationSync)
            FaultActivePublicationCore(in transaction, fault);
    }

    /// <summary>
    /// Compatibility form for publishers that explicitly stamp every table
    /// before sealing. New publishers should call <see cref="BeginPublication"/>
    /// so the database owns the generation selection.
    /// </summary>
    public bool SealPublication(
        ulong frameGeneration,
        ulong topologyGeneration,
        ulong contentGeneration,
        ulong lookupGeneration)
    {
        lock (_publicationSync)
        {
            if (_activePublicationSequence == 0u &&
                !TryBeginPublication(out _))
                return false;

            return SealPublication(
                frameGeneration,
                topologyGeneration,
                contentGeneration,
                lookupGeneration,
                out _);
        }
    }

    /// <summary>Gets the next unacknowledged publication for one ordered consumer.</summary>
    public bool TryAcquireNextPublication(
        AdvancedGpuScenePublicationConsumerToken token,
        out AdvancedGpuScenePublicationReference reference)
    {
        lock (_publicationSync)
        {
            reference = default;
            if (_publicationFaulted || !IsConsumerTokenCurrent(token))
                return false;

            ulong nextSequence = _consumerAcknowledgements[token.Index] + 1u;
            if (nextSequence == 0u || !TryFindPublication(nextSequence, out int ringIndex))
                return false;

            reference = new AdvancedGpuScenePublicationReference(
                _publicationRing[ringIndex],
                _publicationSnapshots[ringIndex]);
            return true;
        }
    }

    /// <summary>
    /// Returns immutable table deltas and remaps for a retained publication.
    /// Callers must keep a package or GPU lease for the entire span-use period.
    /// </summary>
    public bool TryGetPublicationSnapshot(
        in AdvancedGpuScenePublicationReference reference,
        out AdvancedGpuScenePublicationSnapshot snapshot)
    {
        lock (_publicationSync)
        {
            snapshot = null!;
            if (!TryFindPublication(reference, out int ringIndex))
                return false;

            snapshot = _publicationSnapshots[ringIndex];
            return true;
        }
    }

    /// <summary>
    /// Cumulatively acknowledges every publication through <paramref name="reference"/>.
    /// Acknowledgement alone does not release package or GPU leases.
    /// </summary>
    public bool AcknowledgePublication(
        AdvancedGpuScenePublicationConsumerToken token,
        in AdvancedGpuScenePublicationReference reference)
    {
        lock (_publicationSync)
        {
            if (!IsConsumerTokenCurrent(token) || !TryFindPublication(reference, out _))
                return false;
            if (reference.Sequence < _consumerAcknowledgements[token.Index])
                return false;

            _consumerAcknowledgements[token.Index] = reference.Sequence;
            DrainAcknowledgedPublicationsAndReclaimTombstones();
            return true;
        }
    }

    public bool TryAcquirePublicationLease(
        in AdvancedGpuScenePublicationReference reference,
        EAdvancedGpuScenePublicationPinKind kind,
        out AdvancedGpuScenePublicationLease lease)
    {
        lock (_publicationSync)
        {
            lease = default;
            if (_publicationFaulted ||
                !TryFindPublication(reference, out int ringIndex))
                return false;

            int leaseSlot = 0;
            for (int index = 1; index < _leaseActive.Length; ++index)
                if (_leaseActive[index] == 0)
                {
                    leaseSlot = index;
                    break;
                }
            if (leaseSlot == 0)
                return false;

            ref uint pinCount = ref (kind == EAdvancedGpuScenePublicationPinKind.Package
                ? ref _packagePinCounts[ringIndex]
                : ref _gpuPinCounts[ringIndex]);
            if (pinCount == uint.MaxValue)
                return false;

            ++pinCount;
            uint generation = NextGeneration(_leaseGenerations[leaseSlot]);
            _leaseGenerations[leaseSlot] = generation;
            _leaseReferences[leaseSlot] = reference;
            _leaseKinds[leaseSlot] = kind;
            _leaseActive[leaseSlot] = 1;
            lease = new AdvancedGpuScenePublicationLease(
                this,
                checked((uint)leaseSlot),
                generation,
                reference);
            return true;
        }
    }

    internal void ReleasePublicationLease(uint leaseSlot, uint leaseGeneration)
    {
        lock (_publicationSync)
        {
            if (leaseSlot == 0u || leaseSlot >= (uint)_leaseActive.Length ||
                _leaseActive[leaseSlot] == 0 ||
                _leaseGenerations[leaseSlot] != leaseGeneration)
            {
                return;
            }

            AdvancedGpuScenePublicationReference reference = _leaseReferences[leaseSlot];
            EAdvancedGpuScenePublicationPinKind kind = _leaseKinds[leaseSlot];
            if (!TryFindPublication(reference, out int ringIndex))
                throw new InvalidOperationException("The publication lease outlived its retained ring entry.");

            ref uint pinCount = ref (kind == EAdvancedGpuScenePublicationPinKind.Package
                ? ref _packagePinCounts[ringIndex]
                : ref _gpuPinCounts[ringIndex]);
            if (pinCount == 0u)
                throw new InvalidOperationException("The publication lease was released more than once.");

            --pinCount;
            _leaseActive[leaseSlot] = 0;
            _leaseReferences[leaseSlot] = default;
            DrainAcknowledgedPublicationsAndReclaimTombstones();
        }
    }

    public bool TryResolveDraw(
        AdvancedGpuHandle drawHandle,
        out AdvancedResolvedSharedDrawRecords resolved)
    {
        resolved = default;
        if (PublicationFaulted ||
            !Scene.TryResolveDraw(drawHandle, out AdvancedResolvedDrawRecords scene) ||
            !Materials.Materials.TryGet(scene.Draw.Material, out AdvancedMaterialRecord material))
        {
            return false;
        }

        resolved.Scene = scene;
        resolved.Material = material;
        return true;
    }

    public bool TryCreateDrawDependencySnapshot(
        AdvancedGpuHandle drawHandle,
        out AdvancedSharedDrawDependencySnapshot snapshot)
    {
        snapshot = default;
        if (PublicationFaulted ||
            !Scene.TryCreateDrawDependencySnapshot(
                drawHandle,
                out AdvancedDrawDependencySnapshot scene) ||
            !Materials.Materials.TryGetDenseIndex(
                scene.Material,
                out uint materialDenseIndex))
        {
            return false;
        }

        snapshot = new AdvancedSharedDrawDependencySnapshot(
            scene,
            materialDenseIndex);
        return true;
    }

    public void BeginStructuralUpdate()
    {
        if (!BeginPublication())
            throw new InvalidOperationException("The bounded publication ring is full or another publication is already active.");
    }

    private void BeginStructuralUpdateCore()
    {
        Scene.BeginStructuralUpdate();
        Materials.Materials.ClearPublishedRemaps();
        Materials.Kernels.ClearPublishedRemaps();
        Materials.Layouts.ClearPublishedRemaps();
        Resources.Textures.ClearPublishedRemaps();
        Resources.Samplers.ClearPublishedRemaps();
        Resources.Lights.ClearPublishedRemaps();
        Resources.Shadows.ClearPublishedRemaps();
        Resources.Probes.ClearPublishedRemaps();
        Resources.Environments.ClearPublishedRemaps();
        Resources.Decals.ClearPublishedRemaps();
        Resources.GiResources.ClearPublishedRemaps();
    }

    /// <summary>
    /// Compacts all physical tables, publishes remaps, and refreshes the GPU handle
    /// lookup image. Returns -1 when a retained remap batch has exhausted capacity.
    /// </summary>
    public int CompactAndPublish()
    {
        if (PublicationFaulted)
            return -1;

        int total = Scene.CompactAndPublishRemaps();
        if (total < 0 ||
            !Accumulate(Materials.Materials.Compact(), ref total) ||
            !Accumulate(Materials.Kernels.Compact(), ref total) ||
            !Accumulate(Materials.Layouts.Compact(), ref total) ||
            !Accumulate(Resources.Textures.Compact(), ref total) ||
            !Accumulate(Resources.Samplers.Compact(), ref total) ||
            !Accumulate(Resources.Lights.Compact(), ref total) ||
            !Accumulate(Resources.Shadows.Compact(), ref total) ||
            !Accumulate(Resources.Probes.Compact(), ref total) ||
            !Accumulate(Resources.Environments.Compact(), ref total) ||
            !Accumulate(Resources.Decals.Compact(), ref total) ||
            !Accumulate(Resources.GiResources.Compact(), ref total) ||
            !HandleLookups.Publish(Scene, Materials, Resources))
        {
            return -1;
        }

        return total;
    }

    public bool PublishHandleLookups()
    {
        lock (_publicationSync)
            return !_publicationFaulted && _activePublicationSequence == 0u &&
                HandleLookups.Publish(Scene, Materials, Resources);
    }

    public void GrowAtFrameBoundary(
        in AdvancedSharedGpuSceneCapacityProfile capacities)
    {
        if (!TryGrowAtFrameBoundary(capacities))
        {
            throw new InvalidOperationException(
                "Shared GPU-scene storage cannot grow during an active or faulted publication.");
        }
    }

    /// <summary>
    /// Attempts boundary-only growth without invalidating retained publications.
    /// Live tables replace their backing arrays, while only free publication-ring
    /// snapshots are rebuilt at the larger capacity. Retained snapshots keep their
    /// exact immutable images until their consumers release them.
    /// </summary>
    public bool TryGrowAtFrameBoundary(
        in AdvancedSharedGpuSceneCapacityProfile capacities)
    {
        lock (_publicationSync)
        {
            DrainAcknowledgedPublicationsAndReclaimTombstones();
            if (_publicationFaulted || _activePublicationSequence != 0u)
                return false;

            AdvancedSharedGpuSceneCapacityProfile grown =
                GrowCapacityProfile(in _capacities, in capacities);
            if (grown == _capacities)
                return true;

            Scene.GrowAtFrameBoundary(grown.Scene);
            Materials.GrowAtFrameBoundary(
                grown.MaterialRecords,
                grown.ShadingKernels,
                grown.MaterialLayouts,
                grown.MaterialLayoutMembers,
                grown.MaterialConstantWords,
                grown.MaterialTextureBindings);
            Resources.GrowAtFrameBoundary(
                grown.TextureRecords,
                grown.SamplerRecords,
                grown.LightRecords,
                grown.ShadowRecords,
                grown.ProbeRecords,
                grown.EnvironmentRecords,
                grown.DecalRecords,
                grown.GiResourceRecords);
            HandleLookups.RebuildAtFrameBoundary(Scene, Materials, Resources);
            _capacities = grown;
            for (int index = 0; index < _publicationSnapshots.Length; ++index)
            {
                if (_publicationRing[index].Sequence != 0u)
                    continue;

                _publicationSnapshots[index] =
                    new AdvancedGpuScenePublicationSnapshot(this, _capacities);
            }
            return true;
        }
    }

    /// <summary>
    /// Grows only append-only geometry payload arenas at a publication boundary.
    /// Retained publications are legal here because each arena growth replaces
    /// its backing array; older snapshots keep their previous immutable array.
    /// Record tables, lookup images, and ring-owned snapshot storage are not
    /// resized by this operation.
    /// </summary>
    public bool TryGrowGeometryArenasAtFrameBoundary(
        in AdvancedGpuSceneCapacityProfile requested)
    {
        lock (_publicationSync)
        {
            if (_publicationFaulted || _activePublicationSequence != 0u)
                return false;

            AdvancedGpuSceneCapacityProfile current = _capacities.Scene;
            AdvancedGpuSceneCapacityProfile grown = current with
            {
                StaticVertexBytes = Math.Max(
                    current.StaticVertexBytes,
                    requested.StaticVertexBytes),
                IndexBytes = Math.Max(current.IndexBytes, requested.IndexBytes),
                PreSkinnedCurrentBytes = Math.Max(
                    current.PreSkinnedCurrentBytes,
                    requested.PreSkinnedCurrentBytes),
                PreSkinnedPreviousBytes = Math.Max(
                    current.PreSkinnedPreviousBytes,
                    requested.PreSkinnedPreviousBytes),
                MeshletBytes = Math.Max(
                    current.MeshletBytes,
                    requested.MeshletBytes),
                MeshletDescriptorBytes = Math.Max(
                    current.MeshletDescriptorBytes,
                    requested.MeshletDescriptorBytes),
                MeshletVertexIndexBytes = Math.Max(
                    current.MeshletVertexIndexBytes,
                    requested.MeshletVertexIndexBytes),
                MeshletTriangleWordBytes = Math.Max(
                    current.MeshletTriangleWordBytes,
                    requested.MeshletTriangleWordBytes),
            };
            Scene.Geometry.GrowAtBoundary(
                current.GeometryRecords,
                grown.StaticVertexBytes,
                grown.IndexBytes,
                grown.PreSkinnedCurrentBytes,
                grown.PreSkinnedPreviousBytes,
                grown.MeshletBytes,
                grown.MeshletDescriptorBytes,
                grown.MeshletVertexIndexBytes,
                grown.MeshletTriangleWordBytes);
            _capacities = _capacities with { Scene = grown };
            return true;
        }
    }

    /// <summary>
    /// Releases tombstoned record slots that are acknowledged by every consumer
    /// and not retained by a package or GPU lease. Returns reclaimed slot count.
    /// </summary>
    public int ReclaimAcknowledgedTombstones()
    {
        lock (_publicationSync)
            return ReclaimAcknowledgedTombstonesCore();
    }

    private int ReclaimAcknowledgedTombstonesCore()
    {
        ulong safeSequence = GetMinimumReclaimableSequence();
        if (safeSequence <= _lastReclaimedPublicationSequence)
            return 0;

        int reclaimed = 0;
        reclaimed += Scene.Draws.ReclaimAcknowledged(safeSequence);
        reclaimed += Scene.Instances.ReclaimAcknowledged(safeSequence);
        reclaimed += Scene.Transforms.ReclaimAcknowledged(safeSequence);
        reclaimed += Scene.Deformations.ReclaimAcknowledged(safeSequence);
        reclaimed += Scene.RenderStates.ReclaimAcknowledged(safeSequence);
        reclaimed += Scene.EditorIdentities.ReclaimAcknowledged(safeSequence);
        reclaimed += Scene.Geometry.Records.ReclaimAcknowledged(safeSequence);
        reclaimed += Materials.Materials.ReclaimAcknowledged(safeSequence);
        reclaimed += Materials.Kernels.ReclaimAcknowledged(safeSequence);
        reclaimed += Materials.Layouts.ReclaimAcknowledged(safeSequence);
        reclaimed += Resources.Textures.ReclaimAcknowledged(safeSequence);
        reclaimed += Resources.Samplers.ReclaimAcknowledged(safeSequence);
        reclaimed += Resources.Lights.ReclaimAcknowledged(safeSequence);
        reclaimed += Resources.Shadows.ReclaimAcknowledged(safeSequence);
        reclaimed += Resources.Probes.ReclaimAcknowledged(safeSequence);
        reclaimed += Resources.Environments.ReclaimAcknowledged(safeSequence);
        reclaimed += Resources.Decals.ReclaimAcknowledged(safeSequence);
        reclaimed += Resources.GiResources.ReclaimAcknowledged(safeSequence);
        _lastReclaimedPublicationSequence = safeSequence;
        return reclaimed;
    }

    private static bool Accumulate(int result, ref int total)
    {
        if (result < 0)
            return false;

        total = checked(total + result);
        return true;
    }

    private static AdvancedSharedGpuSceneCapacityProfile GrowCapacityProfile(
        in AdvancedSharedGpuSceneCapacityProfile current,
        in AdvancedSharedGpuSceneCapacityProfile requested)
        => new(
            GrowCapacityProfile(current.Scene, requested.Scene),
            Math.Max(current.MaterialRecords, requested.MaterialRecords),
            Math.Max(current.ShadingKernels, requested.ShadingKernels),
            Math.Max(current.MaterialLayouts, requested.MaterialLayouts),
            Math.Max(current.MaterialLayoutMembers, requested.MaterialLayoutMembers),
            Math.Max(current.MaterialConstantWords, requested.MaterialConstantWords),
            Math.Max(current.MaterialTextureBindings, requested.MaterialTextureBindings),
            Math.Max(current.TextureRecords, requested.TextureRecords),
            Math.Max(current.SamplerRecords, requested.SamplerRecords),
            Math.Max(current.LightRecords, requested.LightRecords),
            Math.Max(current.ShadowRecords, requested.ShadowRecords),
            Math.Max(current.ProbeRecords, requested.ProbeRecords),
            Math.Max(current.EnvironmentRecords, requested.EnvironmentRecords),
            Math.Max(current.DecalRecords, requested.DecalRecords),
            Math.Max(current.GiResourceRecords, requested.GiResourceRecords));

    private static AdvancedGpuSceneCapacityProfile GrowCapacityProfile(
        AdvancedGpuSceneCapacityProfile current,
        AdvancedGpuSceneCapacityProfile requested)
        => new(
            Math.Max(current.DrawRecords, requested.DrawRecords),
            Math.Max(current.InstanceRecords, requested.InstanceRecords),
            Math.Max(current.TransformRecords, requested.TransformRecords),
            Math.Max(current.DeformationRecords, requested.DeformationRecords),
            Math.Max(current.RenderStateRecords, requested.RenderStateRecords),
            Math.Max(current.EditorIdentityRecords, requested.EditorIdentityRecords),
            Math.Max(current.GeometryRecords, requested.GeometryRecords),
            Math.Max(current.StaticVertexBytes, requested.StaticVertexBytes),
            Math.Max(current.IndexBytes, requested.IndexBytes),
            Math.Max(current.PreSkinnedCurrentBytes, requested.PreSkinnedCurrentBytes),
            Math.Max(current.PreSkinnedPreviousBytes, requested.PreSkinnedPreviousBytes),
            Math.Max(current.MeshletBytes, requested.MeshletBytes),
            Math.Max(current.MeshletDescriptorBytes, requested.MeshletDescriptorBytes),
            Math.Max(current.MeshletVertexIndexBytes, requested.MeshletVertexIndexBytes),
            Math.Max(current.MeshletTriangleWordBytes, requested.MeshletTriangleWordBytes));

    private bool EnsurePublicationCapacity()
    {
        DrainAcknowledgedPublicationsAndReclaimTombstones();
        return _publicationCount < _publicationRing.Length;
    }

    private void DrainAcknowledgedPublicationsAndReclaimTombstones()
    {
        ulong safeSequence = GetMinimumReclaimableSequence();
        while (_publicationCount > 0 &&
               _publicationRing[_publicationHead].Sequence <= safeSequence &&
               _packagePinCounts[_publicationHead] == 0u &&
               _gpuPinCounts[_publicationHead] == 0u)
        {
            _publicationRing[_publicationHead] = default;
            _publicationHead = (_publicationHead + 1) % _publicationRing.Length;
            --_publicationCount;
        }

        ReclaimAcknowledgedTombstonesCore();
    }

    private ulong GetMinimumAcknowledgedSequence()
    {
        ulong minimum = _lastSealedPublicationSequence;
        for (int index = 1; index < _consumerActive.Length; ++index)
        {
            if (_consumerActive[index] != 0)
                minimum = Math.Min(minimum, _consumerAcknowledgements[index]);
        }

        return minimum;
    }

    private ulong GetMinimumReclaimableSequence()
    {
        ulong minimum = GetMinimumAcknowledgedSequence();
        for (int offset = 0; offset < _publicationCount; ++offset)
        {
            int index = (_publicationHead + offset) % _publicationRing.Length;
            if (_packagePinCounts[index] == 0u && _gpuPinCounts[index] == 0u)
                continue;

            ulong pinnedSequence = _publicationRing[index].Sequence;
            if (pinnedSequence == 0u)
                continue;
            minimum = Math.Min(minimum, pinnedSequence - 1u);
        }

        return minimum;
    }

    private bool TryFindPublication(ulong sequence, out int ringIndex)
    {
        ringIndex = -1;
        if (sequence == 0u)
            return false;

        for (int offset = 0; offset < _publicationCount; ++offset)
        {
            int index = (_publicationHead + offset) % _publicationRing.Length;
            if (_publicationRing[index].Sequence != sequence)
                continue;

            ringIndex = index;
            return true;
        }

        return false;
    }

    private bool TryFindPublication(
        in AdvancedGpuScenePublicationReference reference,
        out int ringIndex)
    {
        ringIndex = -1;
        if (!reference.IsValid || reference.DatabaseEpoch != _databaseEpoch ||
            !TryFindPublication(reference.Sequence, out ringIndex))
        {
            return false;
        }

        return _publicationRing[ringIndex] == reference.Publication;
    }

    private bool IsConsumerTokenCurrent(AdvancedGpuScenePublicationConsumerToken token)
        => token.IsValid && token.Index < (uint)_consumerActive.Length &&
           _consumerActive[token.Index] != 0 &&
           _consumerGenerations[token.Index] == token.Generation;

    private bool IsActiveTransactionCurrent(
        in AdvancedGpuScenePublicationTransaction transaction)
        => transaction.IsValid &&
           transaction.DatabaseEpoch == _databaseEpoch &&
           transaction.Sequence == _activePublicationSequence &&
           transaction.RingIndex == _activePublicationRingIndex;

    private bool CanPreparePublicationCore(int ringIndex)
    {
        if ((uint)ringIndex >= (uint)_publicationSnapshots.Length ||
            !HandleLookups.CanPublish(Scene, Materials, Resources))
        {
            return false;
        }

        AdvancedGpuScenePublicationSnapshot snapshot =
            _publicationSnapshots[ringIndex];
        return Scene.Draws.CanSealPublication(snapshot.Draws) &&
            Scene.Instances.CanSealPublication(snapshot.Instances) &&
            Scene.Transforms.CanSealPublication(snapshot.Transforms) &&
            Scene.Deformations.CanSealPublication(snapshot.Deformations) &&
            Scene.RenderStates.CanSealPublication(snapshot.RenderStates) &&
            Scene.EditorIdentities.CanSealPublication(snapshot.EditorIdentities) &&
            Scene.Geometry.Records.CanSealPublication(snapshot.Geometry) &&
            Materials.Materials.CanSealPublication(snapshot.Materials) &&
            Materials.Kernels.CanSealPublication(snapshot.Kernels) &&
            Materials.Layouts.CanSealPublication(snapshot.Layouts) &&
            Materials.CanSealPublication(snapshot.MaterialPayloads) &&
            Resources.Textures.CanSealPublication(snapshot.Textures) &&
            Resources.Samplers.CanSealPublication(snapshot.Samplers) &&
            Resources.Lights.CanSealPublication(snapshot.GlobalResources.Lights) &&
            Resources.Shadows.CanSealPublication(snapshot.GlobalResources.Shadows) &&
            Resources.Probes.CanSealPublication(snapshot.GlobalResources.Probes) &&
            Resources.Environments.CanSealPublication(snapshot.GlobalResources.Environments) &&
            Resources.Decals.CanSealPublication(snapshot.GlobalResources.Decals) &&
            Resources.GiResources.CanSealPublication(snapshot.GlobalResources.GiResources);
    }

    private void FaultActivePublicationCore(
        in AdvancedGpuScenePublicationTransaction transaction,
        EAdvancedGpuScenePublicationFault fault)
    {
        if (!IsActiveTransactionCurrent(in transaction))
            return;

        _publicationFaulted = true;
        _publicationFault = fault == EAdvancedGpuScenePublicationFault.None
            ? EAdvancedGpuScenePublicationFault.InvariantFailure
            : fault;
        _publicationFaultSequence = transaction.Sequence;
        _preparedPublication = default;
        _publicationPrepared = false;
    }

    private void BeginTablePublicationGeneration(ulong sequence)
    {
        Scene.Draws.BeginPublicationGeneration(sequence);
        Scene.Instances.BeginPublicationGeneration(sequence);
        Scene.Transforms.BeginPublicationGeneration(sequence);
        Scene.Deformations.BeginPublicationGeneration(sequence);
        Scene.RenderStates.BeginPublicationGeneration(sequence);
        Scene.EditorIdentities.BeginPublicationGeneration(sequence);
        Scene.Geometry.Records.BeginPublicationGeneration(sequence);
        Materials.Materials.BeginPublicationGeneration(sequence);
        Materials.Kernels.BeginPublicationGeneration(sequence);
        Materials.Layouts.BeginPublicationGeneration(sequence);
        Resources.Textures.BeginPublicationGeneration(sequence);
        Resources.Samplers.BeginPublicationGeneration(sequence);
        Resources.Lights.BeginPublicationGeneration(sequence);
        Resources.Shadows.BeginPublicationGeneration(sequence);
        Resources.Probes.BeginPublicationGeneration(sequence);
        Resources.Environments.BeginPublicationGeneration(sequence);
        Resources.Decals.BeginPublicationGeneration(sequence);
        Resources.GiResources.BeginPublicationGeneration(sequence);
    }

    private bool TrySealTablePublication(
        ulong sequence,
        AdvancedGpuScenePublicationSnapshot snapshot)
    {
        if (!Scene.Draws.TrySealPublication(sequence, snapshot.Draws) ||
            !Scene.Instances.TrySealPublication(sequence, snapshot.Instances) ||
            !Scene.Transforms.TrySealPublication(sequence, snapshot.Transforms) ||
            !Scene.Deformations.TrySealPublication(sequence, snapshot.Deformations) ||
            !Scene.RenderStates.TrySealPublication(sequence, snapshot.RenderStates) ||
            !Scene.EditorIdentities.TrySealPublication(sequence, snapshot.EditorIdentities) ||
            !Scene.Geometry.Records.TrySealPublication(sequence, snapshot.Geometry) ||
            !Materials.Materials.TrySealPublication(sequence, snapshot.Materials) ||
            !Materials.Kernels.TrySealPublication(sequence, snapshot.Kernels) ||
            !Materials.Layouts.TrySealPublication(sequence, snapshot.Layouts) ||
            !Materials.TrySealPublication(sequence, snapshot.MaterialPayloads) ||
            snapshot.MaterialPayloads.Sequence != sequence ||
            !Resources.Textures.TrySealPublication(sequence, snapshot.Textures) ||
            !Resources.Samplers.TrySealPublication(sequence, snapshot.Samplers) ||
            !Resources.Lights.TrySealPublication(sequence, snapshot.GlobalResources.Lights) ||
            !Resources.Shadows.TrySealPublication(sequence, snapshot.GlobalResources.Shadows) ||
            !Resources.Probes.TrySealPublication(sequence, snapshot.GlobalResources.Probes) ||
            !Resources.Environments.TrySealPublication(sequence, snapshot.GlobalResources.Environments) ||
            !Resources.Decals.TrySealPublication(sequence, snapshot.GlobalResources.Decals) ||
            !Resources.GiResources.TrySealPublication(sequence, snapshot.GlobalResources.GiResources))
        {
            return false;
        }

        snapshot.GeometryPayloads.Capture();
        return snapshot.TryCaptureResourceTableState(
            sequence,
            Resources.Generations);
    }

    private static ulong CreateDatabaseEpoch()
    {
        ulong epoch = unchecked((ulong)System.Threading.Interlocked.Increment(ref s_nextDatabaseEpoch));
        return epoch == 0u ? 1u : epoch;
    }

    private static uint NextGeneration(uint generation)
    {
        ++generation;
        return generation == 0u ? 1u : generation;
    }

    private static void IncrementSequence(ref ulong sequence)
    {
        ++sequence;
        if (sequence == 0u)
            throw new InvalidOperationException("Advanced GPU scene publication sequence overflowed.");
    }
}
