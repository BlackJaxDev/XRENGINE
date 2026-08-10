using System;

namespace XREngine.Rendering.Vulkan;

internal sealed record MeshDrawOp(int PassIndex, XRFrameBuffer? Target, PendingMeshDraw Draw, FrameOpContext Context) 
    : FrameOp(PassIndex, Target, Context)
{
    private PendingMeshDraw _draw = Draw;
    private DescriptorBindingSnapshot _descriptorBindingSnapshot;
    private bool _hasDescriptorBindingSnapshot;
    private VulkanMeshDrawSortKey _canonicalSortKey;
    private bool _hasCanonicalSortKey;

    public PendingMeshDraw Draw
    {
        get => _draw;
        private set
        {
            _draw = value;
            _hasCanonicalSortKey = false;
        }
    }

    internal ref readonly PendingMeshDraw DrawRef => ref _draw;

    internal ref readonly VulkanMeshDrawSortKey CanonicalSortKey
    {
        get
        {
            if (!_hasCanonicalSortKey)
            {
                _canonicalSortKey = VulkanMeshDrawSortKey.Capture(this);
                _hasCanonicalSortKey = true;
            }

            return ref _canonicalSortKey;
        }
    }

    /// <summary>
    /// Returns the immutable descriptor dependency captured while lowering this
    /// frame operation. Mutable frame-source sampler snapshots deliberately bypass
    /// this cache because their logical binding can acquire a new physical image,
    /// view, or sampler while the retained draw operation remains unchanged.
    /// </summary>
    internal bool TryGetDescriptorBindingSnapshot(
        out DescriptorBindingSnapshot snapshot)
    {
        snapshot = _descriptorBindingSnapshot;
        return _hasDescriptorBindingSnapshot;
    }

    internal void SetDescriptorBindingSnapshot(
        in DescriptorBindingSnapshot snapshot)
    {
        ThrowIfSealedForFramePlan();
        _descriptorBindingSnapshot = snapshot;
        _hasDescriptorBindingSnapshot = true;
    }

    /// <summary>
    /// True when this draw was enqueued inside an occlusion QueryOp Begin/End bracket
    /// (CPU occlusion proxy AABB draws). Such draws must keep their enqueue position
    /// relative to the surrounding QueryOps: canonical opaque-draw reordering would
    /// make the frame-op sort comparer intransitive and scramble Begin/End pairing
    /// (observed as VUID-vkCmdBeginQuery-queryPool-01922 and
    /// VUID-vkEndCommandBuffer-commandBuffer-00061).
    /// </summary>
    private bool _preserveSubmissionOrder;
    internal bool PreserveSubmissionOrder
    {
        get => _preserveSubmissionOrder;
        set
        {
            ThrowIfSealedForFramePlan();
            _preserveSubmissionOrder = value;
            _hasCanonicalSortKey = false;
        }
    }

    public override EVulkanPrimaryPlanNodeKind Kind => EVulkanPrimaryPlanNodeKind.MeshDraw;

    internal override FrameOp CreateSealedPlanSnapshot()
    {
        ThrowIfSealedForFramePlan();
        return SealPlanSnapshot(this with { Draw = Draw.CreateSealedCopy() });
    }

    internal override int RecordPrimary(
        VulkanCommandRuntime renderer,
        scoped ref PrimaryCommandBufferRecordingState recordingState,
        in VulkanPrimaryOperationRecordingInfo recordingInfo)
    {
        if (VulkanCommandRuntime.CommandRecordingDiagnosticsEnabled &&
            string.Equals(
                Draw.Renderer.MeshRenderer.Mesh?.Name,
                "CpuOcclusionProxy.UnitCube",
                StringComparison.Ordinal))
        {
            Debug.VulkanEvery(
                "Vulkan.CpuOcclusionProxy.RecordState",
                TimeSpan.FromSeconds(1),
                "[Vulkan][CpuQueryDiag] activeQuery={0} viewport=({1},{2},{3},{4}) scissor=({5},{6},{7},{8}) modelT=({9:F3},{10:F3},{11:F3}) modelS=({12:F3},{13:F3},{14:F3}) cameraT=({15:F3},{16:F3},{17:F3}).",
                recordingState.ActiveInlineQuery is not null,
                Draw.Viewport.X,
                Draw.Viewport.Y,
                Draw.Viewport.Width,
                Draw.Viewport.Height,
                Draw.Scissor.Offset.X,
                Draw.Scissor.Offset.Y,
                Draw.Scissor.Extent.Width,
                Draw.Scissor.Extent.Height,
                Draw.ModelMatrix.M41,
                Draw.ModelMatrix.M42,
                Draw.ModelMatrix.M43,
                Draw.ModelMatrix.M11,
                Draw.ModelMatrix.M22,
                Draw.ModelMatrix.M33,
                Draw.CameraPosition.X,
                Draw.CameraPosition.Y,
                Draw.CameraPosition.Z);
        }

        if (recordingInfo.ExecutesSecondaryRange &&
            recordingInfo.OperationIndex >=
            recordingState.MeshSecondaryFallbackEndIndex)
        {
            int commandChainRunCount = renderer.CountContiguousMeshCommandChainRun(
                ref recordingState,
                recordingInfo.OperationIndex,
                this,
                recordingInfo.PassIndex);
            if (renderer.TryExecuteScheduledMeshCommandChainSecondaryRun(
                    ref recordingState,
                    recordingInfo.OperationIndex,
                    commandChainRunCount,
                    recordingInfo.PassIndex,
                    this) ||
                renderer.TryExecuteMeshCommandChainSecondaryRun(
                    ref recordingState,
                    recordingInfo.OperationIndex,
                    commandChainRunCount,
                    recordingInfo.PassIndex,
                    this))
            {
                if (Target is null)
                    recordingState.ActualSwapchainWriteCount += commandChainRunCount;
                return recordingInfo.OperationIndex + commandChainRunCount - 1;
            }

            // A failed whole-run preparation must not retry the same remaining
            // suffix from every following draw. Encode the rest of this bounded
            // run inline once; the next independent render range may try again.
            recordingState.MeshSecondaryFallbackEndIndex = Math.Max(
                recordingState.MeshSecondaryFallbackEndIndex,
                recordingInfo.OperationIndex + commandChainRunCount);
        }

        int inlineDrawUniformSlot = renderer.GetMeshDrawUniformSlot(
            ref recordingState,
            recordingInfo.OperationIndex,
            Draw.Renderer,
            Context,
            Draw);
        System.Diagnostics.Debug.Assert(
            recordingInfo.BeginsRendering,
            "Mesh-draw primary-plan nodes must own render-scope entry.");
        if (recordingInfo.BeginsRendering &&
            !recordingState.RenderScope.MatchesTarget(Target))
        {
            renderer.EndActiveRenderPass(ref recordingState);
            Draw.Renderer.TryTransitionPreparedDescriptorImagesForSampling(
                recordingState.CommandBuffer,
                Draw,
                inlineDrawUniformSlot,
                recordingState.CommandBufferImageSlot,
                Target);
            renderer.BeginRenderPassForTarget(
                ref recordingState,
                Target,
                recordingInfo.PassIndex,
                recordingState.ActiveContext);
        }

        bool recordedInlineDraw = renderer.RecordMeshDrawIntoCommandBuffer(
            ref recordingState,
            recordingState.CommandBuffer,
            this,
            recordingInfo.PassIndex,
            inlineDrawUniformSlot);
        if (recordingState.ActiveInlineQuery is not null && recordedInlineDraw)
            recordingState.ActiveInlineQueryRecordedDraw = true;
        if (Target is null)
            recordingState.ActualSwapchainWriteCount++;

        return recordingInfo.OperationIndex;
    }

    internal static MeshDrawOp Rent(
        int passIndex,
        XRFrameBuffer? target,
        in PendingMeshDraw draw,
        in FrameOpContext context,
        bool preserveSubmissionOrder)
    {
        bool frameOwned = TryRentForCurrentFrame(context, out MeshDrawOp? reusable);
        if (reusable is null)
        {
            MeshDrawOp created = new(passIndex, target, draw, context)
            {
                PreserveSubmissionOrder = preserveSubmissionOrder,
            };
            return frameOwned ? RetainForCurrentFrame(created, context) : created;
        }

        reusable.Reset(
            passIndex,
            target,
            draw,
            context,
            preserveSubmissionOrder);
        return reusable;
    }

    internal void Reset(
        int passIndex,
        XRFrameBuffer? target,
        in PendingMeshDraw draw,
        in FrameOpContext context,
        bool preserveSubmissionOrder)
    {
        PassIndex = passIndex;
        Target = target;
        Draw = draw;
        Context = context;
        PreserveSubmissionOrder = preserveSubmissionOrder;
        _descriptorBindingSnapshot = default;
        _hasDescriptorBindingSnapshot = false;
        _canonicalSortKey = default;
        _hasCanonicalSortKey = false;
    }
}
