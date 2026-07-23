using XREngine.Data.Rendering;
using XREngine.Rendering.Vulkan;

namespace XREngine.Rendering.Compute;

/// <summary>Vulkan adapter for ordered physics-chain compute work.</summary>
internal sealed class VulkanPhysicsChainComputeBackend : IPhysicsChainComputeBackend
{
    private static readonly PhysicsChainComputeCapabilities SupportedCapabilities = new(
        SupportsComputeDispatch: true,
        SupportsShaderStorageBarriers: true,
        SupportsGpuBufferCopies: true,
        SupportsAsyncReadback: true,
        SupportsIndirectDispatch: true,
        SupportsSubgroupArithmetic: false,
        SupportsSubmissionFences: true,
        SupportsZeroReadbackPublication: true);

    private readonly VulkanRenderer _renderer;

    private VulkanPhysicsChainComputeBackend(VulkanRenderer renderer)
        => _renderer = renderer;

    public AbstractRenderer Renderer => _renderer;
    public string Name => _renderer.GetType().Name;
    public PhysicsChainComputeCapabilities Capabilities
        => _renderer.SupportsOrderedComputeWork
            ? SupportedCapabilities
            : default;

    public bool BeginBatch()
        => _renderer.TryBeginOrderedComputeBatch();

    public void CommitBatch()
        => _renderer.CommitOrderedComputeBatch();

    public void RollbackBatch()
        => _renderer.RollbackOrderedComputeBatch();

    public static bool TryCreate(AbstractRenderer? renderer, out IPhysicsChainComputeBackend? backend)
    {
        if (renderer is VulkanRenderer vulkanRenderer && vulkanRenderer.SupportsOrderedComputeWork)
        {
            backend = new VulkanPhysicsChainComputeBackend(vulkanRenderer);
            return true;
        }

        backend = null;
        return false;
    }

    public bool EnsureGpuBufferReady(XRDataBuffer buffer)
        => _renderer.TryEnsureComputeBufferReady(buffer);

    public PhysicsChainComputeEnqueueStatus TryDispatchDirect(
        XRRenderProgram program,
        uint groupsX,
        uint groupsY,
        uint groupsZ,
        PhysicsChainComputePassKind passKind)
        => (PhysicsChainComputeEnqueueStatus)_renderer.TryDispatchCompute(program, groupsX, groupsY, groupsZ);

    public PhysicsChainComputeEnqueueStatus TryCopyBuffer(in PhysicsChainComputeBufferCopy copy)
        => (PhysicsChainComputeEnqueueStatus)_renderer.TryEnqueueBufferCopy(
            copy.Source,
            copy.SourceOffset,
            copy.Destination,
            copy.DestinationOffset,
            copy.ByteCount,
            "PhysicsChain.BufferCopy");

    public PhysicsChainComputeEnqueueStatus TryDispatchIndirect(
        XRRenderProgram program,
        XRDataBuffer arguments,
        nint byteOffset)
        => (PhysicsChainComputeEnqueueStatus)_renderer.TryDispatchComputeIndirect(
            program,
            arguments,
            byteOffset,
            "PhysicsChain.IndirectDispatch");

    public PhysicsChainComputeEnqueueStatus TryCompletePass(in PhysicsChainComputePass pass)
        => (PhysicsChainComputeEnqueueStatus)_renderer.TryCompleteOrderedComputePass(
            pass.CompletionBarrier,
            GetPassCompletionLabel(pass.Kind));

    public XRGpuFence? InsertFence()
        => _renderer.InsertGpuFence();

    public bool TryReadBuffer(XRDataBuffer buffer, Span<byte> destination)
        => _renderer.TryReadMappedBuffer(buffer, destination);

    private static string GetPassCompletionLabel(PhysicsChainComputePassKind kind)
        => kind switch
        {
            PhysicsChainComputePassKind.ArenaGrowth => "PhysicsChain.ArenaGrowth.Completion",
            PhysicsChainComputePassKind.ActiveWorkReset => "PhysicsChain.ActiveWorkReset.Completion",
            PhysicsChainComputePassKind.ActiveWorkCompaction => "PhysicsChain.ActiveWorkCompaction.Completion",
            PhysicsChainComputePassKind.IndirectArgumentGeneration => "PhysicsChain.IndirectArgumentGeneration.Completion",
            PhysicsChainComputePassKind.Simulation => "PhysicsChain.Simulation.Completion",
            PhysicsChainComputePassKind.SelectiveReadbackGather => "PhysicsChain.SelectiveReadbackGather.Completion",
            PhysicsChainComputePassKind.BoundsPublication => "PhysicsChain.BoundsPublication.Completion",
            PhysicsChainComputePassKind.BonePalettePublication => "PhysicsChain.BonePalettePublication.Completion",
            PhysicsChainComputePassKind.ReadbackTransfer => "PhysicsChain.ReadbackTransfer.Completion",
            PhysicsChainComputePassKind.DebugVisualization => "PhysicsChain.DebugVisualization.Completion",
            _ => "PhysicsChain.Unknown.Completion",
        };
}
