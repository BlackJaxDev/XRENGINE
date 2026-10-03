using System.Threading;
using XREngine.Rendering.Meshlets;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Commands;

public partial class GPUScene
{
    private const int MeshSubmissionPublicationSlots = 8;
    private const int MeshSubmissionLeaseSlots = 256;
    private const int MaxMeshSubmissionRecords = 1 << 20;
    private GpuMeshSubmissionRecord[] _updatingMeshSubmissions = [];
    private GpuMeshSubmissionPublication[]? _meshSubmissionPublications;
    private GpuMeshSubmissionPublication? _currentMeshSubmissionPublication;
    private GpuMeshSubmissionPublication?[]? _meshSubmissionLeasePublications;
    private ulong[]? _meshSubmissionLeaseGenerations;
    private ulong _meshSubmissionSequence;
    private ulong _meshSubmissionLeaseGeneration;
    private int _meshSubmissionPublicationRequested;
    private bool _meshSubmissionCaptureEnabled;
    private ulong _meshSubmissionFrameId;
    private bool _hasMeshSubmissionFrameId;

    /// <summary>Supplies the world swap identity before an explicit output enters its render scope.</summary>
    internal void SetMeshSubmissionFrameId(ulong frameId)
    {
        if (Volatile.Read(ref _meshSubmissionPublicationRequested) == 0)
            return;
        using (_lock.EnterScope())
        {
            _meshSubmissionFrameId = frameId;
            _hasMeshSubmissionFrameId = true;
        }
    }

    /// <summary>Requests the material-independent resident projection at the next scene swap.</summary>
    public void RequestMeshSubmissionPublication()
    {
        using (_lock.EnterScope())
        {
            _meshSubmissionCaptureEnabled = true;
            Interlocked.Exchange(ref _meshSubmissionPublicationRequested, 1);
        }
    }

    /// <summary>
    /// Pins the last complete scene projection. Keep the lease until all CPU and
    /// backend queue consumers have finished; exhaustion never falls back to live sources.
    /// </summary>
    public bool TryAcquireMeshSubmissionPublication(out GpuMeshSubmissionPublicationLease lease)
    {
        using (_lock.EnterScope())
            return TryCreateMeshSubmissionLease(_currentMeshSubmissionPublication, out lease);
    }

    internal GpuMeshSubmissionPublication? GetMeshSubmissionLeasePublication(int tokenIndex, ulong generation)
    {
        using (_lock.EnterScope())
            return IsMeshSubmissionLeaseCurrent(tokenIndex, generation)
                ? _meshSubmissionLeasePublications![tokenIndex] : null;
    }

    internal bool TryRetainMeshSubmissionPublication(int tokenIndex, ulong generation,
        out GpuMeshSubmissionPublicationLease lease)
    {
        using (_lock.EnterScope())
        {
            lease = default;
            return IsMeshSubmissionLeaseCurrent(tokenIndex, generation)
                && TryCreateMeshSubmissionLease(_meshSubmissionLeasePublications![tokenIndex], out lease);
        }
    }

    internal void ReleaseMeshSubmissionPublication(int tokenIndex, ulong generation)
    {
        using (_lock.EnterScope())
        {
            if (!IsMeshSubmissionLeaseCurrent(tokenIndex, generation))
                return;
            GpuMeshSubmissionPublication publication = _meshSubmissionLeasePublications![tokenIndex]!;
            _meshSubmissionLeasePublications[tokenIndex] = null;
            publication.PinCount--;
            if (publication.PinCount == 0 && !ReferenceEquals(publication, _currentMeshSubmissionPublication))
                publication.ClearRetainedSources();
        }
    }

    private bool IsMeshSubmissionLeaseCurrent(int tokenIndex, ulong generation)
        => generation != 0 && _meshSubmissionLeaseGenerations is { } generations
           && (uint)tokenIndex < (uint)generations.Length
           && generations[tokenIndex] == generation
           && _meshSubmissionLeasePublications![tokenIndex] is not null;

    private bool TryCreateMeshSubmissionLease(GpuMeshSubmissionPublication? publication,
        out GpuMeshSubmissionPublicationLease lease)
    {
        lease = default;
        if (publication is null || _meshSubmissionLeasePublications is null)
            return false;
        for (int index = 0; index < _meshSubmissionLeasePublications.Length; index++)
        {
            if (_meshSubmissionLeasePublications[index] is not null)
                continue;
            ulong generation = checked(++_meshSubmissionLeaseGeneration);
            _meshSubmissionLeasePublications[index] = publication;
            _meshSubmissionLeaseGenerations![index] = generation;
            publication.PinCount++;
            lease = new(this, index, generation);
            return true;
        }
        return false;
    }

