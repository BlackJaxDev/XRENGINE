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
    FrameOpContext Context) : FrameOp(PassIndex, Target, Context)
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
        in FrameOpContext context)
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
                context);
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
            context);
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
        in FrameOpContext context)
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
    }
}