namespace XREngine.Rendering.WebGPU;

/// <summary>Retains an immutable, fully resolved program binding set and its engine resource owners.</summary>
internal sealed class WebGpuBindingSet : IDisposable
{
    private readonly WebGpuRendererHost _renderer;
    private readonly int[] _resources;
    private readonly AbstractRenderAPIObject?[] _owners;
    private readonly int[] _groups;
    private bool _disposed;

    public WebGpuBindingSet(WebGpuRendererHost renderer, ReadOnlySpan<int> resources,
        ReadOnlySpan<AbstractRenderAPIObject?> owners, int[] groups)
    {
        _renderer = renderer;
        _resources = resources.ToArray();
        _owners = owners.ToArray();
        _groups = groups;
    }

    public ReadOnlySpan<int> GroupHandles => _groups;
    public bool IsDisposed => _disposed;

    public bool Matches(ReadOnlySpan<int> resources)
        => !_disposed && resources.SequenceEqual(_resources);

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
            if (owner is WebGpuTexture2D texture) texture.MarkRecorded();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (int group in _groups)
            if (group != 0) _renderer.RetireEngineResourceAfterFrame(group);
    }
}