    private void CaptureUpdatingMeshSubmission(uint index, in GpuSceneMeshCommandSnapshot snapshot,
        XRMesh mesh, XRMaterial material, int primitiveIndex, uint lodCount,
        in DrawMetadata metadata, in BoundsGpu bounds)
    {
        if (!_meshSubmissionCaptureEnabled)
            return;
        EnsureUpdatingMeshSubmissionCapacity(index + 1);
        GpuMeshSubmissionRecord previous = _updatingMeshSubmissions[index];
        XRMeshRenderer renderer = snapshot.Renderer
            ?? throw new InvalidOperationException("GPUScene.MeshSubmission.RendererMissing: a resident command must have a captured renderer.");
        MeshletPayload? payload = mesh.MeshletPayload;
        _logicalMeshStates.TryGetValue(metadata.LogicalMeshID, out LogicalMeshState? lodState);
        _updatingMeshSubmissions[index] = new()
        {
            Source = _commandIndexLookup[index].command,
            CommandIndex = index,
            StableQueryKey = snapshot.StableQueryKey,
            PrimitiveIndex = primitiveIndex,
            SourcePrimitiveCount = Math.Max(1, renderer.Submeshes.Count),
            RenderPass = snapshot.RenderPass,
            SourceOrder = ((ulong)index << 32) | unchecked((uint)primitiveIndex),
            Renderer = renderer,
            Mesh = mesh,
            Material = material,
            MaterialOverride = snapshot.MaterialOverride,
            RenderOptionsOverride = snapshot.RenderOptionsOverride,
            SourceBindings = GpuMeshSubmissionSourceBindings.Capture(mesh, renderer, material, previous.SourceBindings),
            Metadata = metadata,
            AuthoredInstanceCount = snapshot.Instances,
            AuthoredPrimitiveInstanceCount = renderer.Submeshes.Count == 0 ? 1u : renderer.Submeshes[primitiveIndex].InstanceCount,
            Bounds = bounds,
            CurrentWorld = snapshot.ModelMatrix,
            PreviousWorld = snapshot.ModelMatrix,
            LodTransforms = snapshot.LodTransforms,
            WorldMatrixIsModelMatrix = snapshot.WorldMatrixIsModelMatrix,
            ForceCpuRendering = snapshot.ForceCpuRendering,
            HasSkinning = mesh.HasSkinning,
            HasBlendshapes = mesh.HasBlendshapes,
            BillboardMode = material.BillboardMode,
            ForceNoStereo = _commandIndexLookup[index].command is RenderCommandMesh2D,
            DisableMeshletCulling = snapshot.DisableMeshletCulling || !snapshot.Owner.Is3D
                || snapshot.Owner.LocalCullingVolume is null || snapshot.Owner.HasCullingIntersectionOverride,
            LodCount = lodCount,
            LodMetadata = lodState?.ToEntry() ?? default,
            MeshletPayload = payload,
            GeometryRevision = mesh.GeometryRevision,
            PayloadValidationRevision = payload?.ValidationRevision ?? 0,
            PayloadOwnerGeometryRevision = payload?.OwnerGeometryRevision ?? 0,
            PayloadOwnerValidationToken = payload?.OwnerValidationToken ?? 0,
        };
    }

    private void EnsureUpdatingMeshSubmissionCapacity(uint requiredCapacity)
    {
        if (requiredCapacity > MaxMeshSubmissionRecords)
            throw new InvalidOperationException("GPUScene.MeshSubmission.CapacityExceeded: the resident projection exceeds its bounded command capacity.");
        if (_updatingMeshSubmissions.Length >= requiredCapacity)
            return;
        int capacity = (int)System.Numerics.BitOperations.RoundUpToPowerOf2(Math.Max(64u, requiredCapacity));
        Array.Resize(ref _updatingMeshSubmissions, capacity);
        Array.Resize(ref _publishedMeshSubmissionLodTransforms, capacity);
        Array.Resize(ref _publishedMeshSubmissionSkinning, capacity);
    }

    private void RemoveUpdatingMeshSubmission(uint targetIndex, uint lastIndex)
    {
        ClearUpdatingMeshSubmissionLods(targetIndex);
        ClearUpdatingMeshSubmissionLods(lastIndex);
        if (lastIndex >= _updatingMeshSubmissions.Length)
            return;
        if (targetIndex != lastIndex)
        {
            GpuMeshSubmissionRecord moved = _updatingMeshSubmissions[lastIndex];
            DrawMetadata metadata = moved.Metadata;
            metadata.DrawID = targetIndex;
            metadata.BoundsID = targetIndex;
            _updatingMeshSubmissions[targetIndex] = moved with
            {
                CommandIndex = targetIndex,
                SourceOrder = ((ulong)targetIndex << 32) | unchecked((uint)moved.PrimitiveIndex),
                Metadata = metadata,
            };
            _publishedMeshSubmissionLodTransforms[targetIndex] = _publishedMeshSubmissionLodTransforms[lastIndex];
            _publishedMeshSubmissionSkinning[targetIndex] = _publishedMeshSubmissionSkinning[lastIndex];
        }
        _updatingMeshSubmissions[lastIndex] = default;
        _publishedMeshSubmissionLodTransforms[lastIndex] = null;
        _publishedMeshSubmissionSkinning[lastIndex] = false;
    }

