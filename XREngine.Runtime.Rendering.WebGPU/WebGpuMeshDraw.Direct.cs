namespace XREngine.Rendering.WebGPU;

internal sealed partial class WebGpuMeshDraw
{
    private readonly record struct DirectCommandKey(WebGpuBindingSet Bindings, uint Vertices, uint FirstVertex, uint Instances);
    private readonly Dictionary<DirectCommandKey, int> _directCommands = [];

    internal void RecordVertexlessDirect(WebGpuBindingSet bindings, uint vertices, uint firstVertex, uint instances)
    {
        if (!IsReady || _indices is not null || !_program.Artifact.VertexBuffers.IsDefaultOrEmpty)
            throw Unsupported("direct storage-pulled draws require a ready vertexless pipeline");
        if (bindings.IsDisposed)
        {
            _renderer.MarkEngineDrawPending();
            return;
        }
        DirectCommandKey key = new(bindings, vertices, firstVertex, instances);
        if (!_directCommands.TryGetValue(key, out int command))
        {
            int capacity = bindings.IsNativeRaster ? WebGpuAdvancedVisibilityFrame.MaximumRetainedDirectCommands : 4096;
            if (_directCommands.Count >= capacity)
                throw Unsupported($"vertexless direct draws exceed {capacity} retained binding/count variants");
            command = _renderer.PrepareEngineCommands(this, DescribeDraw(bindings,
                directVertices: vertices, directFirstVertex: firstVertex, directInstances: instances));
            _directCommands.Add(key, command);
        }
        (var viewport, var scissor) = _renderer.ResolveEngineDrawArea();
        Span<uint> offsets = stackalloc uint[16];
        int count = _program.SnapshotUniforms(offsets);
        _renderer.RecordEngineCommands(command, offsets[..count], viewport: viewport, scissor: scissor);
        _renderer.MarkEngineViewHistoryDrawWrite(_frameBuffer, in _output,
            _program.Artifact.FragmentEntryPoint is not null, _state.ColorWriteMask, vertices, instances, scissor);
        bindings.MarkRecorded();
        if (scissor is not ({ Width: 0 } or { Height: 0 }))
        {
            _frameBuffer?.MarkRecorded();
            _renderer.CountEngineMeshDraw();
        }
    }

    private bool DirectDependsOn(AbstractRenderAPIObject resource)
    {
        foreach (DirectCommandKey key in _directCommands.Keys)
            if (key.Bindings.DependsOn(resource)) return true;
        return false;
    }

    private void ReleaseDirectCommandsUsing(AbstractRenderAPIObject? resource = null, int handle = 0, WebGpuBindingSet? bindings = null)
    {
        List<DirectCommandKey>? removed = null;
        foreach ((DirectCommandKey key, int command) in _directCommands)
        {
            if (resource is not null ? !key.Bindings.UsesHandle(resource, handle) :
                bindings is not null && !ReferenceEquals(bindings, key.Bindings)) continue;
            _renderer.RetireEngineResourceAfterFrame(command);
            (removed ??= []).Add(key);
        }
        if (removed is not null)
            foreach (DirectCommandKey key in removed) _directCommands.Remove(key);
    }
}
