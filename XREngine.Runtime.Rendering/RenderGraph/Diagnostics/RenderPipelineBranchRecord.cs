using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering.RenderGraph.Diagnostics;

/// <summary>
/// The child container that a branch command executed, and the render frame in which it executed.
/// </summary>
/// <param name="Container">The executed child container, or null when the command executed no child.</param>
/// <param name="FrameId">The render frame identifier from <c>RuntimeEngine.Rendering.State.RenderFrameId</c>.</param>
public readonly record struct RenderPipelineBranchRecord(ViewportRenderCommandContainer? Container, ulong FrameId);
