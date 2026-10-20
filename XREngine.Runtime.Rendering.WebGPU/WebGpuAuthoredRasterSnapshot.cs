using System.Globalization;
using XREngine.Data.Geometry;

namespace XREngine.Rendering.WebGPU;

/// <summary>Completion-owned GPU copies and immutable raster commands for one authored candidate.</summary>
internal sealed class WebGpuAuthoredRasterSnapshot(WebGpuRendererHost renderer) : IDisposable
{
    private const int MaximumBuffers = 32;
    private sealed class BufferCopy(WebGpuRendererHost host)
    {
        internal readonly WebGpuOwnedStorageBuffer Buffer = new(host, "Authored ordered raster input",
            BrowserBufferUsage.Vertex | BrowserBufferUsage.Index | BrowserBufferUsage.CopySource);
        internal int Source;
        internal AbstractRenderAPIObject? SourceOwner;
        internal int Length;
        internal int Command;
        internal WebGpuResourceRequest? Request;
    }

    private readonly List<BufferCopy> _buffers = new(8);
    private readonly int[] _commands = new int[64];
    private readonly uint[] _uniformOffsets = new uint[16];
    private int _bufferCount;
    private int _uniformCount;
    private int _rankCount;
    private ulong _revision;
    private ulong _commandRevision;
    private WebGpuMeshDraw? _draw;
    private WebGpuBindingSet? _bindings;
    private int _argumentHandle;
    private BoundingRectangle? _viewport;
    private BoundingRectangle? _scissor;
    private uint[]? _rankUniformOffsets;
    private uint? _directInstances;
    internal WebGpuOwnedStorageBuffer Arguments { get; } = new(renderer,
        "Authored ranked indexed arguments", BrowserBufferUsage.Indirect);
    internal ulong Revision => _revision;
    internal Span<uint> UniformOffsets => _uniformOffsets;
    internal Span<uint> UniformOffsetsForRank(int rank)
    {
        _rankUniformOffsets ??= new uint[64 * 16];
        return _rankUniformOffsets.AsSpan(rank * 16, 16);
    }

    internal void BeginCapture(int rankCount)
    {
        _bufferCount = 0;
        _rankCount = rankCount;
        Arguments.EnsureCapacity(checked(rankCount * 20));
    }

    internal WebGpuOwnedStorageBuffer CaptureBuffer(AbstractRenderAPIObject source, int handle, uint byteLength)
    {
        for (int index = 0; index < _bufferCount; index++)
            if (_buffers[index].Source == handle)
            {
                if (_buffers[index].Length < byteLength)
                    throw Unsupported("BufferExtentChanged", "one raster input was captured with inconsistent byte extents");
                return _buffers[index].Buffer;
            }
        if (_bufferCount == MaximumBuffers)
            throw Unsupported("BufferCapacity", "one candidate exceeds 32 distinct raster input buffers");
        if (source is WebGpuOwnedStorageBuffer owned && !owned.SupportsCopySource)
            throw Unsupported("BufferCopyUnavailable", "a backend-owned raster input does not declare GPU copy-source usage");
        if (source is WebGpuDataBuffer data)
        {
            data.Generate();
            data.StagePendingUpload();
            if (handle != data.ResourceHandle)
                throw Unsupported("BufferGenerationChanged", "a raster input changed its physical generation while captured");
        }
        int length = checked((int)((byteLength + 3u) & ~3u));
        if (length == 0) throw Unsupported("EmptyBuffer", "a raster input has no copyable bytes");
        renderer.ReserveAuthoredOrderingCopyBytes(length);
        if (_bufferCount == _buffers.Count) _buffers.Add(new(renderer));
        BufferCopy copy = _buffers[_bufferCount++];
        int previous = copy.Buffer.ResourceHandle;
        copy.Buffer.EnsureCapacity(length);
        if (copy.Source != handle || copy.Length != length || previous != copy.Buffer.ResourceHandle || copy.Command == 0)
        {
            if (copy.Command != 0) renderer.RetireEngineResourceAfterFrame(copy.Command);
            copy.Command = 0;
            copy.Source = handle;
            copy.SourceOwner = source;
            copy.Length = length;
            copy.Command = renderer.CreateEngineReplacement(this, ref copy.Request, 7, string.Create(CultureInfo.InvariantCulture,
                $"{{\"label\":\"Authored ordered raster input snapshot\",\"commands\":[{{\"type\":\"copyBuffer\",\"source\":{handle},\"sourceOffset\":0,\"destination\":{copy.Buffer.ResourceHandle},\"destinationOffset\":0,\"size\":{length}}}]}}"));
            _revision = checked(_revision + 1);
        }
        return copy.Buffer;
    }

