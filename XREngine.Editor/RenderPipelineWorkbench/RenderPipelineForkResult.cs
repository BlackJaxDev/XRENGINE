using XREngine.Rendering;

namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>The result of <see cref="RenderPipelineFork.Create"/>.</summary>
/// <param name="Pipeline">The new editable pipeline.</param>
/// <param name="Unconverted">One line for each item that the fork could not convert. The user fixes these by hand.</param>
public sealed record RenderPipelineForkResult(CustomRenderPipeline Pipeline, IReadOnlyList<string> Unconverted);
