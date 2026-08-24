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
    private PendingMeshDraw _draw = Draw;
    public PendingMeshDraw Draw
    {
        get => _draw;
        private set => _draw = value;
    }
    internal ref readonly PendingMeshDraw DrawRef => ref _draw;
    public uint DrawCount { get; private set; } = DrawCount;
    public uint Stride { get; private set; } = Stride;
    public nuint ByteOffset { get; private set; } = ByteOffset;
    public nuint CountByteOffset { get; private set; } = CountByteOffset;
    public bool UseCount { get; private set; } = UseCount;
    public VulkanBindlessMaterialDescriptorBinding? BindlessMaterialTextures { get; private set; } = BindlessMaterialTextures;
    public VulkanIndirectSecondaryRecordingContract SecondaryRecordingContract { get; private set; } = SecondaryRecordingContract;
    public override EVulkanPrimaryPlanNodeKind Kind => EVulkanPrimaryPlanNodeKind.IndirectDraw;

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
        bool frameOwned = TryRentForCurrentFrame(context, out IndirectDrawOp? reusable);
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
            return frameOwned ? RetainForCurrentFrame(created, context) : created;
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
