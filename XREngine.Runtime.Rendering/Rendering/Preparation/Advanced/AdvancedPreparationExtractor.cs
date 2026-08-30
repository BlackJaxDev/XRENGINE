using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>
/// Live, allocation-free warmed extraction from the immutable legacy world
/// snapshot into aggregate deformation, visibility, and indirect preparation
/// records. It is pipeline-neutral and intentionally owns no output textures
/// or per-view temporal histories.
/// </summary>
public sealed class AdvancedPreparationExtractor : IDisposable
{
    private readonly AdvancedPreparationOptions _options;
    private readonly AdvancedAnimationScheduler _animationScheduler;
    private readonly AdvancedVisibilityFeedbackRing _visibilityFeedbackRing;
    private readonly AdvancedGpuHandle[] _feedbackLookupHandles;
    private readonly AdvancedAnimationVisibilityFeedback[]
        _feedbackLookupRecords;
    private readonly uint[] _feedbackLookupStamps;
    private readonly int[] _scheduleLookupIndices;
    private readonly uint[] _scheduleLookupStamps;
    private readonly AdvancedAnimationScheduleTelemetry[]
        _animationSchedules;
    private readonly AdvancedDeformationJobStream _deformationJobs;
    private readonly AdvancedFrameSlotUploadArena _frameUploadArena;
    private readonly AdvancedUploadCopyRange[] _uploadCopyRanges;
    private readonly AdvancedDeformedVertexArena _deformedArena;
    private readonly AdvancedGpuDeformationResources _gpuDeformation;
    private readonly AdvancedDeformationDispatchPlanner _dispatchPlanner;
    private readonly AdvancedVisibilityPlanner _visibilityPlanner;
    private readonly AdvancedIndirectRangePlanner _indirectPlanner;
    private readonly AdvancedVisibilityCandidate[] _visibilityCandidates;
    private readonly AdvancedVisibilityPayload[] _visibilityPayloads;
    private readonly AdvancedVisibilityDispatchPlan[] _visibilityPlans;
    private readonly RenderFrameViewDescriptor[] _visibilityPlanViews;
    private readonly AdvancedDeformedArenaSlice[] _drawDeformationSlices;
    private readonly int[] _drawDeformationCandidateIndices;
    private readonly uint[] _depthPyramidGenerations;
    private readonly ulong[] _viewHistoryKeys;
    private readonly AdvancedBoneLodTier[] _boneTierScratch;
    private AdvancedDeformationAdmissionResult _admission;
    private AdvancedIndirectPreparationResult _indirectResult;
    private AdvancedFrameUploadAllocation _deformationUpload;
    private int _drawCount;
    private int _visibilityPlanCount;
    private int _uploadCopyRangeCount;
    private int _animationScheduleCount;
    private bool _hasDeformationUpload;
    private uint _feedbackLookupGeneration;
    private uint _scheduleLookupGeneration;
    private ulong _publicationGeneration;
    private long _visibilityContentGeneration;
    private AdvancedGpuScenePublication _preparedScenePublication;
    private RenderFrameViewSet? _lastVisibilityViewSet;
    // This is an identity-only guard for the fixed extractor columns.  A
    // deferred Vulkan consumer must obtain record data from the package's
    // retained publication, never by retaining this mutable GPUScene.
    private uint _preparedSceneIdentity;

    public AdvancedPreparationExtractor(AdvancedPreparationOptions options)
    {
        ValidateOptions(options);
        _options = options;
        _animationScheduler = new AdvancedAnimationScheduler(
            options.MaximumDeformationJobs);
        _visibilityFeedbackRing = new AdvancedVisibilityFeedbackRing(
            options.DeformedArena.FrameSlotCount,
            options.MaximumDeformationJobs);
        int schedulingLookupCapacity = NextPowerOfTwo(
            checked(options.MaximumDeformationJobs * 2));
        _feedbackLookupHandles =
            new AdvancedGpuHandle[schedulingLookupCapacity];
        _feedbackLookupRecords =
            new AdvancedAnimationVisibilityFeedback[
                schedulingLookupCapacity];
        _feedbackLookupStamps =
            new uint[schedulingLookupCapacity];
        _scheduleLookupIndices =
            new int[schedulingLookupCapacity];
        _scheduleLookupStamps =
            new uint[schedulingLookupCapacity];
        _animationSchedules =
            new AdvancedAnimationScheduleTelemetry[
                options.MaximumDeformationJobs];
        _deformationJobs = new AdvancedDeformationJobStream(
            options.MaximumDeformationJobs);
        _frameUploadArena = new AdvancedFrameSlotUploadArena(
            options.FrameUploadArena);
        _uploadCopyRanges =
            new AdvancedUploadCopyRange[_frameUploadArena.MaxCopyRangeCount];
        _deformedArena = new AdvancedDeformedVertexArena(
            options.DeformedArena);
        _gpuDeformation = new AdvancedGpuDeformationResources(options);
        _dispatchPlanner = new AdvancedDeformationDispatchPlanner(
            options.MaximumDeformationJobs,
            options.MaximumDeformationFamilies);
        _visibilityPlanner = new AdvancedVisibilityPlanner(
            options.MaximumViews,
            options.MaximumDraws);
        _indirectPlanner = new AdvancedIndirectRangePlanner(
            options.MaximumDraws,
            options.MaximumIndirectRanges);
        _visibilityCandidates =
            new AdvancedVisibilityCandidate[options.MaximumDraws];
        _visibilityPayloads =
            new AdvancedVisibilityPayload[options.MaximumDraws];
        _visibilityPlans =
            new AdvancedVisibilityDispatchPlan[options.MaximumViews];
        _visibilityPlanViews =
            new RenderFrameViewDescriptor[options.MaximumViews];
        _drawDeformationSlices =
            new AdvancedDeformedArenaSlice[options.MaximumDraws];
        _drawDeformationCandidateIndices =
            new int[options.MaximumDraws];
        _depthPyramidGenerations = new uint[options.MaximumViews];
        _viewHistoryKeys = new ulong[options.MaximumViews];
        _boneTierScratch = new AdvancedBoneLodTier[1];
    }

