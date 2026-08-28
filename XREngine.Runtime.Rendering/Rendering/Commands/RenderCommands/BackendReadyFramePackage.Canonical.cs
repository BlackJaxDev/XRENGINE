using XREngine.Data.Rendering;

namespace XREngine.Rendering.Commands;

public sealed partial class BackendReadyFramePackage
{
    private BackendReadyCanonicalViewRecord[] _canonicalViews = [];
    private BackendReadyCanonicalPassRecord[] _canonicalPasses = [];
    private BackendReadyCanonicalDirtyOwnerRange[] _canonicalDirtyOwnerRanges = [];
    private BackendReadyDiagnosticReadbackRequest[] _canonicalDiagnosticReadbackRequests = [];
    private BackendReadyCpuVisibleDrawRecord[] _canonicalCpuVisibleDraws = [];
    private BackendReadyOrderedExceptionRecord[] _canonicalOrderedExceptions = [];
    private BackendTemplateProjectionDelta[] _canonicalTemplateProjectionDeltas = [];
    private AdvancedGpuScenePublicationLease? _canonicalPublicationLease;
    private int _canonicalViewCount;
    private int _canonicalPassCount;
    private int _canonicalDirtyOwnerRangeCount;
    private int _canonicalDiagnosticReadbackRequestCount;
    private int _canonicalCpuVisibleDrawCount;
    private int _canonicalOrderedExceptionCount;
    private int _canonicalTemplateProjectionDeltaCount;

    /// <summary>
    /// Canonical resident scene publication captured for this package. The
    /// package only references this publication; allocation and retirement
    /// remain the responsibility of the shared scene database.
    /// </summary>
    public BackendReadyCanonicalScenePublication CanonicalScenePublication { get; private set; }

    /// <summary>
    /// Frame-scoped state independent of visible command enumeration.
    /// </summary>
    public BackendReadyCanonicalFrameRecord CanonicalFrame { get; private set; }

    public BackendReadySubmissionResolution SubmissionResolution { get; private set; }
    public ReadOnlySpan<BackendReadyCanonicalViewRecord> CanonicalViews => _canonicalViews.AsSpan(0, _canonicalViewCount);
    public ReadOnlySpan<BackendReadyCanonicalPassRecord> CanonicalPasses => _canonicalPasses.AsSpan(0, _canonicalPassCount);
    public ReadOnlySpan<BackendReadyCanonicalDirtyOwnerRange> CanonicalDirtyOwnerRanges => _canonicalDirtyOwnerRanges.AsSpan(0, _canonicalDirtyOwnerRangeCount);
    public ReadOnlySpan<BackendReadyDiagnosticReadbackRequest> DiagnosticReadbackRequests => _canonicalDiagnosticReadbackRequests.AsSpan(0, _canonicalDiagnosticReadbackRequestCount);
    public ReadOnlySpan<BackendReadyCpuVisibleDrawRecord> CpuVisibleDraws => _canonicalCpuVisibleDraws.AsSpan(0, _canonicalCpuVisibleDrawCount);
    public ReadOnlySpan<BackendReadyOrderedExceptionRecord> OrderedExceptions => _canonicalOrderedExceptions.AsSpan(0, _canonicalOrderedExceptionCount);
    public ReadOnlySpan<BackendTemplateProjectionDelta> TemplateProjectionDeltas => _canonicalTemplateProjectionDeltas.AsSpan(0, _canonicalTemplateProjectionDeltaCount);

