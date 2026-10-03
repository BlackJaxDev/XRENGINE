using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.WebGPU;

internal sealed partial class WebGpuMeshDraw
{
    private readonly record struct IndirectCommandKey(WebGpuBindingSet Bindings,
        AbstractRenderAPIObject Arguments, WebGpuDataBuffer? Count, int ArgumentHandle, int CountHandle,
        uint DrawCount, uint Stride, uint Offset, uint CountOffset);
    private readonly Dictionary<IndirectCommandKey, WebGpuIndirectDrawCommand> _indirectCommands = [];

    /// <summary>Dictionary republishing does not invalidate unchanged canonical atlas stream identities.</summary>
    internal bool MatchesIndirectStreams(XRMesh? mesh, XRMeshRenderer owner)
    {
        foreach (ShaderVertexBufferLayout layout in _program.Artifact.VertexBuffers)
            foreach (ShaderVertexAttribute attribute in layout.Attributes)
            {
                var (buffer, offset, format) = ResolveAttribute(mesh, attribute.Semantic, null, owner);
                bool found = false;
                foreach (WebGpuVertexStream stream in _streams)
                {
                    if (!ReferenceEquals(stream.Buffer.Data, buffer) || stream.Stride != buffer.ElementSize ||
                        stream.StepMode != (buffer.InstanceDivisor == 0 ? "vertex" : "instance")) continue;
                    foreach (ShaderVertexAttribute resolved in stream.Attributes)
                        if (resolved.Semantic == attribute.Semantic && resolved.Offset == offset && resolved.Format == format)
                        {
                            found = true;
                            break;
                        }
                    if (found) break;
                }
                if (!found) return false;
            }
        return true;
    }

    internal void RecordIndirect(WebGpuBindingSet bindings, WebGpuDataBuffer arguments,
        WebGpuDataBuffer? countBuffer, uint drawCount, uint stride, uint offset, uint countOffset)
        => RecordIndirectCore(bindings, arguments, arguments.ResourceHandle, countBuffer, drawCount, stride, offset, countOffset);

    internal void RecordVertexlessIndirect(WebGpuBindingSet bindings, AbstractRenderAPIObject arguments,
        int argumentHandle, uint byteOffset)
        => RecordIndirectCore(bindings, arguments, argumentHandle, null, 1, 16, byteOffset, 0);

    private void RecordIndirectCore(WebGpuBindingSet bindings, AbstractRenderAPIObject arguments,
        int argumentHandle, WebGpuDataBuffer? countBuffer, uint drawCount, uint stride, uint offset, uint countOffset)
    {
        if (!IsReady)
            throw new InvalidOperationException("WebGPU.Indirect.PipelineRequired: the retained raster pipeline must be ready.");
        StageGeometryUploads();
        if (bindings.IsDisposed)
        {
            _renderer.MarkEngineDrawPending();
            return;
        }
        if (_indices is null && countBuffer is not null)
            throw Unsupported("vertexless draws consume one native GPU-written draw record; indexed count lowering is not applicable");
        IndirectCommandKey key = new(bindings, arguments, countBuffer, argumentHandle,
            countBuffer?.ResourceHandle ?? 0, drawCount, stride, offset, countOffset);
        if (!_indirectCommands.TryGetValue(key, out WebGpuIndirectDrawCommand? command))
        {
            int capacity = bindings.IsNativeRaster ? WebGpuAdvancedVisibilityFrame.MaximumRetainedBuckets : 128;
            if (_indirectCommands.Count >= capacity)
                throw Unsupported($"indirect geometry exceeds {capacity} retained argument/binding variants");
            command = new WebGpuIndirectDrawCommand(_renderer, countBuffer is null ? null :
                new WebGpuIndirectCountArguments(_renderer, (WebGpuDataBuffer)arguments, countBuffer, drawCount, stride, offset, countOffset));
            _indirectCommands.Add(key, command);
        }
        if (!command.IsReady)
        {
            _renderer.MarkEngineDrawPending();
            return;
        }
        if (command.CommandHandle == 0)
        {
            int source = command.MaskedArguments?.ResourceHandle ?? argumentHandle;
            command.CommandHandle = _renderer.PrepareCommands(DescribeDraw(bindings, source, drawCount,
                countBuffer is null ? stride : 20, countBuffer is null ? offset : 0));
        }
        (var viewport, var scissor) = _renderer.ResolveEngineDrawArea();
        Span<uint> offsets = stackalloc uint[16];
        int uniformCount = _program.SnapshotUniforms(offsets);
        command.MaskedArguments?.Record();
        _renderer.RecordEngineCommands(command.CommandHandle, offsets[..uniformCount], viewport: viewport, scissor: scissor);
        if (arguments is WebGpuOwnedStorageBuffer nativeArguments) nativeArguments.MarkRecorded();
        bindings.MarkRecorded();
        if (scissor is not ({ Width: 0 } or { Height: 0 }))
        {
            _frameBuffer?.MarkRecorded();
            _renderer.CountEngineMeshDraw();
        }
    }

    private bool IndirectDependsOn(AbstractRenderAPIObject resource)
    {
        foreach (IndirectCommandKey key in _indirectCommands.Keys)
            if (ReferenceEquals(key.Arguments, resource) || ReferenceEquals(key.Count, resource) ||
                key.Bindings.DependsOn(resource)) return true;
        return false;
    }

    private void ReleaseIndirectCommandsUsing(AbstractRenderAPIObject? resource = null, int handle = 0,
        WebGpuBindingSet? bindings = null)
    {
        List<IndirectCommandKey>? obsolete = null;
        foreach ((IndirectCommandKey key, WebGpuIndirectDrawCommand command) in _indirectCommands)
        {
            bool remove = resource is null && bindings is null ||
                bindings is not null && ReferenceEquals(key.Bindings, bindings) ||
                resource is not null && (key.Bindings.UsesHandle(resource, handle) ||
                    ReferenceEquals(key.Arguments, resource) && key.ArgumentHandle == handle ||
                    ReferenceEquals(key.Count, resource) && key.CountHandle == handle);
            if (!remove) continue;
            command.Dispose();
            (obsolete ??= []).Add(key);
        }
        if (obsolete is not null)
            foreach (IndirectCommandKey key in obsolete) _indirectCommands.Remove(key);
    }
}
