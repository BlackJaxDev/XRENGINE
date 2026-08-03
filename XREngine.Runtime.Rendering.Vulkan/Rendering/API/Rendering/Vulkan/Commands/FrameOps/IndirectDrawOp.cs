namespace XREngine.Rendering.Vulkan;

internal sealed record IndirectDrawOp(
    int PassIndex,
    XRFrameBuffer? Target,
    VkDataBuffer IndirectBuffer,
    VkDataBuffer? ParameterBuffer,
    VkMeshRenderer MeshRenderer,
    PendingMeshDraw Draw,
    uint DrawCount,
    uint Stride,
    nuint ByteOffset,
    nuint CountByteOffset,
    bool UseCount,
    VulkanBindlessMaterialDescriptorBinding? BindlessMaterialTextures,
    FrameOpContext Context,
    VulkanIndirectSecondaryRecordingContract SecondaryRecordingContract = default) 
    : FrameOp(PassIndex, Target, Context)
{
    public VkDataBuffer IndirectBuffer { get; private set; } = IndirectBuffer;
    public VkDataBuffer? ParameterBuffer { get; private set; } = ParameterBuffer;
    public VkMeshRenderer MeshRenderer { get; private set; } = MeshRenderer;
    public PendingMeshDraw Draw { get; private set; } = Draw;
    public uint DrawCount { get; private set; } = DrawCount;
    public uint Stride { get; private set; } = Stride;
    public nuint ByteOffset { get; private set; } = ByteOffset;
    public nuint CountByteOffset { get; private set; } = CountByteOffset;
    public bool UseCount { get; private set; } = UseCount;
    public VulkanBindlessMaterialDescriptorBinding? BindlessMaterialTextures { get; private set; } = BindlessMaterialTextures;
    public VulkanIndirectSecondaryRecordingContract SecondaryRecordingContract { get; private set; } = SecondaryRecordingContract;
    public override EVulkanPrimaryPlanNodeKind Kind => EVulkanPrimaryPlanNodeKind.IndirectDraw;

    internal override int RecordPrimary(
        VulkanRenderer renderer,
        scoped ref VulkanRenderer.PrimaryCommandBufferRecordingState recordingState,
        in VulkanPrimaryOperationRecordingInfo recordingInfo)
    {
        int commandChainRunCount =
            renderer.CountContiguousIndirectCommandChainRun(
                ref recordingState,
                recordingInfo.OperationIndex,
                this,
                recordingInfo.PassIndex);
        if (recordingInfo.ExecutesSecondaryRange &&
            renderer.TryExecuteIndirectCommandChainSecondaryRun(
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

        renderer.EmitIndirectDrawRunReadBarrier(ref recordingState);
        System.Diagnostics.Debug.Assert(
            recordingInfo.BeginsRendering,
            "Indirect-draw primary-plan nodes must own render-scope entry.");
        if (recordingInfo.BeginsRendering)
        {
            renderer.BeginRenderPassForTarget(
                ref recordingState,
                Target,
                recordingInfo.PassIndex,
                recordingState.ActiveContext);
        }

        renderer.CmdBeginLabel(recordingState.CommandBuffer, "IndirectDraw");
        renderer.RecordIndirectDrawIntoCommandBuffer(
            ref recordingState,
            recordingState.CommandBuffer,
            this,
            recordingInfo.PassIndex,
            recordingInfo.OperationIndex);
        renderer.CmdEndLabel(recordingState.CommandBuffer);

        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanIndirectRecordingMode(
            usedSecondary: false,
            usedParallel: false,
            opCount: 1);
        if (Target is null)
            recordingState.ActualSwapchainWriteCount++;

        return recordingInfo.OperationIndex;
    }

    internal static IndirectDrawOp Rent(
        int passIndex,
        XRFrameBuffer? target,
        VkDataBuffer indirectBuffer,
        VkDataBuffer? parameterBuffer,
        VkMeshRenderer meshRenderer,
        in PendingMeshDraw draw,
        uint drawCount,
        uint stride,
        nuint byteOffset,
        nuint countByteOffset,
        bool useCount,
        VulkanBindlessMaterialDescriptorBinding? bindlessMaterialTextures,
        in FrameOpContext context,
        in VulkanIndirectSecondaryRecordingContract secondaryRecordingContract =
            default)
    {
        bool frameOwned = TryRentForCurrentFrame(out IndirectDrawOp? reusable);
        if (reusable is null)
        {
            IndirectDrawOp created = new(
                passIndex,
                target,
                indirectBuffer,
                parameterBuffer,
                meshRenderer,
                draw,
                drawCount,
                stride,
                byteOffset,
                countByteOffset,
                useCount,
                bindlessMaterialTextures,
                context,
                secondaryRecordingContract);
            return frameOwned ? RetainForCurrentFrame(created) : created;
        }

        reusable.Reset(
            passIndex,
            target,
            indirectBuffer,
            parameterBuffer,
            meshRenderer,
            draw,
            drawCount,
            stride,
            byteOffset,
            countByteOffset,
            useCount,
            bindlessMaterialTextures,
            context,
            secondaryRecordingContract);
        return reusable;
    }

    private void Reset(
        int passIndex,
        XRFrameBuffer? target,
        VkDataBuffer indirectBuffer,
        VkDataBuffer? parameterBuffer,
        VkMeshRenderer meshRenderer,
        in PendingMeshDraw draw,
        uint drawCount,
        uint stride,
        nuint byteOffset,
        nuint countByteOffset,
        bool useCount,
        VulkanBindlessMaterialDescriptorBinding? bindlessMaterialTextures,
        in FrameOpContext context,
        in VulkanIndirectSecondaryRecordingContract secondaryRecordingContract)
    {
        PassIndex = passIndex;
        Target = target;
        IndirectBuffer = indirectBuffer;
        ParameterBuffer = parameterBuffer;
        MeshRenderer = meshRenderer;
        Draw = draw;
        DrawCount = drawCount;
        Stride = stride;
        ByteOffset = byteOffset;
        CountByteOffset = countByteOffset;
        UseCount = useCount;
        BindlessMaterialTextures = bindlessMaterialTextures;
        Context = context;
        SecondaryRecordingContract = secondaryRecordingContract;
    }
}