    /// <summary>
    /// Captures backend-facing projections of canonical resident state. Callers
    /// supply preallocated span-backed data; this method copies it into the
    /// package's reusable arrays and performs no scene traversal.
    /// </summary>
    internal void PrepareCanonical(
        in BackendReadyCanonicalScenePublication scenePublication,
        in BackendReadyCanonicalFrameRecord frame,
        ReadOnlySpan<BackendReadyCanonicalViewRecord> views,
        ReadOnlySpan<BackendReadyCanonicalPassRecord> passes,
        in BackendReadySubmissionResolution submissionResolution,
        ReadOnlySpan<BackendReadyCanonicalDirtyOwnerRange> dirtyOwnerRanges,
        ReadOnlySpan<BackendReadyDiagnosticReadbackRequest> diagnosticReadbackRequests,
        ReadOnlySpan<BackendReadyCpuVisibleDrawRecord> cpuVisibleDraws,
        ReadOnlySpan<BackendReadyOrderedExceptionRecord> orderedExceptions,
        ReadOnlySpan<BackendTemplateProjectionDelta> templateProjectionDeltas)
    {
        CanonicalScenePublication = scenePublication;
        CanonicalFrame = frame;
        SubmissionResolution = submissionResolution;
        CopyCanonical(views, ref _canonicalViews, ref _canonicalViewCount);
        CopyCanonical(passes, ref _canonicalPasses, ref _canonicalPassCount);
        CopyCanonical(dirtyOwnerRanges, ref _canonicalDirtyOwnerRanges, ref _canonicalDirtyOwnerRangeCount);
        CopyCanonical(diagnosticReadbackRequests, ref _canonicalDiagnosticReadbackRequests, ref _canonicalDiagnosticReadbackRequestCount);
        CopyCanonical(cpuVisibleDraws, ref _canonicalCpuVisibleDraws, ref _canonicalCpuVisibleDrawCount);
        CopyCanonical(orderedExceptions, ref _canonicalOrderedExceptions, ref _canonicalOrderedExceptionCount);
        CopyCanonical(templateProjectionDeltas, ref _canonicalTemplateProjectionDeltas, ref _canonicalTemplateProjectionDeltaCount);
    }

    /// <summary>
    /// Resets canonical package references without shrinking reusable backing
    /// arrays. <see cref="MeshSelections"/> intentionally remains a legacy
    /// parity sidecar until the backend consumes canonical projections directly.
    /// </summary>
    internal void ResetCanonical()
    {
        _canonicalPublicationLease?.Dispose();
        _canonicalPublicationLease = null;
        CanonicalScenePublication = default;
        CanonicalFrame = default;
        SubmissionResolution = default;
        ClearCanonical(ref _canonicalViews, ref _canonicalViewCount);
        ClearCanonical(ref _canonicalPasses, ref _canonicalPassCount);
        ClearCanonical(ref _canonicalDirtyOwnerRanges, ref _canonicalDirtyOwnerRangeCount);
        ClearCanonical(ref _canonicalDiagnosticReadbackRequests, ref _canonicalDiagnosticReadbackRequestCount);
        ClearCanonical(ref _canonicalCpuVisibleDraws, ref _canonicalCpuVisibleDrawCount);
        ClearCanonical(ref _canonicalOrderedExceptions, ref _canonicalOrderedExceptionCount);
        ClearCanonical(ref _canonicalTemplateProjectionDeltas, ref _canonicalTemplateProjectionDeltaCount);
    }

    /// <summary>
    /// Projects one canonical resident publication into this frame package. The
    /// resident pass set and template deltas come from the scene publication;
    /// only compact CPU-visible rows and ordered exceptions use visible selections.
    /// </summary>
    internal void PrepareCanonicalFromScene(
        GPUScene? scene,
        XRCamera? camera,
        int viewportWidth,
        int viewportHeight)
    {
        // A late package preparation only has command membership. It must not
        // discard the already captured canonical publication and its lease.
        if (scene is null || scene.AdvancedPublicationRejected ||
            scene.AdvancedPublicationFaulted ||
            !scene.AdvancedScenePublication.IsValid)
            return;

        ResetCanonical();

        AdvancedGpuScenePublicationReference publication = scene.AdvancedScenePublication;
        if (!scene.AdvancedSharedDatabase.TryAcquirePublicationLease(
            publication,
            EAdvancedGpuScenePublicationPinKind.Package,
            out AdvancedGpuScenePublicationLease lease))
        {
            return;
        }
        _canonicalPublicationLease = lease;

        if (!scene.AdvancedSharedDatabase.TryGetPublicationSnapshot(
                publication,
                out AdvancedGpuScenePublicationSnapshot snapshot))
        {
            ResetCanonical();
            return;
        }

        AdvancedGpuScenePublication identity = publication.Publication;
        CanonicalScenePublication = new BackendReadyCanonicalScenePublication(
            identity.DatabaseEpoch,
            identity.Sequence,
            identity.FrameGeneration,
            identity.TopologyGeneration,
            identity.ContentGeneration,
            identity.LookupGeneration);
        CanonicalFrame = new BackendReadyCanonicalFrameRecord(
            identity.FrameGeneration,
            identity.Sequence,
            checked((ulong)Math.Max(0L, SourceRevision)),
            DependencySignature);
        SubmissionResolution = CreateCanonicalSubmissionResolution();

        if (camera is not null)
        {
            EnsureCanonicalCapacity(ref _canonicalViews, 1);
            _canonicalViews[0] = new BackendReadyCanonicalViewRecord(
                0u,
                camera.Transform.InverseWorldMatrix,
                camera.ProjectionMatrix,
                viewportWidth,
                viewportHeight,
                identity.FrameGeneration);
            _canonicalViewCount = 1;
        }

        PopulateCanonicalResidentPasses(scene, in identity);
        PopulateCanonicalDirtyRanges(scene, in identity);
        PopulateCanonicalVisibleRecords(scene);
        PopulateCanonicalTemplateDeltas(snapshot, in identity);
        PopulateCanonicalDiagnosticReadbackRequests();

        // Vulkan acquires a bounded queue-side GPU pin while each render request
        // is enqueued. Once projection has completed, the package pin must be
        // released so producer-side capacity growth remains legal.
        _canonicalPublicationLease?.Dispose();
        _canonicalPublicationLease = null;
    }

