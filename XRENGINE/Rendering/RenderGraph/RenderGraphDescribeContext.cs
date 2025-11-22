using System.Collections.Generic;

namespace XREngine.Rendering.RenderGraph;

/// <summary>
/// Carries state while walking the viewport command list to produce render-graph metadata.
/// Tracks currently bound render targets so later commands can attribute their reads/writes.
/// </summary>
public sealed class RenderGraphDescribeContext
{
    private readonly Stack<RenderTargetBinding> _targetStack = new();
    private readonly Dictionary<string, int> _syntheticPassIndices = new();
    private int _nextSyntheticPassIndex = 100000;

    public RenderGraphDescribeContext(RenderPassMetadataCollection metadata)
    {
        Metadata = metadata;
    }

    public RenderPassMetadataCollection Metadata { get; }

    public RenderTargetBinding? CurrentRenderTarget
        => _targetStack.Count > 0 ? _targetStack.Peek() : null;

    public void PushRenderTarget(string? name, bool writes, bool clearColor, bool clearDepth, bool clearStencil)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;
        _targetStack.Push(new RenderTargetBinding(name!, writes, clearColor && writes, clearDepth && writes, clearStencil && writes));
    }

    public void PopRenderTarget()
    {
        if (_targetStack.Count > 0)
            _targetStack.Pop();
    }

    public RenderPassBuilder GetOrCreateSyntheticPass(string key, RenderGraphPassStage stage = RenderGraphPassStage.Graphics)
    {
        if (string.IsNullOrWhiteSpace(key))
            key = $"SyntheticPass{_nextSyntheticPassIndex}";

        if (!_syntheticPassIndices.TryGetValue(key, out int passIndex))
        {
            passIndex = _nextSyntheticPassIndex++;
            _syntheticPassIndices[key] = passIndex;
        }

        return Metadata.ForPass(passIndex, key, stage);
    }
}

public sealed class RenderTargetBinding
{
    private bool _pendingColorClear;
    private bool _pendingDepthClear;
    private bool _pendingStencilClear;

    public RenderTargetBinding(string name, bool writes, bool clearColor, bool clearDepth, bool clearStencil)
    {
        Name = name;
        Writes = writes;
        _pendingColorClear = clearColor;
        _pendingDepthClear = clearDepth;
        _pendingStencilClear = clearStencil;
    }

    public string Name { get; }
    public bool Writes { get; }

    public RenderGraphAccess ColorAccess => Writes ? RenderGraphAccess.ReadWrite : RenderGraphAccess.Read;
    public RenderGraphAccess DepthAccess => Writes ? RenderGraphAccess.ReadWrite : RenderGraphAccess.Read;

    public RenderPassLoadOp ConsumeColorLoadOp()
    {
        var op = _pendingColorClear ? RenderPassLoadOp.Clear : RenderPassLoadOp.Load;
        _pendingColorClear = false;
        return op;
    }

    public RenderPassLoadOp ConsumeDepthLoadOp()
    {
        var op = _pendingDepthClear ? RenderPassLoadOp.Clear : RenderPassLoadOp.Load;
        _pendingDepthClear = false;
        return op;
    }

    public RenderPassLoadOp ConsumeStencilLoadOp()
    {
        var op = _pendingStencilClear ? RenderPassLoadOp.Clear : RenderPassLoadOp.Load;
        _pendingStencilClear = false;
        return op;
    }

    public RenderPassStoreOp GetColorStoreOp()
        => Writes ? RenderPassStoreOp.Store : RenderPassStoreOp.DontCare;

    public RenderPassStoreOp GetDepthStoreOp()
        => Writes ? RenderPassStoreOp.Store : RenderPassStoreOp.DontCare;

    public RenderPassStoreOp GetStencilStoreOp()
        => Writes ? RenderPassStoreOp.Store : RenderPassStoreOp.DontCare;
}
