using Silk.NET.OpenGL;
using XREngine.Data.Rendering;
using XREngine.Rendering.OpenGL;

namespace XREngine.Rendering.Compute;

/// <summary>
/// OpenGL implementation of the narrow physics-chain compute backend contract.
/// </summary>
internal sealed class OpenGLPhysicsChainComputeBackend : IPhysicsChainComputeBackend
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

    private readonly OpenGLRenderer _renderer;

    private OpenGLPhysicsChainComputeBackend(OpenGLRenderer renderer)
        => _renderer = renderer;

    public AbstractRenderer Renderer => _renderer;
    public string Name => _renderer.GetType().Name;
    public PhysicsChainComputeCapabilities Capabilities => SupportedCapabilities;

    public bool BeginBatch() => true;
    public void CommitBatch() { }
    public void RollbackBatch() { }

    /// <summary>
    /// Creates an adapter when the active renderer is OpenGL. Backend type checks stay
    /// here so the dispatcher remains independent of OpenGL implementation types.
    /// </summary>
    public static bool TryCreate(AbstractRenderer? renderer, out IPhysicsChainComputeBackend? backend)
    {
        if (renderer is OpenGLRenderer openGlRenderer)
        {
            backend = new OpenGLPhysicsChainComputeBackend(openGlRenderer);
            return true;
        }

        backend = null;
        return false;
    }

    public bool EnsureGpuBufferReady(XRDataBuffer buffer)
        => TryGetBufferIdForGpuCopy(buffer, out _);

    public PhysicsChainComputeEnqueueStatus TryDispatchDirect(
        XRRenderProgram program,
        uint groupsX,
        uint groupsY,
        uint groupsZ,
        PhysicsChainComputePassKind passKind)
        => (PhysicsChainComputeEnqueueStatus)_renderer.TryDispatchCompute(program, groupsX, groupsY, groupsZ);

    public PhysicsChainComputeEnqueueStatus TryCopyBuffer(in PhysicsChainComputeBufferCopy copy)
    {
        if (!TryGetBufferIdForGpuCopy(copy.Source, out uint sourceBufferId)
            || !TryGetBufferIdForGpuCopy(copy.Destination, out uint destinationBufferId))
            return PhysicsChainComputeEnqueueStatus.InvalidResource;

        _renderer.RawGL.CopyNamedBufferSubData(
            sourceBufferId,
            destinationBufferId,
            copy.SourceOffset,
            copy.DestinationOffset,
            copy.ByteCount);
        return PhysicsChainComputeEnqueueStatus.Enqueued;
    }

    public PhysicsChainComputeEnqueueStatus TryDispatchIndirect(
        XRRenderProgram program,
        XRDataBuffer arguments,
        nint byteOffset)
    {
        if (byteOffset < 0 || !TryGetBufferId(arguments, out uint argumentsBufferId))
            return PhysicsChainComputeEnqueueStatus.InvalidResource;
        if (_renderer.GetOrCreateAPIRenderObject(program, generateNow: true) is not OpenGLRenderer.GLRenderProgram glProgram)
            return PhysicsChainComputeEnqueueStatus.InvalidResource;
        if (!glProgram.Use())
            return PhysicsChainComputeEnqueueStatus.ProgramPending;

        _renderer.RawGL.BindBuffer(GLEnum.DispatchIndirectBuffer, argumentsBufferId);
        _renderer.RawGL.DispatchComputeIndirect(byteOffset);
        _renderer.RawGL.BindBuffer(GLEnum.DispatchIndirectBuffer, 0);
        return PhysicsChainComputeEnqueueStatus.Enqueued;
    }

    public PhysicsChainComputeEnqueueStatus TryCompletePass(in PhysicsChainComputePass pass)
    {
        _renderer.MemoryBarrier(pass.CompletionBarrier);
        return PhysicsChainComputeEnqueueStatus.Enqueued;
    }

    public XRGpuFence? InsertFence()
        => _renderer.InsertGpuFence();

    public unsafe bool TryReadBuffer(XRDataBuffer buffer, Span<byte> destination)
    {
        if (destination.IsEmpty)
            return true;
        if (!TryGetBufferId(buffer, out uint bufferId))
            return false;

        fixed (byte* pointer = destination)
        {
            _renderer.RawGL.BindBuffer(GLEnum.ShaderStorageBuffer, bufferId);
            _renderer.RawGL.GetBufferSubData(
                GLEnum.ShaderStorageBuffer,
                IntPtr.Zero,
                (nuint)destination.Length,
                pointer);
            _renderer.RawGL.BindBuffer(GLEnum.ShaderStorageBuffer, 0);
        }

        return true;
    }

    private bool TryGetBufferIdForGpuCopy(XRDataBuffer buffer, out uint bufferId)
    {
        bufferId = 0;
        if (_renderer.GetOrCreateAPIRenderObject(buffer, generateNow: true) is OpenGLRenderer.GLDataBuffer glBuffer)
        {
            glBuffer.EnsureStorageAllocatedForGpuCopy();
            return glBuffer.TryGetBindingId(out bufferId) && bufferId != 0;
        }

        return TryGetBufferId(buffer, out bufferId);
    }

    private static bool TryGetBufferId(XRDataBuffer buffer, out uint bufferId)
    {
        bufferId = 0;
        foreach (var wrapper in buffer.APIWrappers)
        {
            if (wrapper is OpenGLRenderer.GLDataBuffer glBuffer
                && glBuffer.TryGetBindingId(out bufferId)
                && bufferId != 0)
                return true;
        }

        return false;
    }
}