    private void PopulateCanonicalResidentPasses(
        GPUScene scene,
        in AdvancedGpuScenePublication identity)
    {
        BackendReadySubmissionResolution submissionResolution = SubmissionResolution;
        EBackendReadyPassDiagnosticFlags diagnostics =
            GetPassDiagnostics(in submissionResolution);
        ReadOnlySpan<LegacyCanonicalDrawMapping> mappings =
            scene.LegacyCanonicalDrawMappings;
        EnsureCanonicalCapacity(ref _canonicalPasses, mappings.Length);
        int count = 0;
        for (int mappingIndex = 0; mappingIndex < mappings.Length; ++mappingIndex)
        {
            LegacyCanonicalDrawMapping mapping = mappings[mappingIndex];
            int pass = checked((int)mapping.LegacyRenderPass);
            int existing = -1;
            for (int passIndex = 0; passIndex < count; ++passIndex)
                if (_canonicalPasses[passIndex].PassIndex == pass)
                {
                    existing = passIndex;
                    break;
                }

            if (existing >= 0)
            {
                BackendReadyCanonicalPassRecord record = _canonicalPasses[existing];
                _canonicalPasses[existing] = record with
                {
                DependencySignature = MixCanonical(
                        record.DependencySignature,
                        mapping.DependencySignature),
                    MembershipSignature = MixCanonical(
                        record.MembershipSignature,
                        PackHandle(mapping.Draw)),
                };
                continue;
            }

            _canonicalPasses[count++] = new BackendReadyCanonicalPassRecord(
                pass,
                identity.TopologyGeneration,
                mapping.DependencySignature,
                MixCanonical(14695981039346656037ul, PackHandle(mapping.Draw)),
                submissionResolution,
                diagnostics);
        }
        _canonicalPassCount = count;
    }

    private void PopulateCanonicalDirtyRanges(
        GPUScene scene,
        in AdvancedGpuScenePublication identity)
    {
        ReadOnlySpan<AdvancedGpuDirtyOwnerRange> ranges =
            scene.AdvancedDirtyOwnerRanges;
        EnsureCanonicalCapacity(ref _canonicalDirtyOwnerRanges, ranges.Length);
        for (int index = 0; index < ranges.Length; ++index)
        {
            AdvancedGpuDirtyOwnerRange range = ranges[index];
            _canonicalDirtyOwnerRanges[index] =
                new BackendReadyCanonicalDirtyOwnerRange(
                    MapOwner(range.Owner),
                    range.Range,
                    range.ContentGeneration);
        }
        _canonicalDirtyOwnerRangeCount = ranges.Length;
    }

