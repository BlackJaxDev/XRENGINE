namespace XREngine.Rendering.WebGPU;

/// <summary>Retains an immutable, fully resolved program binding set and its engine resource owners.</summary>
internal sealed class WebGpuBindingSet : IDisposable
{
    private readonly WebGpuRendererHost _renderer;
    private readonly WebGpuRenderProgram _program;
    private readonly int[] _resources;
    private readonly uint[] _sizes;
    private readonly AbstractRenderAPIObject?[] _owners;
    private readonly int[] _groups;
    private bool _disposed;

    public WebGpuBindingSet(WebGpuRendererHost renderer, WebGpuRenderProgram program, ReadOnlySpan<int> resources,
        ReadOnlySpan<uint> sizes,
        ReadOnlySpan<AbstractRenderAPIObject?> owners, int[] groups)
    {
        _renderer = renderer;
        _program = program;
        _resources = resources.ToArray();
        _sizes = sizes.ToArray();
        _owners = owners.ToArray();
        _groups = groups;
    }

    public ReadOnlySpan<int> GroupHandles => _groups;
    public bool IsDisposed => _disposed;

    public bool Matches(ReadOnlySpan<int> resources, ReadOnlySpan<uint> sizes)
        => !_disposed && resources.SequenceEqual(_resources) && sizes.SequenceEqual(_sizes);

    public bool SameResourcesWithDifferentSizes(ReadOnlySpan<int> resources, ReadOnlySpan<uint> sizes)
        => !_disposed && resources.SequenceEqual(_resources) && !sizes.SequenceEqual(_sizes);

    public bool DependsOn(AbstractRenderAPIObject resource)
    {
        foreach (AbstractRenderAPIObject? owner in _owners)
            if (ReferenceEquals(owner, resource)) return true;
        return false;
    }

    public bool UsesHandle(AbstractRenderAPIObject resource, int handle)
    {
        for (int index = 0; index < _resources.Length; index++)
            if (ReferenceEquals(_owners[index], resource) && _resources[index] == handle)
                return true;
        return false;
    }

    public void MarkRecorded()
    {
        foreach (AbstractRenderAPIObject? owner in _owners)
            switch (owner)
            {
                case WebGpuTexture2D texture: texture.MarkRecorded(); break;
                case WebGpuTexture2DArray array: array.MarkRecorded(); break;
                case WebGpuTextureCube cube: cube.MarkRecorded(); break;
            }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _renderer.ReleaseEngineMeshCommandsUsingBindingSet(_program, this);
        _program.ReleaseComputeCommandsUsing(this);
        foreach (int group in _groups)
            if (group != 0) _renderer.RetireEngineResourceAfterFrame(group);
    }
}