    public AdvancedPreparationOptions Options => _options;
    public ReadOnlySpan<AdvancedVisibilityCandidate> VisibilityCandidates
        => _visibilityCandidates.AsSpan(0, _drawCount);
    public ReadOnlySpan<AdvancedVisibilityPayload> VisibilityPayloads
        => _visibilityPayloads.AsSpan(0, _drawCount);
    /// <summary>
    /// Verifies that a deferred backend consumer still observes the exact
    /// retained preparation generation it was handed. This guards the fixed
    /// payload arrays from a later world-frame extraction without copying them
    /// into a transient CPU fallback buffer.
    /// </summary>
    public bool MatchesPublication(in AdvancedPreparationPublication publication)
        => _preparedSceneIdentity != 0u && publication.GpuResourcesPublished &&
           publication.PublicationGeneration == _publicationGeneration &&
           publication.VisibilityContentGeneration == VisibilityContentGeneration &&
           publication.ScenePublication == _preparedScenePublication &&
           publication.DrawCount == (uint)_drawCount &&
           publication.SceneIdentity == _preparedSceneIdentity &&
           publication.FrameId != 0u;
    public ReadOnlySpan<EAdvancedGeometryProducer> VisibilityProducers
        => _indirectPlanner.ProducersByPayload;
    public ReadOnlySpan<AdvancedVisibilityDispatchPlan> VisibilityPlans
        => _visibilityPlans.AsSpan(0, _visibilityPlanCount);
    public ReadOnlySpan<AdvancedDeformationJobRecord> DeformationJobs
        => _deformationJobs.Jobs;
    public ReadOnlySpan<AdvancedDeformationDispatchBatch> DeformationBatches
        => _dispatchPlanner.Batches;
    public AdvancedDeformationDispatchPlanner DispatchPlanner
        => _dispatchPlanner;
    public ReadOnlySpan<AdvancedIndirectRange> IndirectRanges
        => _indirectPlanner.Ranges;
    /// <summary>
    /// Original payload indices grouped in the exact order occupied by the
    /// indirect ranges. The visibility set-1 publisher uses this immutable
    /// permutation to stamp each payload's range without reclassification.
    /// </summary>
    public ReadOnlySpan<int> IndirectPayloadIndices
        => _indirectPlanner.PayloadIndices;
    public AdvancedDeformationAdmissionResult Admission => _admission;
    public AdvancedIndirectPreparationResult IndirectResult => _indirectResult;
    /// <summary>
    /// Monotonic identity for the mutable visibility columns retained by this
    /// extractor. Deferred consumers must validate it before using those arrays.
    /// </summary>
    public ulong VisibilityContentGeneration
        => unchecked((ulong)Volatile.Read(ref _visibilityContentGeneration));
    public AdvancedDeformedVertexArenaTelemetry ArenaTelemetry
        => _deformedArena.GetTelemetry();
    public AdvancedFrameUploadTelemetrySnapshot FrameUploadTelemetry
        => _frameUploadArena.GetTelemetrySnapshot();
    public AdvancedGpuDeformationResources GpuDeformation
        => _gpuDeformation;
    public AdvancedGpuDeformationPublication GpuDeformationPublication
        => _gpuDeformation.Publication;
    public ReadOnlySpan<AdvancedAnimationScheduleTelemetry>
        AnimationSchedules
        => _animationSchedules.AsSpan(0, _animationScheduleCount);
    public bool HasDeformationUpload => _hasDeformationUpload;
    public AdvancedFrameUploadAllocation DeformationUpload
        => _deformationUpload;
    public ReadOnlySpan<AdvancedUploadCopyRange> UploadCopyRanges
        => _uploadCopyRanges.AsSpan(0, _uploadCopyRangeCount);