    private static EBackendReadyCanonicalOwner MapOwner(EAdvancedGpuRecordOwner owner)
        => owner switch
        {
            EAdvancedGpuRecordOwner.Draw => EBackendReadyCanonicalOwner.Draw,
            EAdvancedGpuRecordOwner.Instance => EBackendReadyCanonicalOwner.Instance,
            EAdvancedGpuRecordOwner.Transform => EBackendReadyCanonicalOwner.Transform,
            EAdvancedGpuRecordOwner.Deformation => EBackendReadyCanonicalOwner.Deformation,
            EAdvancedGpuRecordOwner.RenderState => EBackendReadyCanonicalOwner.RenderState,
            EAdvancedGpuRecordOwner.Material => EBackendReadyCanonicalOwner.Material,
            EAdvancedGpuRecordOwner.Texture => EBackendReadyCanonicalOwner.Texture,
            EAdvancedGpuRecordOwner.Sampler => EBackendReadyCanonicalOwner.Sampler,
            EAdvancedGpuRecordOwner.Geometry => EBackendReadyCanonicalOwner.Geometry,
            EAdvancedGpuRecordOwner.EditorIdentity => EBackendReadyCanonicalOwner.EditorIdentity,
            _ => EBackendReadyCanonicalOwner.None,
        };

    private void PopulateCanonicalVisibleRecords(GPUScene scene)
    {
        int visibleCount = 0;
        int exceptionCount = 0;
        for (int selectionIndex = 0; selectionIndex < _meshSelectionCount; ++selectionIndex)
        {
            BackendReadyMeshSelection selection = _meshSelections[selectionIndex];
            int primitiveCount = selection.Mesh?.Submeshes.Count ?? 0;
            if (primitiveCount == 0)
                primitiveCount = 1;

            for (int primitiveIndex = 0; primitiveIndex < primitiveCount; ++primitiveIndex)
            {
                ulong orderKey = ((ulong)(uint)selectionIndex << 32) | (uint)primitiveIndex;
                if (!scene.TryGetCanonicalDraw(selection.Command, primitiveIndex, out AdvancedGpuHandle draw))
                {
                    scene.TryGetCanonicalCompatibilityReason(
                        selection.Command,
                        primitiveIndex,
                        out EAdvancedCanonicalCompatibilityReason compatibilityReason);
                    EnsureCanonicalCapacity(ref _canonicalOrderedExceptions, exceptionCount + 1);
                    _canonicalOrderedExceptions[exceptionCount++] =
                        new BackendReadyOrderedExceptionRecord(
                            AdvancedGpuHandle.Invalid,
                            0u,
                            selection.RenderPass,
                            orderKey,
                            1u,
                            compatibilityReason);
                    continue;
                }

                if (selection.ForceCpuRendering || selection.ExcludeFromGpuIndirect)
                {
                    EnsureCanonicalCapacity(ref _canonicalOrderedExceptions, exceptionCount + 1);
                    _canonicalOrderedExceptions[exceptionCount++] =
                        new BackendReadyOrderedExceptionRecord(
                            draw,
                            0u,
                            selection.RenderPass,
                            orderKey,
                            selection.ForceCpuRendering ? 2u : 4u);
                }

                EnsureCanonicalCapacity(ref _canonicalCpuVisibleDraws, visibleCount + 1);
                _canonicalCpuVisibleDraws[visibleCount++] =
                    new BackendReadyCpuVisibleDrawRecord(
                        draw,
                        0u,
                        selection.RenderPass,
                        Math.Max(1u, selection.Instances),
                        orderKey);
            }
        }
        _canonicalCpuVisibleDrawCount = visibleCount;
        _canonicalOrderedExceptionCount = exceptionCount;
    }

