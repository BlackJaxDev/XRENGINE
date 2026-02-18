namespace XREngine.Rendering.RenderGraph;

/// <summary>
/// High-level stage classification for a render pass. Used to reason about which pipeline
/// domains (graphics/compute/transfer) the pass touches before backend compilation.
/// </summary>
public enum ERenderGraphPassStage
{
    Graphics,
    Compute,
    Transfer
}