    internal int ResolveBuffer(int sourceHandle)
    {
        for (int index = 0; index < _bufferCount; index++)
            if (_buffers[index].Source == sourceHandle) return _buffers[index].Buffer.ResourceHandle;
        throw Unsupported("BufferSnapshotMissing", "a deferred raster input has no candidate-owned image");
    }

    internal void RecordCopies()
    {
        renderer.RequireAuthoredOrderingCommandCapacity(_bufferCount + _rankCount + 1);
        for (int index = 0; index < _bufferCount; index++)
        {
            BufferCopy copy = _buffers[index];
            renderer.RecordEngineCommands(copy.Command, []);
            copy.Buffer.MarkRecorded();
        }
    }

    internal void PrepareRaster(WebGpuMeshDraw draw, WebGpuBindingSet bindings, int uniformCount,
        BoundingRectangle? viewport, BoundingRectangle? scissor, uint? directInstances = null)
    {
        if (!ReferenceEquals(_draw, draw) || !ReferenceEquals(_bindings, bindings) ||
            _argumentHandle != Arguments.ResourceHandle || _commandRevision != _revision ||
            _directInstances.HasValue != directInstances.HasValue)
        {
            ReleaseRasterCommands();
            _draw = draw;
            _bindings = bindings;
            _argumentHandle = Arguments.ResourceHandle;
            _commandRevision = _revision;
        }
        _uniformCount = uniformCount;
        _viewport = viewport;
        _scissor = scissor;
        _directInstances = directInstances;
        if (directInstances.HasValue)
        {
            if (_commands[0] == 0) _commands[0] = draw.PrepareRankedDirectRaster(bindings, this);
        }
        else
            for (int rank = 0; rank < _rankCount; rank++)
                if (_commands[rank] == 0) _commands[rank] = draw.PrepareRankedRaster(bindings, this, checked((uint)(rank * 20)));
        bindings.MarkRecorded();
    }

    internal void RecordRank(int rank)
    {
        int command = _commands[_directInstances.HasValue ? 0 : rank];
        if (_draw is null || !_draw.IsReady || _bindings is null || _bindings.IsDisposed || rank >= _rankCount || command == 0)
            throw Unsupported("RasterGenerationChanged", "a deferred candidate lost its exact raster command generation");
        ReadOnlySpan<uint> offsets = _directInstances.HasValue
            ? _rankUniformOffsets!.AsSpan(rank * 16, _uniformCount) : _uniformOffsets.AsSpan(0, _uniformCount);
        renderer.RecordEngineCommands(command, offsets, _directInstances, _viewport, _scissor);
        Arguments.MarkRecorded();
        _draw.MarkRankedRasterRecorded(_bindings, _scissor, _directInstances);
    }

    private void ReleaseRasterCommands()
    {
        foreach (int command in _commands)
            if (command != 0) renderer.RetireEngineResourceAfterFrame(command);
        Array.Clear(_commands);
    }

    internal void InvalidateRaster()
    {
        ReleaseRasterCommands();
        _draw = null;
        _bindings = null;
    }

    internal void ReleaseCommandsUsingHandle(AbstractRenderAPIObject resource, int handle)
    {
        if (_bindings?.UsesHandle(resource, handle) == true ||
            ReferenceEquals(resource, Arguments) && _argumentHandle == handle)
            InvalidateRaster();
        foreach (BufferCopy copy in _buffers)
            if (ReferenceEquals(copy.SourceOwner, resource) && copy.Source == handle && copy.Command != 0)
            {
                renderer.RetireEngineResourceAfterFrame(copy.Command);
                copy.Command = 0;
                copy.SourceOwner = null;
            }
    }

    internal void ReleaseCommandUsing(WebGpuBindingSet bindings)
    {
        if (ReferenceEquals(_bindings, bindings)) InvalidateRaster();
    }

    internal void ReleaseDrawUsing(AbstractRenderAPIObject resource)
    {
        if (_draw?.DependsOn(resource) == true || _bindings?.DependsOn(resource) == true) InvalidateRaster();
    }

    public void Dispose()
    {
        renderer.CancelEngineResourceRequests(this);
        ReleaseRasterCommands();
        foreach (BufferCopy copy in _buffers)
        {
            if (copy.Command != 0) renderer.RetireEngineResourceAfterFrame(copy.Command);
            copy.Buffer.Dispose();
        }
        _buffers.Clear();
        Arguments.Dispose();
    }

    private static NotSupportedException Unsupported(string code, string detail)
        => new($"WebGPU.AuthoredOrdering.{code}: {detail}.");
}