    private void PopulateCanonicalTemplateDeltas(
        AdvancedGpuScenePublicationSnapshot snapshot,
        in AdvancedGpuScenePublication identity)
    {
        int count = 0;
        AppendTemplateDeltas(snapshot.Draws, EBackendReadyCanonicalOwner.Draw, EBackendTemplateMutationDomain.LayoutTopology, identity.Sequence, ref count);
        AppendTemplateDeltas(snapshot.Instances, EBackendReadyCanonicalOwner.Instance, EBackendTemplateMutationDomain.DataContent, identity.Sequence, ref count);
        AppendTemplateDeltas(snapshot.Transforms, EBackendReadyCanonicalOwner.Transform, EBackendTemplateMutationDomain.DataContent, identity.Sequence, ref count);
        AppendTemplateDeltas(snapshot.Deformations, EBackendReadyCanonicalOwner.Deformation, EBackendTemplateMutationDomain.DataContent, identity.Sequence, ref count);
        AppendTemplateDeltas(snapshot.RenderStates, EBackendReadyCanonicalOwner.RenderState, EBackendTemplateMutationDomain.LayoutTopology, identity.Sequence, ref count);
        AppendTemplateDeltas(snapshot.EditorIdentities, EBackendReadyCanonicalOwner.EditorIdentity, EBackendTemplateMutationDomain.DataContent, identity.Sequence, ref count);
        AppendTemplateDeltas(snapshot.Geometry, EBackendReadyCanonicalOwner.Geometry, EBackendTemplateMutationDomain.LayoutTopology, identity.Sequence, ref count);
        AppendTemplateDeltas(snapshot.Materials, EBackendReadyCanonicalOwner.Material, EBackendTemplateMutationDomain.ResourceTable, identity.Sequence, ref count);
        AppendTemplateDeltas(snapshot.Kernels, EBackendReadyCanonicalOwner.ShadingKernel, EBackendTemplateMutationDomain.LayoutTopology, identity.Sequence, ref count);
        AppendTemplateDeltas(snapshot.Layouts, EBackendReadyCanonicalOwner.MaterialLayout, EBackendTemplateMutationDomain.LayoutTopology, identity.Sequence, ref count);
        AppendTemplateDeltas(snapshot.Textures, EBackendReadyCanonicalOwner.Texture, EBackendTemplateMutationDomain.ResourceTable, identity.Sequence, ref count);
        AppendTemplateDeltas(snapshot.Samplers, EBackendReadyCanonicalOwner.Sampler, EBackendTemplateMutationDomain.ResourceTable, identity.Sequence, ref count);
        _canonicalTemplateProjectionDeltaCount = count;
    }

    private void AppendTemplateDeltas<T>(
        AdvancedGpuRecordTablePublicationSnapshot<T> snapshot,
        EBackendReadyCanonicalOwner owner,
        EBackendTemplateMutationDomain domain,
        ulong sequence,
        ref int count)
        where T : unmanaged
    {
        if (snapshot.Sequence != sequence)
            throw new InvalidOperationException("The publication snapshot sequence does not match its retained reference.");

        ReadOnlySpan<AdvancedGpuRecordPublicationDelta> deltas = snapshot.Deltas;
        for (int index = 0; index < deltas.Length; ++index)
        {
            AdvancedGpuRecordPublicationDelta delta = deltas[index];
            EBackendTemplateProjectionDeltaKind kind = delta.Change switch
            {
                EAdvancedGpuRecordPublicationChange.Added => EBackendTemplateProjectionDeltaKind.Add,
                EAdvancedGpuRecordPublicationChange.Updated => EBackendTemplateProjectionDeltaKind.Update,
                EAdvancedGpuRecordPublicationChange.Tombstoned => EBackendTemplateProjectionDeltaKind.Tombstone,
                EAdvancedGpuRecordPublicationChange.DenseRemapped => EBackendTemplateProjectionDeltaKind.DenseRemap,
                _ => EBackendTemplateProjectionDeltaKind.None,
            };
            if (kind == EBackendTemplateProjectionDeltaKind.None)
                continue;

            EnsureCanonicalCapacity(ref _canonicalTemplateProjectionDeltas, count + 1);
            _canonicalTemplateProjectionDeltas[count++] = new BackendTemplateProjectionDelta(
                kind,
                kind == EBackendTemplateProjectionDeltaKind.DenseRemap
                    ? EBackendTemplateMutationDomain.ResourceTable
                    : MapMutationDomain(delta.Domain),
                owner,
                delta.Handle,
                AdvancedGpuHandle.Invalid,
                sequence,
                delta.PreviousDenseIndex,
                delta.CurrentDenseIndex);
        }

        ReadOnlySpan<AdvancedGpuHandleRemap> remaps = snapshot.Remaps;
        for (int index = 0; index < remaps.Length; ++index)
        {
            AdvancedGpuHandleRemap remap = remaps[index];
            EnsureCanonicalCapacity(ref _canonicalTemplateProjectionDeltas, count + 1);
            _canonicalTemplateProjectionDeltas[count++] = new BackendTemplateProjectionDelta(
                EBackendTemplateProjectionDeltaKind.DenseRemap,
                EBackendTemplateMutationDomain.ResourceTable,
                owner,
                remap.Handle,
                AdvancedGpuHandle.Invalid,
                sequence,
                remap.PreviousDenseIndex,
                remap.CurrentDenseIndex);
        }
    }

