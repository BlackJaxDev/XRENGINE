using XREngine.Data.Rendering;

using XREngine.Rendering.Diagnostics;
using System.Numerics;

namespace XREngine.Rendering.Commands;

public sealed partial class BackendReadyFramePackage
{
    private BackendReadyCanonicalViewRecord[] _canonicalViews = [];
    private BackendReadyCanonicalPassRecord[] _canonicalPasses = [];
    private BackendReadyCanonicalDirtyOwnerRange[] _canonicalDirtyOwnerRanges = [];
    private BackendReadyDiagnosticReadbackRequest[] _canonicalDiagnosticReadbackRequests = [];
    private GpuDiagnosticReadbackPlan[] _canonicalDiagnosticReadbackPlans = [];
    private BackendReadyCpuVisibleDrawRecord[] _canonicalCpuVisibleDraws = [];
    private BackendReadyOrderedExceptionRecord[] _canonicalOrderedExceptions = [];
    private BackendTemplateProjectionDelta[] _canonicalTemplateProjectionDeltas = [];
    private AdvancedGlobalPassPublicationCoverage[] _canonicalGlobalPassCoverage = [];
    private AdvancedGpuScenePublicationLease? _canonicalPublicationLease;
    private int _canonicalViewCount;
    private int _canonicalPassCount;
    private int _canonicalDirtyOwnerRangeCount;
    private int _canonicalDiagnosticReadbackRequestCount;
    private int _canonicalCpuVisibleDrawCount;
    private int _canonicalOrderedExceptionCount;
    private int _canonicalTemplateProjectionDeltaCount;
    private int _canonicalDiagnosticReadbackPlanCount;
    private int _canonicalGlobalPassCoverageCount;

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
    /// <summary>
    /// Immutable diagnostic-only sidecar attachments sealed while the package is
    /// prepared. An empty span carries no Vulkan reservation, copy, or decoder work.
    /// </summary>
    public ReadOnlySpan<GpuDiagnosticReadbackPlan> DiagnosticReadbackPlans
        => _canonicalDiagnosticReadbackPlans.AsSpan(0, _canonicalDiagnosticReadbackPlanCount);
    public ReadOnlySpan<BackendReadyCpuVisibleDrawRecord> CpuVisibleDraws => _canonicalCpuVisibleDraws.AsSpan(0, _canonicalCpuVisibleDrawCount);
    public ReadOnlySpan<BackendReadyOrderedExceptionRecord> OrderedExceptions => _canonicalOrderedExceptions.AsSpan(0, _canonicalOrderedExceptionCount);
    public ReadOnlySpan<BackendTemplateProjectionDelta> TemplateProjectionDeltas => _canonicalTemplateProjectionDeltas.AsSpan(0, _canonicalTemplateProjectionDeltaCount);
    /// <summary>
    /// Per-pass immutable global shadow/probe coverage derived from the same
    /// retained publication as the canonical pass records.
    /// </summary>
    public ReadOnlySpan<AdvancedGlobalPassPublicationCoverage> GlobalPassCoverage
        => _canonicalGlobalPassCoverage.AsSpan(0, _canonicalGlobalPassCoverageCount);

    /// <summary>
    /// Number of immutable advanced submission rows retained by this package's
    /// publication. A package without a retained canonical publication reports
    /// zero rather than recreating a legacy managed selection projection.
    /// </summary>
    public int CanonicalSubmissionCount
        => TryGetCanonicalPublicationSnapshot(out AdvancedGpuScenePublicationSnapshot snapshot)
            ? snapshot.Submission.Count
            : 0;