    private void RefreshMeshSubmissionPayloadAtFrameBoundary(XRMesh mesh)
    {
        if (!_meshSubmissionCaptureEnabled)
            return;
        MeshletPayload? payload = mesh.MeshletPayload;
        for (uint index = 0; index < _updatingCommandCount && index < _updatingMeshSubmissions.Length; index++)
        {
            GpuMeshSubmissionRecord record = _updatingMeshSubmissions[index];
            if (!ReferenceEquals(record.Mesh, mesh))
                continue;
            _updatingMeshSubmissions[index] = record with
            {
                MeshletPayload = payload,
                GeometryRevision = mesh.GeometryRevision,
                PayloadValidationRevision = payload?.ValidationRevision ?? 0,
                PayloadOwnerGeometryRevision = payload?.OwnerGeometryRevision ?? 0,
                PayloadOwnerValidationToken = payload?.OwnerValidationToken ?? 0,
                SourceBindings = GpuMeshSubmissionSourceBindings.Capture(mesh, record.Renderer, record.Material, record.SourceBindings),
            };
        }
    }

    private void PublishMeshSubmissionIfRequested()
    {
        ulong frameId = _hasMeshSubmissionFrameId ? _meshSubmissionFrameId
            : _hasAdvancedGlobalResourceCapture ? _advancedGlobalResources.FrameId
            : RuntimeEngine.Rendering.State.RenderFrameId;
        _hasMeshSubmissionFrameId = false;
        if (Interlocked.Exchange(ref _meshSubmissionPublicationRequested, 0) == 0)
            return;
        if (_meshSubmissionPublications is null)
        {
            _meshSubmissionPublications = new GpuMeshSubmissionPublication[MeshSubmissionPublicationSlots];
            for (int index = 0; index < _meshSubmissionPublications.Length; index++)
                _meshSubmissionPublications[index] = new();
            _meshSubmissionLeasePublications = new GpuMeshSubmissionPublication?[MeshSubmissionLeaseSlots];
            _meshSubmissionLeaseGenerations = new ulong[MeshSubmissionLeaseSlots];
        }

        GpuMeshSubmissionPublication? destination = null;
        foreach (GpuMeshSubmissionPublication publication in _meshSubmissionPublications)
        {
            if (publication.PinCount != 0 || ReferenceEquals(publication, _currentMeshSubmissionPublication))
                continue;
            destination = publication;
            break;
        }
        if (destination is null)
            throw new InvalidOperationException("GPUScene.MeshSubmission.PublicationSlotsExhausted: all resident projections are retained by unfinished consumers.");

        CaptureMissingMeshSubmissionsAtFrameBoundary();
        for (uint index = 0; index < _updatingCommandCount; index++)
        {
            GpuMeshSubmissionRecord record = _updatingMeshSubmissions[index];
            // Binding owners can publish a replacement without changing the
            // command's selector fields. Capture that membership here so a
            // rejected render-time mutation becomes eligible at the next swap.
            GpuMeshSubmissionSourceBindings bindings = GpuMeshSubmissionSourceBindings.Capture(
                record.Mesh, record.Renderer, record.Material, record.SourceBindings);
            if (!ReferenceEquals(bindings, record.SourceBindings) || record.BillboardMode != record.Material.BillboardMode)
                _updatingMeshSubmissions[index] = record with
                {
                    SourceBindings = bindings,
                    BillboardMode = record.Material.BillboardMode,
                };
        }
        destination.BeginCapture(frameId, checked(++_meshSubmissionSequence),
            _updatingMeshSubmissions.AsSpan(0, checked((int)_updatingCommandCount)));
        Span<GpuMeshSubmissionRecord> records = destination.WritableRecords;
        for (int index = 0; index < records.Length; index++)
        {
            GpuMeshSubmissionRecord record = records[index];
            // These CPU-authored stream images were just published at this same
            // swap boundary. No visibility, counts, or GPU-written data is read back.
            DrawMetadata metadata = DrawMetadataBuffer.GetDataRawAtIndex<DrawMetadata>((uint)index);
            _logicalMeshStates.TryGetValue(metadata.LogicalMeshID, out LogicalMeshState? lodState);
            GpuMeshSubmissionLodTransforms? lodTransforms = record.LodTransforms;
            GpuMeshSubmissionLodTransforms? priorLodTransforms = _publishedMeshSubmissionLodTransforms[index];
            if (lodTransforms is { } currentLodTransforms)
            {
                GpuMeshSubmissionLodTransforms previousLodTransforms = priorLodTransforms ?? currentLodTransforms;
                // Component and source-model history advance at the same scene
                // publication boundary, including public live Add/Update calls.
                lodTransforms = currentLodTransforms with
                {
                    PreviousComponentWorld = previousLodTransforms.CurrentComponentWorld,
                    PreviousSkinningEnabled = previousLodTransforms.SkinningEnabled,
                };
            }
            records[index] = record with
            {
                Metadata = metadata,
                LodCount = lodState?.LODCount ?? record.LodCount,
                LodMetadata = lodState?.ToEntry() ?? record.LodMetadata,
                Bounds = BoundsBuffer.GetDataRawAtIndex<BoundsGpu>(metadata.BoundsID),
                CurrentWorld = TransformBuffer.GetDataRawAtIndex<TransformGpu>(metadata.TransformID).WorldMatrix,
                PreviousWorld = PrevTransformBuffer.GetDataRawAtIndex<TransformGpu>(metadata.TransformID).WorldMatrix,
                LodTransforms = lodTransforms,
            };
            CaptureMeshSubmissionLods(destination, index, in records[index], lodState,
                priorLodTransforms.HasValue ? _publishedMeshSubmissionSkinning[index] : record.HasSkinning);
        }
        destination.CompleteCapture();
        for (int index = 0; index < records.Length; index++)
        {
            _publishedMeshSubmissionLodTransforms[index] = records[index].LodTransforms;
            _publishedMeshSubmissionSkinning[index] = records[index].HasSkinning;
        }
        GpuMeshSubmissionPublication? prior = _currentMeshSubmissionPublication;
        _currentMeshSubmissionPublication = destination;
        prior?.ClearRetainedSources();
    }