    private static EBackendTemplateMutationDomain MapMutationDomain(
        EAdvancedGpuMutationDomain domain)
        => domain switch
        {
            EAdvancedGpuMutationDomain.Content => EBackendTemplateMutationDomain.DataContent,
            EAdvancedGpuMutationDomain.ResourceBinding => EBackendTemplateMutationDomain.ResourceTable,
            EAdvancedGpuMutationDomain.LayoutTopology => EBackendTemplateMutationDomain.LayoutTopology,
            EAdvancedGpuMutationDomain.RecordingTopology => EBackendTemplateMutationDomain.Recording,
            _ => EBackendTemplateMutationDomain.LayoutTopology,
        };

    private void PopulateCanonicalDiagnosticReadbackRequests()
    {
        bool diagnostic = SubmissionResolution.Resolved is
            EMeshSubmissionStrategy.GpuIndirectInstrumented or
            EMeshSubmissionStrategy.GpuMeshletInstrumented;
        if (!diagnostic)
            return;

        for (int passIndex = 0; passIndex < _canonicalPassCount; ++passIndex)
        {
            EnsureCanonicalCapacity(ref _canonicalDiagnosticReadbackRequests, passIndex + 1);
            _canonicalDiagnosticReadbackRequests[passIndex] =
                new BackendReadyDiagnosticReadbackRequest(
                    EBackendReadyDiagnosticReadbackKind.SubmissionValidation,
                    0u,
                    _canonicalPasses[passIndex].PassIndex,
                    4096u);
        }
        _canonicalDiagnosticReadbackRequestCount = _canonicalPassCount;
    }

    private static BackendReadySubmissionResolution CreateCanonicalSubmissionResolution()
    {
        EMeshSubmissionStrategy requested =
            RuntimeEngine.Rendering.ResolveRequestedMeshSubmissionStrategy();
        EMeshSubmissionStrategy resolved =
            RuntimeEngine.Rendering.LastResolvedMeshSubmissionStrategy;
        return new BackendReadySubmissionResolution(
            requested,
            resolved,
            requested != resolved,
            MixCanonical((uint)requested, (uint)resolved),
            RuntimeEngine.Rendering.LastResolvedRendererBackend,
            RuntimeEngine.Rendering.LastResolvedMeshShaderDialect,
            RuntimeEngine.Rendering.LastResolvedSupportsMeshletDispatch);
    }

    private static EBackendReadyPassDiagnosticFlags GetPassDiagnostics(
        in BackendReadySubmissionResolution resolution)
    {
        EBackendReadyPassDiagnosticFlags diagnostics = EBackendReadyPassDiagnosticFlags.None;
        if (resolution.Downgraded)
            diagnostics |= EBackendReadyPassDiagnosticFlags.StrategyDowngraded;
        if (resolution.Resolved is EMeshSubmissionStrategy.GpuIndirectInstrumented or EMeshSubmissionStrategy.GpuMeshletInstrumented)
            diagnostics |= EBackendReadyPassDiagnosticFlags.InstrumentedReadbackRequested;
        if (resolution.Requested.IsAnyMeshletStrategy() && !resolution.SupportsMeshletDispatch)
            diagnostics |= EBackendReadyPassDiagnosticFlags.MeshletDispatchUnavailable;
        return diagnostics;
    }

    private static void EnsureCanonicalCapacity<T>(ref T[] values, int required)
    {
        if (values.Length < required)
            Array.Resize(ref values, GrowCapacity(values.Length, required));
    }

    private static ulong MixCanonical(ulong hash, ulong value)
    {
        hash ^= value;
        hash *= 1099511628211ul;
        return hash;
    }

    private static ulong PackHandle(in AdvancedGpuHandle handle)
        => ((ulong)handle.Generation << 32) | handle.Index;

    private static void CopyCanonical<T>(ReadOnlySpan<T> source, ref T[] destination, ref int count)
    {
        int previousCount = count;
        if (destination.Length < source.Length)
            Array.Resize(ref destination, GrowCapacity(destination.Length, source.Length));

        source.CopyTo(destination);
        count = source.Length;
        if (previousCount > count)
            Array.Clear(destination, count, previousCount - count);
    }

    private static void ClearCanonical<T>(ref T[] values, ref int count)
    {
        if (count > 0)
            Array.Clear(values, 0, count);
        count = 0;
    }
}