    /// <summary>
    /// Looks up a canonical submission row from the immutable publication held
    /// by this package. This is the only normal-frame ownership query exposed
    /// to renderer-neutral consumers; it intentionally does not reconstruct a
    /// managed mesh-selection projection.
    /// </summary>
    public bool TryGetCanonicalSubmission(
        int renderPass,
        uint stableQueryKey,
        out AdvancedDrawSubmissionRecord submission)
    {
        if (TryGetCanonicalPublicationSnapshot(out AdvancedGpuScenePublicationSnapshot snapshot))
        {
            ReadOnlySpan<AdvancedDrawSubmissionRecord> submissions = snapshot.Submission.Records;
            for (int index = 0; index < submissions.Length; ++index)
            {
                AdvancedDrawSubmissionRecord candidate = submissions[index];
                if (candidate.PassIndex == unchecked((uint)renderPass) &&
                    candidate.StableQueryKey == stableQueryKey)
                {
                    submission = candidate;
                    return true;
                }
            }
        }

        submission = default;
        return false;
    }

    /// <summary>
    /// Returns whether a canonical row is owned by advanced GPU submission.
    /// Missing canonical data is deliberately fail-visible to CPU callers.
    /// </summary>
    public bool IsCanonicalGpuOwned(int renderPass, uint stableQueryKey)
        => TryGetCanonicalSubmission(renderPass, stableQueryKey, out AdvancedDrawSubmissionRecord submission) &&
           submission.CompatibilityReason == EAdvancedCanonicalCompatibilityReason.None &&
           (submission.Flags & (uint)GPUIndirectRenderFlags.CpuFallbackOnly) == 0u;

    /// <summary>
    /// Exposes the exact immutable publication association already retained by
    /// this package. Backends use it only to acquire their own completion-owned
    /// GPU lease; ownership never transfers out of the package here.
    /// </summary>
    internal bool TryGetCanonicalPublication(
        out AdvancedSharedGpuSceneDatabase database,
        out AdvancedGpuScenePublicationReference publication)
    {
        if (_canonicalPublicationLease is { IsValid: true } lease &&
            lease.Database is { } retainedDatabase)
        {
            database = retainedDatabase;
            publication = lease.Reference;
            return publication.IsValid;
        }

        database = null!;
        publication = default;
        return false;
    }