    private void CaptureMissingMeshSubmissionsAtFrameBoundary()
    {
        EnsureUpdatingMeshSubmissionCapacity(_updatingCommandCount);
        for (uint index = 0; index < _updatingCommandCount; index++)
        {
            if (_updatingMeshSubmissions[index].Renderer is not null)
                continue;
            if (!_commandIndexLookup.TryGetValue(index, out var source))
                throw new InvalidOperationException("GPUScene.MeshSubmission.SourceMissing: a resident row lost its command owner before publication.");
            IRenderCommandMesh command = source.command;
            GpuSceneMeshCommandSnapshot snapshot = command is RenderCommandMesh3D command3D
                ? command3D.CaptureGpuSceneSnapshot()
                : new(command.Mesh, command.WorldMatrix, command.WorldMatrixIsModelMatrix,
                    command.MaterialOverride, command.Instances, command.RenderPass,
                    command.ForceCpuRendering, command.EditorHighlightBits, command.StableQueryKey,
                    default, command.RenderOptionsOverride);
            DrawMetadata metadata = DrawMetadataBuffer.GetDataRawAtIndex<DrawMetadata>(index);
            if (!_idToMesh.TryGetValue(metadata.MeshID, out XRMesh? mesh)
                || !_idToMaterial.TryGetValue(metadata.MaterialID, out XRMaterial? material))
                throw new InvalidOperationException("GPUScene.MeshSubmission.ResidentSourceMissing: a resident command has no existing geometry or material owner.");
            uint lodCount = _logicalMeshStates.TryGetValue(metadata.LogicalMeshID, out LogicalMeshState? state)
                ? state.LODCount : 1;
            CaptureUpdatingMeshSubmission(index, snapshot, mesh, material, source.subMeshIndex, lodCount,
                metadata, BoundsBuffer.GetDataRawAtIndex<BoundsGpu>(metadata.BoundsID));
        }
    }

    private void ResetMeshSubmissionPublications()
    {
        using (_lock.EnterScope())
        {
            Interlocked.Exchange(ref _meshSubmissionPublicationRequested, 0);
            Array.Clear(_updatingMeshSubmissions);
            Array.Clear(_updatingMeshSubmissionLodBindings);
            Array.Clear(_updatingMeshSubmissionOutlineBindings);
            Array.Clear(_publishedMeshSubmissionLodTransforms);
            Array.Clear(_publishedMeshSubmissionSkinning);
            _meshSubmissionCaptureEnabled = false;
            _hasMeshSubmissionFrameId = false;
            _currentMeshSubmissionPublication = null;
            if (_meshSubmissionPublications is null)
                return;
            foreach (GpuMeshSubmissionPublication publication in _meshSubmissionPublications)
                publication.ClearRetainedSources();
        }
    }
}
