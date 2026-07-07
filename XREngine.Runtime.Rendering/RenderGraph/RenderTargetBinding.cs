namespace XREngine.Rendering.RenderGraph;

/// <summary>
/// Represents a render target bound to a render pass, along with its load/store operations.
/// </summary>
/// <param name="name">The name of the render target resource.</param>
/// <param name="writes">Indicates whether this render target is written to by the render pass. If false, the render pass only reads from this target.</param>
/// <param name="clearColor">Indicates whether the color buffer of this render target should be cleared at the start of the render pass. This is only relevant if Writes is true.</param>
/// <param name="clearDepth">Indicates whether the depth buffer of this render target should be cleared at the start of the render pass. This is only relevant if Writes is true.</param>
/// <param name="clearStencil">Indicates whether the stencil buffer of this render target should be cleared at the start of the render pass. This is only relevant if Writes is true.</param>
/// <remarks>
/// This class is used internally by the render graph system to track the state of render targets across render passes. It is not intended to be used directly by application code.
/// </remarks>
public sealed class RenderTargetBinding(string name, bool writes, bool clearColor, bool clearDepth, bool clearStencil)
{
    private bool _pendingColorClear = clearColor;
    private bool _pendingDepthClear = clearDepth;
    private bool _pendingStencilClear = clearStencil;

    /// <summary>
    /// The name of the render target resource. This is used to look up the actual resource in the render graph.
    /// </summary>
    public string Name { get; } = name;
    /// <summary>
    /// Indicates whether this render target is written to by the render pass. If false, the render pass only reads from this target.
    /// </summary>
    public bool Writes { get; } = writes;

    /// <summary>
    /// Indicates whether the color buffer of this render target should be cleared at the start of the render pass. This is only relevant if Writes is true.
    /// </summary>
    public ERenderGraphAccess ColorAccess => Writes ? ERenderGraphAccess.ReadWrite : ERenderGraphAccess.Read;
    /// <summary>
    /// Indicates whether the depth buffer of this render target should be cleared at the start of the render pass. This is only relevant if Writes is true.
    /// </summary>
    public ERenderGraphAccess DepthAccess => Writes ? ERenderGraphAccess.ReadWrite : ERenderGraphAccess.Read;

    /// <summary>
    /// Indicates whether the stencil buffer of this render target should be cleared at the start of the render pass. This is only relevant if Writes is true.
    /// </summary>
    /// <returns>The load operation for the stencil buffer.</returns>
    public ERenderPassLoadOp ConsumeColorLoadOp()
    {
        var op = _pendingColorClear ? ERenderPassLoadOp.Clear : ERenderPassLoadOp.Load;
        _pendingColorClear = false;
        return op;
    }

    /// <summary>
    /// Indicates whether the depth buffer of this render target should be cleared at the start of the render pass. This is only relevant if Writes is true.
    /// </summary>
    /// <returns>The load operation for the depth buffer.</returns>
    public ERenderPassLoadOp ConsumeDepthLoadOp()
    {
        var op = _pendingDepthClear ? ERenderPassLoadOp.Clear : ERenderPassLoadOp.Load;
        _pendingDepthClear = false;
        return op;
    }

    /// <summary>
    /// Indicates whether the stencil buffer of this render target should be cleared at the start of the render pass. This is only relevant if Writes is true.
    /// </summary>
    /// <returns>The load operation for the stencil buffer.</returns>
    public ERenderPassLoadOp ConsumeStencilLoadOp()
    {
        var op = _pendingStencilClear ? ERenderPassLoadOp.Clear : ERenderPassLoadOp.Load;
        _pendingStencilClear = false;
        return op;
    }

    /// <summary>
    /// Indicates the store operation for the color buffer of this render target. If Writes is true, the color buffer will be stored; otherwise, it will be discarded.
    /// </summary>
    /// <returns>The store operation for the color buffer.</returns>
    public ERenderPassStoreOp GetColorStoreOp()
        => Writes ? ERenderPassStoreOp.Store : ERenderPassStoreOp.DontCare;

    /// <summary>
    /// Indicates the store operation for the depth buffer of this render target. If Writes is true, the depth buffer will be stored; otherwise, it will be discarded.
    /// </summary>
    /// <returns>The store operation for the depth buffer.</returns>
    public ERenderPassStoreOp GetDepthStoreOp()
        => Writes ? ERenderPassStoreOp.Store : ERenderPassStoreOp.DontCare;

    /// <summary>
    /// Indicates the store operation for the stencil buffer of this render target. If Writes is true, the stencil buffer will be stored; otherwise, it will be discarded.
    /// </summary>
    /// <returns>The store operation for the stencil buffer.</returns>
    public ERenderPassStoreOp GetStencilStoreOp()
        => Writes ? ERenderPassStoreOp.Store : ERenderPassStoreOp.DontCare;
}