    public AdvancedPreparationPublication Build(
        in RenderWorldSnapshot world,
        RenderFrameViewSet? viewSet,
        EAdvancedPreparationConsumer consumers)
    {
        // Do not let deferred or failed preparation leave consumers observing
        // arrays tied to the preceding world frame.
        AdvanceVisibilityContentGeneration();
        _preparedSceneIdentity = 0u;
        _preparedScenePublication = default;
        _drawCount = 0;
        _visibilityPlanCount = 0;
        _lastVisibilityViewSet = null;
        _uploadCopyRangeCount = 0;
        ulong frameId = world.FrameId;
        if (!world.GpuScene.AdvancedSharedDatabase.TryGetPublicationSnapshot(
                world.GpuScene.AdvancedScenePublication,
                out AdvancedGpuScenePublicationSnapshot publicationSnapshot) ||
            publicationSnapshot.Submission.Sequence != world.GpuScene.AdvancedScenePublication.Sequence)
            return CreateDeferredPublication(world, consumers, 0u);
        uint submissionCount = checked((uint)publicationSnapshot.Submission.Records.Length);
        ulong completedValue = frameId == 0UL ? 0UL : frameId - 1UL;
        if (!_frameUploadArena.TryBeginFrame(frameId, completedValue))
        {
            return CreateDeferredPublication(
                world,
                consumers,
            visibleFallbackCount: checked((uint)Math.Min(
                    submissionCount,
                    (uint)_options.MaximumDraws)));
        }
        if (!_deformedArena.TryBeginFrame(frameId, completedValue))
        {
            _frameUploadArena.EndFrame(frameId);
            return CreateDeferredPublication(
                world,
                consumers,
            visibleFallbackCount: checked((uint)Math.Min(
                    submissionCount,
                    (uint)_options.MaximumDraws)));
        }
        if (!_gpuDeformation.TryBeginFrame(
                frameId,
                completedValue,
                _deformedArena.CurrentFrameSlot,
                _deformedArena.PreviousFrameSlot,
                _deformedArena.VertexCapacity))
        {
            _deformedArena.EndFrame(frameId);
            _frameUploadArena.EndFrame(frameId);
            return CreateDeferredPublication(
                world,
                consumers,
            visibleFallbackCount: checked((uint)Math.Min(
                    submissionCount,
                    (uint)_options.MaximumDraws)));
        }

        try
        {
        _deformationJobs.BeginFrame();
        BeginAnimationScheduling(frameId, completedValue);
        _visibilityPlanner.BeginFrame(frameId);
        _drawCount = checked((int)Math.Min(
            submissionCount,
            (uint)_options.MaximumDraws));
        uint extractionOverflow =
            submissionCount > (uint)_options.MaximumDraws
                ? submissionCount -
                    (uint)_options.MaximumDraws
                : 0u;

        for (int commandIndex = 0;
             commandIndex < _drawCount;
             commandIndex++)
        {
            ExtractCommand(publicationSnapshot, commandIndex, frameId);
        }

        _admission = _deformationJobs.FinalizeJobs(
            _options.DeformationBudget);
        ApplyDeformationAdmissionVerdicts();
        ReadOnlySpan<AdvancedDeformationJobRecord> finalizedJobs =
            _deformationJobs.Jobs;
        _deformationUpload = default;
        _hasDeformationUpload =
            finalizedJobs.IsEmpty ||
            _deformationJobs.TryUpload(
                _frameUploadArena,
                out _deformationUpload);
        _dispatchPlanner.Build(
            _hasDeformationUpload
                ? finalizedJobs
                : ReadOnlySpan<AdvancedDeformationJobRecord>.Empty);
        _gpuDeformation.Publish(
            _hasDeformationUpload
                ? finalizedJobs
                : ReadOnlySpan<AdvancedDeformationJobRecord>.Empty,
            _dispatchPlanner.JobIndices,
            _dispatchPlanner.JobVertexOffsets);
        if (!_frameUploadArena.TryBuildCurrentCopyPlan(
                _uploadCopyRanges,
                out _uploadCopyRangeCount))
        {
            throw new InvalidOperationException(
                "The fixed advanced upload copy plan is smaller than its arena contract.");
        }
        _indirectResult = _indirectPlanner.Build(
            _visibilityPayloads.AsSpan(0, _drawCount),
            argumentBufferBase: 0u,
            countBufferBase: 0u,
            argumentStride: 20u,
            countStride: 4u,
            submissionStrategy:
                RuntimeEngine.Rendering.ResolveMeshSubmissionStrategy());
        _visibilityPlanCount = 0;
        if (viewSet is RenderFrameViewSet initialViews)
        {
            _lastVisibilityViewSet = initialViews;
            AddVisibilityPlansCore(initialViews, replaceMask: true);
        }

        _publicationGeneration++;
        _preparedScenePublication =
            world.GpuScene.AdvancedScenePublication.Publication;
        _preparedSceneIdentity = unchecked((uint)RuntimeHelpers.GetHashCode(
            world.GpuScene));
        ulong deformedVertices = 0UL;
        ReadOnlySpan<AdvancedDeformationJobRecord> jobs =
            _deformationJobs.Jobs;
        for (int i = 0; i < jobs.Length; i++)
            deformedVertices += jobs[i].VertexCount;

        return new AdvancedPreparationPublication(
            frameId,
            _publicationGeneration,
            VisibilityContentGeneration,
            SceneIdentity: unchecked((uint)RuntimeHelpers.GetHashCode(
                world.GpuScene)),
            ScenePublication: _preparedScenePublication,
            DrawCount: checked((uint)_drawCount),
            DeformationJobCount: checked((uint)jobs.Length),
            DeformationDispatchCount: checked((uint)_dispatchPlanner.Batches.Length),
            VisibilityViewCount: checked((uint)_visibilityPlanCount),
            IndirectRangeCount: checked((uint)_indirectPlanner.Ranges.Length),
            VisibleFallbackCount: checked(
                _admission.VisibleFallbackCount +
                extractionOverflow +
                (_hasDeformationUpload
                    ? 0u
                    : checked((uint)jobs.Length))),
            DeformedVertexCount: deformedVertices,
            DeformationUploadBytes:
                _hasDeformationUpload
                    ? _deformationUpload.ByteCount
                    : 0u,
            UploadCopyRangeCount: checked((uint)_uploadCopyRangeCount),
            Consumers: consumers,
            RequiresCpuReadback: false,
            WarmedManagedAllocationFree: true,
            GpuResourcesPublished: true,
            AggregateDispatchExecuted: false,
            Backend: RuntimeGraphicsApiKind.Unknown,
            DeformationGpuMilliseconds: 0.0);
        }
        finally
        {
            _gpuDeformation.EndFrame(frameId);
            _deformedArena.EndFrame(frameId);
            _frameUploadArena.EndFrame(frameId);
        }
    }

    public bool TryGetDrawDeformationSlice(
        uint drawIndex,
        out AdvancedDeformedArenaSlice slice)
    {
        if (drawIndex >= (uint)_drawCount)
        {
            slice = default;
            return false;
        }

        slice = _drawDeformationSlices[drawIndex];
        return slice.Owner.IsValid;
    }

    /// <summary>
    /// Adds an output-local view set to the already prepared world frame
    /// without rebuilding shared scene/deformation/indirect work.
    /// </summary>
    public int AddVisibilityPlans(RenderFrameViewSet? viewSet)
    {
        if (viewSet is not RenderFrameViewSet views)
            return _visibilityPlanCount;
        if (_lastVisibilityViewSet is RenderFrameViewSet previousViews &&
            previousViews.Equals(views))
        {
            return _visibilityPlanCount;
        }

        // Invalidate every previously captured publication before mutating
        // candidate masks, temporal depth generations, or dispatch plans.
        AdvanceVisibilityContentGeneration();
        _lastVisibilityViewSet = views;
        return AddVisibilityPlansCore(views, replaceMask: false);
    }