    /// <summary>
    /// Gets the immutable scene image retained by this package.  The caller
    /// may consume it only while the package remains current; Vulkan takes its
    /// own completion-owned lease before the prepared frame is frozen.
    /// </summary>
    internal bool TryGetCanonicalPublicationSnapshot(
        out AdvancedGpuScenePublicationSnapshot snapshot)
    {
        if (TryGetCanonicalPublication(
                out AdvancedSharedGpuSceneDatabase database,
                out AdvancedGpuScenePublicationReference publication) &&
            database.TryGetPublicationSnapshot(publication, out snapshot))
            return true;

        snapshot = null!;
        return false;
    }

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
        BuildDiagnosticReadbackPlan();
        CopyCanonical(cpuVisibleDraws, ref _canonicalCpuVisibleDraws, ref _canonicalCpuVisibleDrawCount);
        CopyCanonical(orderedExceptions, ref _canonicalOrderedExceptions, ref _canonicalOrderedExceptionCount);
        CopyCanonical(templateProjectionDeltas, ref _canonicalTemplateProjectionDeltas, ref _canonicalTemplateProjectionDeltaCount);
        ClearCanonical(ref _canonicalGlobalPassCoverage, ref _canonicalGlobalPassCoverageCount);
    }

    /// <summary>
    /// Resets canonical package references without shrinking reusable backing
    /// arrays. Normal-frame submission state is retained solely by the
    /// publication snapshot and the package's canonical projections.
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
        ClearCanonical(ref _canonicalDiagnosticReadbackPlans, ref _canonicalDiagnosticReadbackPlanCount);
        ClearCanonical(ref _canonicalCpuVisibleDraws, ref _canonicalCpuVisibleDrawCount);
        ClearCanonical(ref _canonicalOrderedExceptions, ref _canonicalOrderedExceptionCount);
        ClearCanonical(ref _canonicalTemplateProjectionDeltas, ref _canonicalTemplateProjectionDeltaCount);
        ClearCanonical(ref _canonicalGlobalPassCoverage, ref _canonicalGlobalPassCoverageCount);
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
            Identity.FrameId,
            identity.FrameGeneration,
            checked((ulong)Math.Max(0L, SourceRevision)),
            DependencySignature);
        SubmissionResolution = CreateCanonicalSubmissionResolution();

        if (RenderFrameViewSetPublication.TryGetLatest(out RenderFrameViewSet viewSet))
        {
            EnsureCanonicalCapacity(ref _canonicalViews, viewSet.ViewCount);
            for (int viewIndex = 0; viewIndex < viewSet.ViewCount; ++viewIndex)
                _canonicalViews[viewIndex] = CreateCanonicalViewRecord(viewSet.GetView(viewIndex), identity.FrameGeneration);
            _canonicalViewCount = viewSet.ViewCount;
        }
        else if (camera is not null)
        {
            EnsureCanonicalCapacity(ref _canonicalViews, 1);
            _canonicalViews[0] = CreateCanonicalViewRecord(camera, viewportWidth, viewportHeight, identity.FrameGeneration);
            _canonicalViewCount = 1;
        }

        PopulateCanonicalResidentPasses(snapshot, in identity);
        PopulateCanonicalGlobalPassCoverage(snapshot, in identity);
        PopulateCanonicalDirtyRanges(snapshot, in identity);
        PopulateCanonicalVisibleRecords(snapshot);
        PopulateCanonicalTemplateDeltas(snapshot, in identity);
        PopulateCanonicalDiagnosticReadbackRequests();
        BuildDiagnosticReadbackPlan();

        // Keep the package lease through prepared-frame capture.  The native
        // consumer obtains its own completion-owned lease there; releasing this
        // one early made the supposedly canonical stream fall back to mutable
        // GPUScene state between package production and primary recording.
    }

    private static BackendReadyCanonicalViewRecord CreateCanonicalViewRecord(in RenderFrameViewDescriptor source, ulong generation)
    {
        Matrix4x4 projectionUnjittered = source.ProjectionMatrixUnjittered == default ? source.ProjectionMatrix : source.ProjectionMatrixUnjittered;
        Matrix4x4 viewProjectionJittered = source.ViewProjectionMatrix;
        Matrix4x4 viewProjectionUnjittered = source.ViewMatrix * projectionUnjittered;
        Matrix4x4 previousViewProjection = source.PreviousViewProjectionMatrix == default ? viewProjectionJittered : source.PreviousViewProjectionMatrix;
        GetViewMask(source.ViewId, out uint viewMaskLo, out uint viewMaskHi);
        return CreateCanonicalViewRecord(
            source.ViewId, source.ViewMatrix, source.ProjectionMatrix, projectionUnjittered,
            viewProjectionJittered, viewProjectionUnjittered, previousViewProjection,
            source.ViewRect.Width, source.ViewRect.Height, source.CameraPositionAndNear,
            source.CameraForwardAndFar, new Vector4(
                source.CurrentJitter.X, source.CurrentJitter.Y,
                source.PreviousJitter.X, source.PreviousJitter.Y),
            source.OutputLayer, CreateAdvancedViewFlags(source), source.EffectiveHistoryKey,
            viewMaskLo, viewMaskHi, generation);
    }

    private static BackendReadyCanonicalViewRecord CreateCanonicalViewRecord(
        XRCamera camera, int viewportWidth, int viewportHeight, ulong generation)
    {
        Matrix4x4 view = camera.Transform.InverseRenderMatrix;
        Matrix4x4 projection = camera.ProjectionMatrix;
        Matrix4x4 projectionUnjittered = camera.ProjectionMatrixUnjittered;
        EAdvancedViewRecordFlags flags = EAdvancedViewRecordFlags.DepthZeroToOne;
        if (camera.IsReversedDepth)
            flags |= EAdvancedViewRecordFlags.ReversedDepth;
        return CreateCanonicalViewRecord(
            0u, view, projection, projectionUnjittered, view * projection,
            view * projectionUnjittered, view * projection,
            checked((uint)Math.Max(viewportWidth, 1)), checked((uint)Math.Max(viewportHeight, 1)),
            new Vector4(camera.Transform.RenderTranslation, camera.NearZ),
            new Vector4(camera.Transform.RenderForward, camera.FarZ),
            new Vector4(camera.ProjectionJitter.X, camera.ProjectionJitter.Y, 0.0f, 0.0f), 0u, flags,
            RenderFrameViewSetCapture.MonoHistoryKey, 1u, 0u, generation);
    }

    private static BackendReadyCanonicalViewRecord CreateCanonicalViewRecord(
        uint viewId, in Matrix4x4 view, in Matrix4x4 projection, in Matrix4x4 projectionUnjittered,
        in Matrix4x4 viewProjectionJittered, in Matrix4x4 viewProjectionUnjittered,
        in Matrix4x4 previousViewProjection, uint viewportWidth, uint viewportHeight,
        in Vector4 cameraPositionAndNear, in Vector4 cameraForwardAndFar,
        in Vector4 currentAndPreviousJitter, uint outputLayer, EAdvancedViewRecordFlags flags,
        ulong historyKey, uint viewMaskLo, uint viewMaskHi, ulong generation)
    {
        ExtractFrustumPlanes(viewProjectionUnjittered, (flags & EAdvancedViewRecordFlags.DepthZeroToOne) != 0,
            out Vector4 left, out Vector4 right, out Vector4 bottom, out Vector4 top, out Vector4 near, out Vector4 far);
        float nearZ = cameraPositionAndNear.W;
        float farZ = cameraForwardAndFar.W;
        float range = farZ - nearZ;
        return new BackendReadyCanonicalViewRecord(viewId, view, projection, checked((int)viewportWidth), checked((int)viewportHeight), generation)
        {
            ProjectionUnjittered = projectionUnjittered,
            ViewProjectionJittered = viewProjectionJittered,
            ViewProjectionUnjittered = viewProjectionUnjittered,
            PreviousViewProjectionJittered = previousViewProjection,
            PreviousViewProjectionUnjittered = previousViewProjection,
            FrustumPlane0 = left,
            FrustumPlane1 = right,
            FrustumPlane2 = bottom,
            FrustumPlane3 = top,
            FrustumPlane4 = near,
            FrustumPlane5 = far,
            CameraPositionAndNear = cameraPositionAndNear,
            CameraForwardAndFar = cameraForwardAndFar,
            CurrentAndPreviousJitter = currentAndPreviousJitter,
            DepthParams = new Vector4(nearZ, farZ, MathF.Abs(range) > float.Epsilon ? 1.0f / range : 0.0f,
                (flags & EAdvancedViewRecordFlags.ReversedDepth) != 0 ? 1.0f : 0.0f),
            OutputLayer = outputLayer,
            Flags = flags,
            HistoryKey = historyKey,
            ViewMaskLo = viewMaskLo,
            ViewMaskHi = viewMaskHi,
        };
    }

    private static EAdvancedViewRecordFlags CreateAdvancedViewFlags(in RenderFrameViewDescriptor source)
    {
        EAdvancedViewRecordFlags flags = source.DepthZeroToOne ? EAdvancedViewRecordFlags.DepthZeroToOne : EAdvancedViewRecordFlags.None;
        if (source.ReversedDepth)
            flags |= EAdvancedViewRecordFlags.ReversedDepth;
        if (source.IsLeftEyeFamily)
            flags |= EAdvancedViewRecordFlags.StereoLeft;
        if (source.IsRightEyeFamily)
            flags |= EAdvancedViewRecordFlags.StereoRight;
        if (source.Foveation.IsEnabled)
            flags |= EAdvancedViewRecordFlags.Foveated;
        if (source.Kind is EVrOutputViewKind.CyclopeanDesktop)
            flags |= EAdvancedViewRecordFlags.Mirror;
        return flags;
    }

    private static void GetViewMask(uint viewId, out uint low, out uint high)
    {
        low = viewId < 32u ? 1u << checked((int)viewId) : 0u;
        high = viewId is >= 32u and < 64u ? 1u << checked((int)(viewId - 32u)) : 0u;
    }

    private static void ExtractFrustumPlanes(
        in Matrix4x4 matrix, bool depthZeroToOne, out Vector4 left, out Vector4 right,
        out Vector4 bottom, out Vector4 top, out Vector4 near, out Vector4 far)
    {
        Vector4 column1 = new(matrix.M11, matrix.M21, matrix.M31, matrix.M41);
        Vector4 column2 = new(matrix.M12, matrix.M22, matrix.M32, matrix.M42);
        Vector4 column3 = new(matrix.M13, matrix.M23, matrix.M33, matrix.M43);
        Vector4 column4 = new(matrix.M14, matrix.M24, matrix.M34, matrix.M44);
        left = NormalizePlane(column4 + column1);
        right = NormalizePlane(column4 - column1);
        bottom = NormalizePlane(column4 + column2);
        top = NormalizePlane(column4 - column2);
        near = NormalizePlane(depthZeroToOne ? column3 : column4 + column3);
        far = NormalizePlane(column4 - column3);
    }

    private static Vector4 NormalizePlane(in Vector4 plane)
    {
        float length = new Vector3(plane.X, plane.Y, plane.Z).Length();
        return length > float.Epsilon ? plane / length : Vector4.Zero;
    }

    private void PopulateCanonicalResidentPasses(
        AdvancedGpuScenePublicationSnapshot snapshot,
        in AdvancedGpuScenePublication identity)
    {
        BackendReadySubmissionResolution submissionResolution = SubmissionResolution;
        EBackendReadyPassDiagnosticFlags diagnostics =
            GetPassDiagnostics(in submissionResolution);
        ReadOnlySpan<AdvancedDrawSubmissionRecord> submissions = snapshot.Submission.Records;
        EnsureCanonicalCapacity(ref _canonicalPasses, submissions.Length);
        int count = 0;
        for (int mappingIndex = 0; mappingIndex < submissions.Length; ++mappingIndex)
        {
            AdvancedDrawSubmissionRecord mapping = submissions[mappingIndex];
            int pass = checked((int)mapping.PassIndex);
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
        AdvancedGpuScenePublicationSnapshot snapshot,
        in AdvancedGpuScenePublication identity)
    {
        if (snapshot.Mutations.Sequence != identity.Sequence)
        {
            throw new InvalidOperationException(
                $"Canonical mutation snapshot sequence {snapshot.Mutations.Sequence} does not match publication {identity.Sequence}.");
        }

        ReadOnlySpan<AdvancedGpuDirtyOwnerRange> ranges = snapshot.Mutations.Ranges;
        EnsureCanonicalCapacity(ref _canonicalDirtyOwnerRanges, ranges.Length);
        for (int index = 0; index < ranges.Length; ++index)
        {
            AdvancedGpuDirtyOwnerRange range = ranges[index];
            _canonicalDirtyOwnerRanges[index] = new BackendReadyCanonicalDirtyOwnerRange(
                MapOwner(range.Owner), range.Range, range.ContentGeneration);
        }
        if (_canonicalDirtyOwnerRangeCount > ranges.Length)
        {
            Array.Clear(_canonicalDirtyOwnerRanges, ranges.Length,
                _canonicalDirtyOwnerRangeCount - ranges.Length);
        }
        _canonicalDirtyOwnerRangeCount = ranges.Length;
    }

    private void PopulateCanonicalGlobalPassCoverage(
        AdvancedGpuScenePublicationSnapshot snapshot,
        in AdvancedGpuScenePublication identity)
    {
        AdvancedGlobalPassPublicationCoverage source = snapshot.GlobalPassCoverage;
        if (source.Sequence != identity.Sequence)
        {
            throw new InvalidOperationException(
                $"Canonical global coverage sequence {source.Sequence} does not match publication {identity.Sequence}.");
        }

        ReadOnlySpan<AdvancedDrawSubmissionRecord> submissions = snapshot.Submission.Records;
        EnsureCanonicalCapacity(ref _canonicalGlobalPassCoverage, _canonicalPassCount);
        int count = 0;
        for (int passRecordIndex = 0; passRecordIndex < _canonicalPassCount; ++passRecordIndex)
        {
            BackendReadyCanonicalPassRecord pass = _canonicalPasses[passRecordIndex];
            bool usesShadows = false;
            bool usesProbes = false;
            for (int submissionIndex = 0; submissionIndex < submissions.Length; ++submissionIndex)
            {
                AdvancedDrawSubmissionRecord submission = submissions[submissionIndex];
                if (submission.PassIndex != unchecked((uint)pass.PassIndex))
                    continue;

                GPUIndirectRenderFlags flags = (GPUIndirectRenderFlags)submission.Flags;
                usesShadows |= (flags & (GPUIndirectRenderFlags.CastShadow | GPUIndirectRenderFlags.ReceiveShadows)) != 0;
                // Lighting rows with a non-unlit material can sample probes.
                // The producer-stamped submission flag avoids consulting live
                // material or scene state during package preparation.
                usesProbes |= (flags & GPUIndirectRenderFlags.Unlit) == 0;
                if (usesShadows && usesProbes)
                    break;
            }

            AdvancedGlobalPassPublicationCoverage coverage = source.ForPass(
                pass.PassIndex, usesShadows, usesProbes);
            _canonicalGlobalPassCoverage[count++] = coverage;
            _canonicalPasses[passRecordIndex] = pass with
            {
                DependencySignature = MixCanonical(
                    pass.DependencySignature,
                    coverage.UsedOwnerGenerationSignature),
            };
        }

        if (_canonicalGlobalPassCoverageCount > count)
        {
            Array.Clear(_canonicalGlobalPassCoverage, count,
                _canonicalGlobalPassCoverageCount - count);
        }
        _canonicalGlobalPassCoverageCount = count;
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
            EAdvancedGpuRecordOwner.MaterialLayout => EBackendReadyCanonicalOwner.MaterialLayout,
            EAdvancedGpuRecordOwner.ShadingKernel => EBackendReadyCanonicalOwner.ShadingKernel,
            EAdvancedGpuRecordOwner.Light => EBackendReadyCanonicalOwner.Light,
            EAdvancedGpuRecordOwner.Shadow => EBackendReadyCanonicalOwner.Shadow,
            EAdvancedGpuRecordOwner.Probe => EBackendReadyCanonicalOwner.Probe,
            EAdvancedGpuRecordOwner.Environment => EBackendReadyCanonicalOwner.Environment,
            EAdvancedGpuRecordOwner.Decal => EBackendReadyCanonicalOwner.Decal,
            EAdvancedGpuRecordOwner.GiResource => EBackendReadyCanonicalOwner.GiResource,
            _ => EBackendReadyCanonicalOwner.None,
        };

    private void PopulateCanonicalVisibleRecords(
        AdvancedGpuScenePublicationSnapshot snapshot)
    {
        int visibleCount = 0;
        _canonicalOrderedExceptionCount = 0;
        ReadOnlySpan<AdvancedDrawSubmissionRecord> mappings = snapshot.Submission.Records;
        EnsureCanonicalCapacity(ref _canonicalCpuVisibleDraws, mappings.Length);
        EnsureCanonicalCapacity(ref _canonicalOrderedExceptions, mappings.Length);
        for (int mappingIndex = 0; mappingIndex < mappings.Length; ++mappingIndex)
        {
            AdvancedDrawSubmissionRecord mapping = mappings[mappingIndex];
            // The mapping is a producer-time pass projection only.  The draw
            // row itself is resolved from the exact immutable package image;
            // mutable visible selections are diagnostic sidecars and never
            // decide normal Vulkan visibility membership.
            if (!snapshot.Draws.TryGet(mapping.Draw, out AdvancedDrawRecord draw) ||
                draw.Geometry != mapping.Geometry || draw.Material != mapping.Material)
                continue;

            _canonicalCpuVisibleDraws[visibleCount++] =
                new BackendReadyCpuVisibleDrawRecord(
                    mapping.Draw,
                    0u,
                    checked((int)mapping.PassIndex), mapping.InstanceCount,
                    mapping.SourceOrder);
            if (mapping.CompatibilityReason != EAdvancedCanonicalCompatibilityReason.None ||
                (mapping.Flags & (uint)GPUIndirectRenderFlags.CpuFallbackOnly) != 0u)
            {
                _canonicalOrderedExceptions[_canonicalOrderedExceptionCount++] =
                    new BackendReadyOrderedExceptionRecord(mapping.Draw, 0u,
                        checked((int)mapping.PassIndex), mapping.SourceOrder,
                        mapping.Flags, mapping.CompatibilityReason);
            }
        }
        _canonicalCpuVisibleDrawCount = visibleCount;
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
        AppendTemplateDeltas(snapshot.GlobalResources.Lights, EBackendReadyCanonicalOwner.Light, EBackendTemplateMutationDomain.DataContent, identity.Sequence, ref count);
        AppendTemplateDeltas(snapshot.GlobalResources.Shadows, EBackendReadyCanonicalOwner.Shadow, EBackendTemplateMutationDomain.LayoutTopology, identity.Sequence, ref count);
        AppendTemplateDeltas(snapshot.GlobalResources.Probes, EBackendReadyCanonicalOwner.Probe, EBackendTemplateMutationDomain.ResourceTable, identity.Sequence, ref count);
        AppendTemplateDeltas(snapshot.GlobalResources.Environments, EBackendReadyCanonicalOwner.Environment, EBackendTemplateMutationDomain.ResourceTable, identity.Sequence, ref count);
        AppendTemplateDeltas(snapshot.GlobalResources.Decals, EBackendReadyCanonicalOwner.Decal, EBackendTemplateMutationDomain.LayoutTopology, identity.Sequence, ref count);
        AppendTemplateDeltas(snapshot.GlobalResources.GiResources, EBackendReadyCanonicalOwner.GiResource, EBackendTemplateMutationDomain.ResourceTable, identity.Sequence, ref count);
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
                    64u);
        }
        _canonicalDiagnosticReadbackRequestCount = _canonicalPassCount;
    }

    /// <summary>
    /// Seals diagnostic attachment before backend workers consume the package.
    /// This is the only package-level attachment point, which makes a later
    /// zero-readback strategy incapable of acquiring sidecar resources.
    /// </summary>
    private void BuildDiagnosticReadbackPlan()
    {
        if (_canonicalDiagnosticReadbackRequestCount == 0)
        {
            _canonicalDiagnosticReadbackPlanCount = 0;
            return;
        }

        EnsureCanonicalCapacity(
            ref _canonicalDiagnosticReadbackPlans,
            _canonicalDiagnosticReadbackRequestCount);
        for (int index = 0; index < _canonicalDiagnosticReadbackRequestCount; ++index)
        {
            BackendReadyDiagnosticReadbackRequest request =
                _canonicalDiagnosticReadbackRequests[index];
            GpuDiagnosticReadbackPlanNode node = new(
                unchecked((ulong)(uint)request.PassIndex),
                request.ViewId,
                0u,
                request.MaximumByteCount,
                SubmissionResolution.Resolved,
                MapDiagnosticDecoder(request.Kind));
            _canonicalDiagnosticReadbackPlans[index] = GpuDiagnosticReadbackPlan.Create(
                CanonicalFrame.FrameId,
                SubmissionResolution.Resolved,
                in node);
        }
        _canonicalDiagnosticReadbackPlanCount = _canonicalDiagnosticReadbackRequestCount;
    }

    private static EGpuDiagnosticReadbackDecoder MapDiagnosticDecoder(
        EBackendReadyDiagnosticReadbackKind kind)
        => kind switch
        {
            EBackendReadyDiagnosticReadbackKind.IndirectDrawCount => EGpuDiagnosticReadbackDecoder.IndirectDrawCount,
            EBackendReadyDiagnosticReadbackKind.MeshletVisibility => EGpuDiagnosticReadbackDecoder.MeshletVisibility,
            EBackendReadyDiagnosticReadbackKind.SubmissionValidation => EGpuDiagnosticReadbackDecoder.SubmissionValidation,
            _ => EGpuDiagnosticReadbackDecoder.None,
        };

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
