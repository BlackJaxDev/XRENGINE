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
    public AdvancedDeformationAdmissionResult Admission => _admission;
    public AdvancedIndirectPreparationResult IndirectResult => _indirectResult;
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
        ulong frameId = world.FrameId;
        ulong completedValue = frameId == 0UL ? 0UL : frameId - 1UL;
        if (!_frameUploadArena.TryBeginFrame(frameId, completedValue))
        {
            return CreateDeferredPublication(
                world,
                consumers,
                visibleFallbackCount: checked((uint)Math.Min(
                    world.GpuScene.TotalCommandCount,
                    (uint)_options.MaximumDraws)));
        }
        if (!_deformedArena.TryBeginFrame(frameId, completedValue))
        {
            _frameUploadArena.EndFrame(frameId);
            return CreateDeferredPublication(
                world,
                consumers,
                visibleFallbackCount: checked((uint)Math.Min(
                    world.GpuScene.TotalCommandCount,
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
                    world.GpuScene.TotalCommandCount,
                    (uint)_options.MaximumDraws)));
        }

        _deformationJobs.BeginFrame();
        BeginAnimationScheduling(frameId, completedValue);
        _visibilityPlanner.BeginFrame(frameId);
        _drawCount = checked((int)Math.Min(
            world.GpuScene.TotalCommandCount,
            (uint)_options.MaximumDraws));
        uint extractionOverflow =
            world.GpuScene.TotalCommandCount > (uint)_options.MaximumDraws
                ? world.GpuScene.TotalCommandCount -
                    (uint)_options.MaximumDraws
                : 0u;

        for (int commandIndex = 0;
             commandIndex < _drawCount;
             commandIndex++)
        {
            ExtractCommand(
                world.GpuScene,
                checked((uint)commandIndex),
                frameId);
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
        AddVisibilityPlans(viewSet);

        _deformedArena.EndFrame(frameId);
        _frameUploadArena.EndFrame(frameId);
        _gpuDeformation.EndFrame(frameId);
        _publicationGeneration++;
        ulong deformedVertices = 0UL;
        ReadOnlySpan<AdvancedDeformationJobRecord> jobs =
            _deformationJobs.Jobs;
        for (int i = 0; i < jobs.Length; i++)
            deformedVertices += jobs[i].VertexCount;

        return new AdvancedPreparationPublication(
            frameId,
            _publicationGeneration,
            SceneIdentity: unchecked((uint)RuntimeHelpers.GetHashCode(
                world.GpuScene)),
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

        for (int viewIndex = 0; viewIndex < views.ViewCount; viewIndex++)
        {
            RenderFrameViewDescriptor view = views.GetView(viewIndex);
            if (FindCurrentPlan(view.EffectiveHistoryKey) >= 0)
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

            if (_visibilityPlanCount < _visibilityPlans.Length)
                _visibilityPlans[_visibilityPlanCount++] = plan;
        }

        return _visibilityPlanCount;
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
        GPUScene scene,
        uint commandIndex,
        ulong frameId)
    {
        _drawDeformationCandidateIndices[commandIndex] = -1;
        _drawDeformationSlices[commandIndex] = default;
        _visibilityCandidates[commandIndex] = default;
        _visibilityPayloads[commandIndex] = default;
        if (!scene.TryGetAdvancedPreparationCommand(
                commandIndex,
                out DrawMetadata command))
        {
            return;
        }

        scene.TryGetSourceCommand(
            commandIndex,
            out IRenderCommandMesh? source);
        if (!scene.TryGetCanonicalAdvancedPreparationHandles(
                commandIndex,
                out AdvancedGpuHandle draw,
                out AdvancedGpuHandle geometry,
                out AdvancedGpuHandle material,
                out AdvancedGpuHandle deformation))
        {
            return;
        }
        bool commandChanged = scene.WasCanonicalDrawAddedThisPublication(draw);
        AdvancedMeshRenderSnapshot snapshot =
            CaptureSnapshot(source);
        XRMeshRenderer? renderer = snapshot.Renderer;
        XRMesh? mesh = renderer?.Mesh;

        EAdvancedVisibilityPreparationFlags visibilityFlags =
            commandChanged
                ? EAdvancedVisibilityPreparationFlags.NewRecord |
                  EAdvancedVisibilityPreparationFlags.ConservativeVisible
                : EAdvancedVisibilityPreparationFlags.None;
        BoundsGpu commandBounds = command.BoundsID < scene.CullBoundsBuffer.ElementCount
            ? scene.CullBoundsBuffer.GetDataRawAtIndex<BoundsGpu>(command.BoundsID)
            : default;
        _visibilityCandidates[commandIndex] =
            new AdvancedVisibilityCandidate(
                draw,
                commandBounds.BoundingSphere,
                commandBounds.AabbMin,
                commandBounds.AabbMax,
                ViewMask: ulong.MaxValue,
                BvhLeaf: command.BoundsID,
                visibilityFlags);

        AdvancedSceneGeometryOffsets offsets =
            ResolveGeometryOffsets(scene, command);
        bool skinned =
            (command.Flags & (uint)GPUIndirectRenderFlags.Skinned) != 0u &&
            mesh is { VertexCount: > 0 };
        bool meshletsResident =
            scene.TryGetMeshletRange(
                command.MeshID,
                out GPUScene.GpuMeshletRange meshletRange) &&
            meshletRange.HasMeshlets;
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
                meshletRange,
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
                IndexCount: ResolveIndexCount(scene, command),
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
        _publicationGeneration++;
        return new AdvancedPreparationPublication(
            world.FrameId,
            _publicationGeneration,
            SceneIdentity: unchecked((uint)RuntimeHelpers.GetHashCode(
                world.GpuScene)),
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
        GPUScene scene,
        in DrawMetadata command)
    {
        uint firstVertex = 0u;
        uint firstIndex = 0u;
        if (scene.TryGetMeshDataEntry(
                command.MeshID,
                out GPUScene.MeshDataEntry meshData))
        {
            firstVertex = meshData.FirstVertex;
            firstIndex = meshData.FirstIndex;
        }

        uint meshletOffset = 0u;
        uint meshletCount = 0u;
        if (scene.TryGetMeshletRange(
                command.MeshID,
                out GPUScene.GpuMeshletRange meshlets))
        {
            meshletOffset = meshlets.MeshletOffset;
            meshletCount = meshlets.MeshletCount;
        }

        return new AdvancedSceneGeometryOffsets(
            VertexOffset: firstVertex,
            PreviousVertexOffset: firstVertex,
            IndexOffset: firstIndex,
            WeightOffset: firstVertex,
            PaletteOffset: command.SkinID,
            MeshletOffset: meshletOffset,
            MeshletCount: meshletCount);
    }

    private static uint ResolveIndexCount(
        GPUScene scene,
        in DrawMetadata command)
        => scene.TryGetMeshDataEntry(
            command.MeshID,
            out GPUScene.MeshDataEntry meshData)
                ? meshData.IndexCount
                : 0u;

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