    private int AddVisibilityPlansCore(
        in RenderFrameViewSet views,
        bool replaceMask)
    {
        // A shared extraction can accumulate output-local view sets. Keep the
        // candidate mask as their exact union; early dispatch selects one
        // canonical view-id from that union for each set-1 segment.
        ClassifyCandidatesForViews(views, replaceMask);

        for (int viewIndex = 0; viewIndex < views.ViewCount; viewIndex++)
        {
            RenderFrameViewDescriptor view = views.GetView(viewIndex);
            int currentPlanIndex = FindCurrentPlan(view.EffectiveHistoryKey);
            if (currentPlanIndex >= 0 &&
                _visibilityPlanViews[currentPlanIndex].Equals(view))
                continue;

            int viewSlot = FindOrAddViewSlot(view.EffectiveHistoryKey);
            if (viewSlot < 0)
                continue;

            uint previousGeneration = _depthPyramidGenerations[viewSlot];
            uint currentGeneration = checked(previousGeneration + 1u);
            _depthPyramidGenerations[viewSlot] = currentGeneration;
            AdvancedDepthPyramidContract depth = new(
                view.EffectiveHistoryKey,
                view.ViewRect.Width,
                view.ViewRect.Height,
                ResolveMipCount(view.ViewRect.Width, view.ViewRect.Height),
                currentGeneration,
                previousGeneration,
                PreviousValid: previousGeneration != 0u,
                view.DepthZeroToOne,
                view.ReversedDepth);
            AdvancedVisibilityDispatchPlan plan =
                _visibilityPlanner.BuildPlan(
                    viewSlot,
                    depth,
                    _visibilityCandidates.AsSpan(0, _drawCount),
                    earlyIndirectArgumentOffset: checked((uint)viewSlot * 64u),
                    deferredCandidateOffset: checked((uint)viewSlot * 16u),
                    lateIndirectArgumentOffset:
                        checked((uint)viewSlot * 64u + 32u),
                    persistentStateOffset:
                        checked((uint)viewSlot *
                        (uint)_options.MaximumDraws),
                    gpuCounterOffset: checked((uint)viewSlot * 16u));

            if (currentPlanIndex >= 0)
            {
                _visibilityPlans[currentPlanIndex] = plan;
                _visibilityPlanViews[currentPlanIndex] = view;
            }
            else if (_visibilityPlanCount < _visibilityPlans.Length)
            {
                _visibilityPlans[_visibilityPlanCount] = plan;
                _visibilityPlanViews[_visibilityPlanCount++] = view;
            }
        }

        return _visibilityPlanCount;
    }

    private void AdvanceVisibilityContentGeneration()
    {
        long generation = Interlocked.Increment(ref _visibilityContentGeneration);
        if (generation == 0)
            _ = Interlocked.Increment(ref _visibilityContentGeneration);
    }

    private void ClassifyCandidatesForViews(
        RenderFrameViewSet? viewSet,
        bool replaceMask)
    {
        if (viewSet is not RenderFrameViewSet views)
            return;

        for (int candidateIndex = 0; candidateIndex < _drawCount; ++candidateIndex)
        {
            AdvancedVisibilityCandidate candidate = _visibilityCandidates[candidateIndex];
            ulong viewMask = replaceMask ? 0UL : candidate.ViewMask;
            for (int viewIndex = 0; viewIndex < views.ViewCount; ++viewIndex)
            {
                RenderFrameViewDescriptor view = views.GetView(viewIndex);
                if (view.ViewId >= 64u ||
                    !SphereIntersectsView(candidate.BoundsSphere, in view))
                {
                    continue;
                }

                viewMask |= 1UL << checked((int)view.ViewId);
            }
            _visibilityCandidates[candidateIndex] = candidate with
            {
                ViewMask = viewMask,
            };
        }
    }

    private static bool SphereIntersectsView(
        Vector4 sphere,
        in RenderFrameViewDescriptor view)
    {
        if (!float.IsFinite(sphere.X) || !float.IsFinite(sphere.Y) ||
            !float.IsFinite(sphere.Z) || !float.IsFinite(sphere.W) ||
            sphere.W < 0.0f)
        {
            return false;
        }

        Matrix4x4 projection = view.ProjectionMatrixUnjittered == default
            ? view.ProjectionMatrix
            : view.ProjectionMatrixUnjittered;
        Matrix4x4 viewProjection = view.ViewMatrix * projection;
        Vector4 columnX = new(viewProjection.M11, viewProjection.M21,
            viewProjection.M31, viewProjection.M41);
        Vector4 columnY = new(viewProjection.M12, viewProjection.M22,
            viewProjection.M32, viewProjection.M42);
        Vector4 columnZ = new(viewProjection.M13, viewProjection.M23,
            viewProjection.M33, viewProjection.M43);
        Vector4 columnW = new(viewProjection.M14, viewProjection.M24,
            viewProjection.M34, viewProjection.M44);
        return SphereInsidePlane(sphere, columnW + columnX) &&
            SphereInsidePlane(sphere, columnW - columnX) &&
            SphereInsidePlane(sphere, columnW + columnY) &&
            SphereInsidePlane(sphere, columnW - columnY) &&
            SphereInsidePlane(sphere, columnZ) &&
            SphereInsidePlane(sphere, columnW - columnZ);
    }

    private static bool SphereInsidePlane(Vector4 sphere, Vector4 plane)
    {
        float normalLength = plane.X * plane.X + plane.Y * plane.Y +
            plane.Z * plane.Z;
        return normalLength > 0.0f &&
            Vector4.Dot(plane, new Vector4(sphere.X, sphere.Y, sphere.Z, 1.0f)) >=
            -sphere.W * MathF.Sqrt(normalLength);
    }

    /// <summary>
    /// Publishes a completion-gated CPU mirror of delayed GPU animation
    /// relevance. Callers must never pass records from an unfinished frame.
    /// </summary>
    public void PublishVisibilityFeedback(
        ulong frameId,
        ReadOnlySpan<AdvancedAnimationVisibilityFeedback> feedback,
        ulong completionValue)
    {
        if (feedback.Length > _visibilityFeedbackRing.RecordCapacity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(feedback),
                "Visibility feedback exceeds the fixed animation scheduler capacity.");
        }

