using System.Collections.Generic;

namespace XREngine.Rendering.RenderGraph;

/// <summary>
/// Carries state while walking the viewport command list to produce render-graph metadata.
/// Tracks currently bound render targets so later commands can attribute their reads/writes.
/// </summary>
public sealed class RenderGraphDescribeContext(RenderPassMetadataCollection metadata)
{
    private readonly Stack<RenderTargetBinding> _targetStack = new();
    private readonly Dictionary<string, int> _syntheticPassIndices = new();
    private int _nextSyntheticPassIndex = 100000;

    public RenderPassMetadataCollection Metadata { get; } = metadata;

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

    public RenderPassBuilder GetOrCreateSyntheticPass(string key, ERenderGraphPassStage stage = ERenderGraphPassStage.Graphics)
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

public sealed class RenderTargetBinding(string name, bool writes, bool clearColor, bool clearDepth, bool clearStencil)
{
    private bool _pendingColorClear = clearColor;
    private bool _pendingDepthClear = clearDepth;
    private bool _pendingStencilClear = clearStencil;

    public string Name { get; } = name;
    public bool Writes { get; } = writes;

    public ERenderGraphAccess ColorAccess => Writes ? ERenderGraphAccess.ReadWrite : ERenderGraphAccess.Read;
    public ERenderGraphAccess DepthAccess => Writes ? ERenderGraphAccess.ReadWrite : ERenderGraphAccess.Read;

    public ERenderPassLoadOp ConsumeColorLoadOp()
    {
        var op = _pendingColorClear ? ERenderPassLoadOp.Clear : ERenderPassLoadOp.Load;
        _pendingColorClear = false;
        return op;
    }

    public ERenderPassLoadOp ConsumeDepthLoadOp()
    {
        var op = _pendingDepthClear ? ERenderPassLoadOp.Clear : ERenderPassLoadOp.Load;
        _pendingDepthClear = false;
        return op;
    }

    public ERenderPassLoadOp ConsumeStencilLoadOp()
    {
        var op = _pendingStencilClear ? ERenderPassLoadOp.Clear : ERenderPassLoadOp.Load;
        _pendingStencilClear = false;
        return op;
    }

    public ERenderPassStoreOp GetColorStoreOp()
        => Writes ? ERenderPassStoreOp.Store : ERenderPassStoreOp.DontCare;

    public ERenderPassStoreOp GetDepthStoreOp()
        => Writes ? ERenderPassStoreOp.Store : ERenderPassStoreOp.DontCare;

    public ERenderPassStoreOp GetStencilStoreOp()
        => Writes ? ERenderPassStoreOp.Store : ERenderPassStoreOp.DontCare;
}