        feedback.CopyTo(
            _visibilityFeedbackRing.GetGpuWritableMirror(frameId));
        _visibilityFeedbackRing.SealGpuWrite(
            frameId,
            feedback.Length,
            completionValue);
    }

    private void ExtractCommand(
        AdvancedGpuScenePublicationSnapshot publication,
        int commandIndex,
        ulong frameId)
    {
        _drawDeformationCandidateIndices[commandIndex] = -1;
        _drawDeformationSlices[commandIndex] = default;
        _visibilityCandidates[commandIndex] = default;
        _visibilityPayloads[commandIndex] = default;
        AdvancedDrawSubmissionRecord submission = publication.Submission.Records[commandIndex];
        DrawMetadata command = new()
        {
            RenderPass = submission.PassIndex,
            SubmeshID = submission.PrimitiveIndex,
            Flags = submission.Flags,
            StateClassID = submission.StateClass,
            InstanceCount = submission.InstanceCount,
        };
        IRenderCommandMesh? source = publication.Submission.DeformationSources[commandIndex].Source;
        AdvancedMeshRenderSnapshot snapshot = CaptureSnapshot(source);
        AdvancedGpuHandle draw = submission.Draw;
        AdvancedGpuHandle geometry = submission.Geometry;
        AdvancedGpuHandle material = submission.Material;
        AdvancedGpuHandle deformation = submission.Deformation;
        bool commandChanged = false;
        XRMeshRenderer? renderer = snapshot.Renderer;
        XRMesh? mesh = renderer?.Mesh;

        EAdvancedVisibilityPreparationFlags visibilityFlags =
            commandChanged
                ? EAdvancedVisibilityPreparationFlags.NewRecord |
                  EAdvancedVisibilityPreparationFlags.ConservativeVisible
                : EAdvancedVisibilityPreparationFlags.None;
        publication.Geometry.TryGet(geometry, out AdvancedGeometryRecord canonicalGeometry);
        BoundsGpu commandBounds = new()
        {
            BoundingSphere = canonicalGeometry.BoundsSphere,
            AabbMin = canonicalGeometry.BoundsMin,
            AabbMax = canonicalGeometry.BoundsMax,
        };
        _visibilityCandidates[commandIndex] =
            new AdvancedVisibilityCandidate(
                draw,
                commandBounds.BoundingSphere,
                commandBounds.AabbMin,
                commandBounds.AabbMax,
                ViewMask: ulong.MaxValue,
                BvhLeaf: 0u,
                visibilityFlags);

        bool hasCanonicalGeometry = geometry.IsValid && canonicalGeometry.IsResident;
        AdvancedSceneGeometryOffsets offsets = ResolveGeometryOffsets(
            hasCanonicalGeometry,
            in canonicalGeometry,
            command.SkinID);
        bool skinned =
            (command.Flags & (uint)GPUIndirectRenderFlags.Skinned) != 0u &&
            mesh is { VertexCount: > 0 };
        bool meshletsResident = hasCanonicalGeometry &&
            canonicalGeometry.MeshletCount != 0u &&
            canonicalGeometry.MeshletDescriptors.IsValid &&
            canonicalGeometry.MeshletVertexIndices.IsValid &&
            canonicalGeometry.MeshletTriangleWords.IsValid;
        GPUScene.GpuMeshletRange deformationMeshletRange = new()
        {
            MeshletOffset = canonicalGeometry.MeshletFirst,
            MeshletCount = canonicalGeometry.MeshletCount,
        };
        AdvancedDeformedArenaSlice deformationSlice = default;
        int deformationCandidateIndex = -1;
        bool aggregateDeformationAvailable = false;
        if (skinned && renderer is not null && mesh is not null)
        {
            aggregateDeformationAvailable = TryAddDeformation(
                renderer,
                mesh,
                command,
                geometry,
                deformation,
                (visibilityFlags & EAdvancedVisibilityPreparationFlags.NewRecord) != 0,
                offsets,
                deformationMeshletRange,
                frameId,
                out deformationSlice,
                out AdvancedGpuDeformationMeshSlice gpuMeshSlice,
                out AdvancedGpuDeformationPoseSlice gpuPoseSlice,
                out deformationCandidateIndex);
            if (aggregateDeformationAvailable)
            {
                offsets = offsets with
                {
                    VertexOffset =
                        deformationSlice.CurrentVertexOffset,
                    PreviousVertexOffset =
                        deformationSlice.PreviousVertexOffset,
                    WeightOffset =
                        gpuMeshSlice.BoneInfluenceOffset,
                    PaletteOffset =
                        gpuPoseSlice.BonePaletteOffset,
                };
            }
        }
        _drawDeformationSlices[commandIndex] = deformationSlice;
        _drawDeformationCandidateIndices[commandIndex] =
            deformationCandidateIndex;

        _visibilityPayloads[commandIndex] =
            new AdvancedVisibilityPayload(
                draw,
                geometry,
                material,
                offsets,
                PrimitiveSection: command.SubmeshID,
                InstanceCount: Math.Max(1u, command.InstanceCount),
                FirstIndex: offsets.IndexOffset,
                IndexCount: hasCanonicalGeometry
                    ? canonicalGeometry.IndexCount
                    : 0u,
                VertexCount: checked((uint)Math.Max(0, mesh?.VertexCount ?? 0)),
                RasterStateClass: command.StateClassID,
                Coverage: ResolveCoverage(command),
                CullMode:
                    (command.Flags &
                     (uint)GPUIndirectRenderFlags.DoubleSided) != 0u
                        ? 0u
                        : 1u,
                PrimitiveTopology: checked((uint)(
                    mesh?.Type ?? EPrimitiveType.Triangles)),
                Skinned: skinned,
                MeshletsResident: meshletsResident,
                ForceCpuDiagnostic:
                    snapshot.ForceCpuRendering ||
                    mesh is null ||
                    !hasCanonicalGeometry ||
                    (skinned && !aggregateDeformationAvailable));
    }

    private bool TryAddDeformation(
        XRMeshRenderer renderer,
        XRMesh mesh,
        in DrawMetadata command,
        AdvancedGpuHandle geometry,
        AdvancedGpuHandle deformation,
        bool newlyVisible,
        in AdvancedSceneGeometryOffsets sourceOffsets,
        in GPUScene.GpuMeshletRange meshletRange,
        ulong frameId,
        out AdvancedDeformedArenaSlice slice,
        out AdvancedGpuDeformationMeshSlice gpuMeshSlice,
        out AdvancedGpuDeformationPoseSlice gpuPoseSlice,
        out int canonicalCandidateIndex)
    {
        gpuMeshSlice = default;
        gpuPoseSlice = default;
        canonicalCandidateIndex = -1;
        if (!geometry.IsValid || !deformation.IsValid)
        {
            slice = default;
            return false;
        }
        AdvancedGpuHandle meshHandle = geometry;
        AdvancedGpuHandle poseHandle = deformation;
        AdvancedGpuHandle outputHandle = deformation;

        uint topologyGeneration = ComputeTopologyGeneration(
            mesh,
            meshletRange);
        if (!_gpuDeformation.TryGetOrAddMesh(
                mesh,
                topologyGeneration,
                out gpuMeshSlice) ||
            !_gpuDeformation.TryGetOrAddPose(
                renderer,
                mesh,
                out gpuPoseSlice))
        {
            slice = default;
            return false;
        }
        if (!_deformedArena.TryAcquireSlice(
                outputHandle,
                checked((uint)mesh.VertexCount),
                topologyGeneration,
                command.LodPolicy + 1u,
                newlyVisible,
                out slice))
        {
            return false;
        }

        // The deformation scheduler consumes the draw record independently of
        // culling; its conservative contribution is refined by visibility later.
        float contribution = 1.0f;
        _boneTierScratch[0] = new AdvancedBoneLodTier(
            renderer.ActiveSkinPaletteCount,
            EAdvancedAnimationBoneRequirement.RuntimeRequired |
            EAdvancedAnimationBoneRequirement.IkTarget |
            EAdvancedAnimationBoneRequirement.Attachment |
            EAdvancedAnimationBoneRequirement.PhysicsChain);
        AdvancedAnimationScheduleDecision schedule =
            GetOrCreateAnimationSchedule(
                poseHandle,
                contribution,
                newlyVisible,
                renderer.ActiveSkinPaletteCount,
                frameId);

        EAdvancedDeformationFeatureFlags features =
            EAdvancedDeformationFeatureFlags.Skinning |
            EAdvancedDeformationFeatureFlags.Velocity |
            EAdvancedDeformationFeatureFlags.PrecomposedPalette;
        if (mesh.HasNormals)
            features |= EAdvancedDeformationFeatureFlags.Normals;
        if (mesh.HasTangents)
            features |= EAdvancedDeformationFeatureFlags.Tangents;
        if (mesh.HasSpillInfluences)
            features |= EAdvancedDeformationFeatureFlags.SpillInfluences;
        if (gpuPoseSlice.ActiveBlendshapeCount != 0u &&
            gpuMeshSlice.BlendshapeCount != 0u)
            features |= EAdvancedDeformationFeatureFlags.Blendshapes;
        if (mesh.MaxBlendshapeAccumulation)
            features |=
                EAdvancedDeformationFeatureFlags.MaximumBlendshapeAccumulation;
        if (meshletRange.HasMeshlets)
            features |= EAdvancedDeformationFeatureFlags.Meshlets;
        if (!slice.HasValidVelocity ||
            !_gpuDeformation.PreviousOutputValid)
            features |= EAdvancedDeformationFeatureFlags.VelocityInvalid;

        uint poseGeneration = FoldGeneration(
            renderer.SkinnedOutputVersion,
            renderer.BlendshapeWeightsVersion,
            schedule.BoneTier);
        ulong vertexLayoutId = AdvancedDeformedVertex.CanonicalLayoutId;
        AdvancedDeformationJobRecord job = new()
        {
            Mesh = meshHandle,
            SharedPose = poseHandle,
            SourceVertexOffset = gpuMeshSlice.SourceVertexOffset,
            CurrentVertexOffset = slice.CurrentVertexOffset,
            PreviousVertexOffset = slice.PreviousVertexOffset,
            BoneInfluenceOffset = gpuMeshSlice.BoneInfluenceOffset,
            BonePaletteOffset = gpuPoseSlice.BonePaletteOffset,
            InverseBindOffset = 0u,
            BlendshapeWeightOffset =
                gpuPoseSlice.ActiveBlendshapeOffset,
            BlendshapeShapeOffset =
                gpuMeshSlice.BlendshapeRangeOffset,
            VertexFirst = 0u,
            VertexCount = checked((uint)mesh.VertexCount),
            MeshletFirst = meshletRange.MeshletOffset,
            MeshletCount = meshletRange.MeshletCount,
            BoneCount = gpuPoseSlice.BoneCount,
            BlendshapeCount =
                gpuPoseSlice.ActiveBlendshapeCount,
            MeshGeneration = meshHandle.Generation,
            PoseGeneration = poseGeneration,
            PaletteGeneration = poseGeneration,
            TopologyGeneration = topologyGeneration,
            VertexLayoutId = vertexLayoutId,
            Features = features,
            Precision = EAdvancedDeformationPrecision.Packed,
            Order = EAdvancedDeformationOrder.BlendshapeThenSkinning,
            OutputStride = checked((uint)Unsafe.SizeOf<AdvancedDeformedVertex>()),
        };
        AdvancedDeformationJobKey key = new(
            meshHandle,
            poseHandle,
            job.MeshGeneration,
            job.PoseGeneration,
            job.PaletteGeneration,
            job.TopologyGeneration,
            vertexLayoutId,
            features,
            job.Precision);
        return _deformationJobs.TryAdd(
            new AdvancedDeformationCandidate(
                job,
                key,
                contribution,
                Mandatory: true,
                Visible: true),
            out canonicalCandidateIndex);
    }

    private void ApplyDeformationAdmissionVerdicts()
    {
        for (int drawIndex = 0; drawIndex < _drawCount; drawIndex++)
        {
            int candidateIndex =
                _drawDeformationCandidateIndices[drawIndex];
            if (candidateIndex < 0 ||
                _deformationJobs.IsCandidateAdmitted(candidateIndex))
            {
                continue;
            }

            _drawDeformationSlices[drawIndex] = default;
            _visibilityPayloads[drawIndex] =
                _visibilityPayloads[drawIndex] with
                {
                    Flags =
                        _visibilityPayloads[drawIndex].Flags |
                        EAdvancedVisibilityPayloadFlags
                            .ForceCpuDiagnostic,
                };
        }
    }

    private void BeginAnimationScheduling(
        ulong frameId,
        ulong completedValue)
    {
        _animationScheduleCount = 0;
        AdvanceLookupGeneration(
            ref _scheduleLookupGeneration,
            _scheduleLookupStamps);
        AdvanceLookupGeneration(
            ref _feedbackLookupGeneration,
            _feedbackLookupStamps);
        if (!_visibilityFeedbackRing.TryGetLatestCompleted(
                frameId == 0UL ? 0UL : frameId - 1UL,
                completedValue,
                out ReadOnlySpan<
                    AdvancedAnimationVisibilityFeedback> feedback,
                out _))
        {
            return;
        }

        uint mask = checked(
            (uint)_feedbackLookupHandles.Length - 1u);
        for (int feedbackIndex = 0;
             feedbackIndex < feedback.Length;
             feedbackIndex++)
        {
            AdvancedAnimationVisibilityFeedback record =
                feedback[feedbackIndex];
            if (!record.Entity.IsValid)
                continue;

            uint start = Hash(record.Entity) & mask;
            for (uint probe = 0u;
                 probe < (uint)_feedbackLookupHandles.Length;
                 probe++)
            {
                int slot = checked((int)((start + probe) & mask));
                if (_feedbackLookupStamps[slot] !=
                    _feedbackLookupGeneration)
                {
                    _feedbackLookupStamps[slot] =
                        _feedbackLookupGeneration;
                    _feedbackLookupHandles[slot] = record.Entity;
                    _feedbackLookupRecords[slot] = record;
                    break;
                }
                if (_feedbackLookupHandles[slot] != record.Entity)
                    continue;

                AdvancedAnimationVisibilityFeedback existing =
                    _feedbackLookupRecords[slot];
                _feedbackLookupRecords[slot] = record with
                {
                    LastVisibleFrame = Math.Max(
                        existing.LastVisibleFrame,
                        record.LastVisibleFrame),
                    ProjectedDiameter = Math.Max(
                        existing.ProjectedDiameter,
                        record.ProjectedDiameter),
                    ViewMask = existing.ViewMask | record.ViewMask,
                    Flags = existing.Flags | record.Flags,
                };
                break;
            }
        }
    }

    private AdvancedAnimationScheduleDecision GetOrCreateAnimationSchedule(
        AdvancedGpuHandle pose,
        float projectedContribution,
        bool newlyVisible,
        uint runtimeRequiredBoneCount,
        ulong frameId)
    {
        uint mask = checked(
            (uint)_scheduleLookupIndices.Length - 1u);
        uint start = Hash(pose) & mask;
        int insertionSlot = -1;
        for (uint probe = 0u;
             probe < (uint)_scheduleLookupIndices.Length;
             probe++)
        {
            int slot = checked((int)((start + probe) & mask));
            if (_scheduleLookupStamps[slot] !=
                _scheduleLookupGeneration)
            {
                insertionSlot = slot;
                break;
            }

            int telemetryIndex = _scheduleLookupIndices[slot];
            if (_animationSchedules[telemetryIndex].Entity == pose)
            {
                return _animationSchedules[telemetryIndex]
                    .Decision;
            }
        }

        AdvancedAnimationVisibilityFeedback feedback;
        if (!TryGetVisibilityFeedback(pose, out feedback))
        {
            EAdvancedAnimationVisibilityFlags flags =
                EAdvancedAnimationVisibilityFlags.Visible |
                EAdvancedAnimationVisibilityFlags.ShadowRelevant;
            if (newlyVisible)
                flags |= EAdvancedAnimationVisibilityFlags.NewlyVisible;
            feedback = new AdvancedAnimationVisibilityFeedback(
                pose,
                frameId,
                projectedContribution,
                projectedContribution > 0.0f
                    ? 1.0f / projectedContribution
                    : float.MaxValue,
                ulong.MaxValue,
                flags);
        }
        else if (newlyVisible)
        {
            feedback = feedback with
            {
                Flags = feedback.Flags |
                    EAdvancedAnimationVisibilityFlags.NewlyVisible,
            };
        }

        double renderDelta =
            RuntimeRenderingHostServices.FrameTiming.RenderDeltaSeconds;
        float deltaSeconds =
            renderDelta >= 0.0 && double.IsFinite(renderDelta)
                ? checked((float)Math.Min(renderDelta, float.MaxValue))
                : 0.0f;
        AdvancedAnimationScheduleDecision decision =
            _animationScheduler.Schedule(
                feedback,
                AdvancedAnimationScheduleProfile.Default,
                _boneTierScratch,
                EAdvancedAnimationBoneRequirement.RuntimeRequired |
                EAdvancedAnimationBoneRequirement.IkTarget |
                EAdvancedAnimationBoneRequirement.Attachment |
                EAdvancedAnimationBoneRequirement.PhysicsChain,
                runtimeRequiredBoneCount,
                requestedBoneTier: 0u,
                frameId,
                deltaSeconds,
                gameplayCpuAnimationRequired: false);
        if (_animationScheduleCount >= _animationSchedules.Length ||
            insertionSlot < 0)
        {
            return decision;
        }

        int index = _animationScheduleCount++;
        _animationSchedules[index] =
            new AdvancedAnimationScheduleTelemetry(pose, decision);
        _scheduleLookupStamps[insertionSlot] =
            _scheduleLookupGeneration;
        _scheduleLookupIndices[insertionSlot] = index;
        return decision;
    }

    private bool TryGetVisibilityFeedback(
        AdvancedGpuHandle pose,
        out AdvancedAnimationVisibilityFeedback feedback)
    {
        uint mask = checked(
            (uint)_feedbackLookupHandles.Length - 1u);
        uint start = Hash(pose) & mask;
        for (uint probe = 0u;
             probe < (uint)_feedbackLookupHandles.Length;
             probe++)
        {
            int slot = checked((int)((start + probe) & mask));
            if (_feedbackLookupStamps[slot] !=
                _feedbackLookupGeneration)
            {
                break;
            }
            if (_feedbackLookupHandles[slot] != pose)
                continue;

            feedback = _feedbackLookupRecords[slot];
            return true;
        }

        feedback = default;
        return false;
    }

    private static void AdvanceLookupGeneration(
        ref uint generation,
        uint[] stamps)
    {
        generation++;
        if (generation != 0u)
            return;

        Array.Clear(stamps);
        generation = 1u;
    }

    private static uint Hash(AdvancedGpuHandle handle)
    {
        uint value = handle.Index * 0x9E3779B9u;
        value ^= handle.Generation +
            0x85EBCA6Bu +
            (value << 6) +
            (value >> 2);
        value ^= value >> 16;
        value *= 0x7FEB352Du;
        value ^= value >> 15;
        return value;
    }

    private int FindOrAddViewSlot(ulong historyKey)
    {
        int empty = -1;
        for (int i = 0; i < _viewHistoryKeys.Length; i++)
        {
            if (_viewHistoryKeys[i] == historyKey)
                return i;
            if (_viewHistoryKeys[i] == 0UL && empty < 0)
                empty = i;
        }

        if (empty >= 0)
            _viewHistoryKeys[empty] = historyKey;
        return empty;
    }

    private int FindCurrentPlan(ulong historyKey)
    {
        for (int i = 0; i < _visibilityPlanCount; i++)
            if (_visibilityPlans[i].ViewHistoryKey == historyKey)
                return i;
        return -1;
    }

    private AdvancedPreparationPublication CreateDeferredPublication(
        in RenderWorldSnapshot world,
        EAdvancedPreparationConsumer consumers,
        uint visibleFallbackCount)
    {
        _preparedSceneIdentity = 0u;
        _preparedScenePublication = default;
        _drawCount = 0;
        _visibilityPlanCount = 0;
        _lastVisibilityViewSet = null;
        _uploadCopyRangeCount = 0;
        _publicationGeneration++;
        return new AdvancedPreparationPublication(
            world.FrameId,
            _publicationGeneration,
            VisibilityContentGeneration,
            SceneIdentity: unchecked((uint)RuntimeHelpers.GetHashCode(
                world.GpuScene)),
            ScenePublication:
                world.GpuScene.AdvancedScenePublication.Publication,
            DrawCount: 0u,
            DeformationJobCount: 0u,
            DeformationDispatchCount: 0u,
            VisibilityViewCount: 0u,
            IndirectRangeCount: 0u,
            VisibleFallbackCount: visibleFallbackCount,
            DeformedVertexCount: 0UL,
            DeformationUploadBytes: 0u,
            UploadCopyRangeCount: 0u,
            Consumers: consumers,
            RequiresCpuReadback: false,
            WarmedManagedAllocationFree: true,
            GpuResourcesPublished: false,
            AggregateDispatchExecuted: false,
            Backend: RuntimeGraphicsApiKind.Unknown,
            DeformationGpuMilliseconds: 0.0);
    }

    public void Dispose()
    {
        _preparedSceneIdentity = 0u;
        _preparedScenePublication = default;
        _drawCount = 0;
        _visibilityPlanCount = 0;
        _gpuDeformation.Dispose();
        _frameUploadArena.Dispose();
    }

    private static AdvancedMeshRenderSnapshot CaptureSnapshot(
        IRenderCommandMesh? source)
    {
        if (source is RenderCommandMesh3D mesh3D)
            return mesh3D.CaptureAdvancedPreparationSnapshot();
        if (source is null)
            return default;
        return new AdvancedMeshRenderSnapshot(
            source.Mesh,
            source.WorldMatrix,
            source.WorldMatrix,
            source.Instances,
            source.WorldMatrixIsModelMatrix,
            source.ForceCpuRendering,
            source.MaterialOverride,
            source.RenderOptionsOverride);
    }

    private static AdvancedSceneGeometryOffsets ResolveGeometryOffsets(
        bool hasCanonicalGeometry,
        in AdvancedGeometryRecord geometry,
        uint skinId)
    {
        if (!hasCanonicalGeometry)
            return default;

        return new AdvancedSceneGeometryOffsets(
            VertexOffset: geometry.CurrentVertexData.ElementOffset,
            PreviousVertexOffset: geometry.PreviousVertexData.ElementOffset,
            IndexOffset: geometry.IndexData.ElementOffset,
            WeightOffset: geometry.CurrentVertexData.ElementOffset,
            PaletteOffset: skinId,
            MeshletOffset: geometry.MeshletFirst,
            MeshletCount: geometry.MeshletCount);
    }

    private static EAdvancedMaterialCoverageMode ResolveCoverage(
        in DrawMetadata command)
    {
        if ((command.Flags &
             (uint)GPUIndirectRenderFlags.Transparent) != 0u)
        {
            return EAdvancedMaterialCoverageMode.Transparent;
        }
        return command.StateClassID == (uint)EGpuMaterialStateClass.AlphaTested
            ? EAdvancedMaterialCoverageMode.Masked
            : EAdvancedMaterialCoverageMode.Opaque;
    }

    private static float ComputeProjectedContribution(in BoundsGpu bounds)
    {
        float radius = Math.Max(0.0f, bounds.BoundingSphere.W);
        float distance = Math.Max(radius, bounds.BoundingSphere.Length());
        return distance > 0.0f
            ? Math.Clamp(radius / distance, 0.0f, 1.0f)
            : 1.0f;
    }

    private static uint ComputeTopologyGeneration(
        XRMesh mesh,
        in GPUScene.GpuMeshletRange meshlets)
    {
        uint value = checked((uint)Math.Max(0, mesh.VertexCount));
        value = (value * 16777619u) ^ checked((uint)Math.Max(0, mesh.IndexCount));
        value = (value * 16777619u) ^ (uint)mesh.Type;
        value = (value * 16777619u) ^ meshlets.MeshletCount;
        return value == 0u ? 1u : value;
    }

    private static uint FoldGeneration(
        ulong first,
        ulong second,
        uint third)
    {
        ulong value = first ^ (second << 1) ^ (second >> 63) ^ third;
        uint folded = (uint)value ^ (uint)(value >> 32);
        return folded == 0u ? 1u : folded;
    }

    private static uint ResolveMipCount(uint width, uint height)
    {
        uint maximum = Math.Max(width, height);
        uint count = 1u;
        while (maximum > 1u)
        {
            maximum >>= 1;
            count++;
        }
        return count;
    }

    private static int NextPowerOfTwo(int value)
    {
        if (value <= 1)
            return 1;

        uint rounded = checked((uint)value - 1u);
        rounded |= rounded >> 1;
        rounded |= rounded >> 2;
        rounded |= rounded >> 4;
        rounded |= rounded >> 8;
        rounded |= rounded >> 16;
        return checked((int)(rounded + 1u));
    }

    private static void ValidateOptions(
        in AdvancedPreparationOptions options)
    {
        if (options.MaximumDraws <= 0 ||
            options.MaximumDeformationJobs <= 0 ||
            options.MaximumDeformationFamilies <= 0 ||
            options.MaximumIndirectRanges <= 0 ||
            options.MaximumViews <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Advanced preparation capacities must be positive.");
        }
    }
}
