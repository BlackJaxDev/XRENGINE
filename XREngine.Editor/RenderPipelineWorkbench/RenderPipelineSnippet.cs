using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// A group of commands that the command palette inserts as one step. A snippet can also add the resource
/// declarations and scene passes that its commands need.
/// </summary>
/// <param name="Name">The name that the palette shows.</param>
/// <param name="Description">What the snippet adds.</param>
/// <param name="Build">Adds declarations to the state when needed and returns the commands to insert, in order.</param>
public sealed record RenderPipelineSnippet(
    string Name,
    string Description,
    Func<RenderPipelineDraftState, List<ViewportRenderCommand>> Build);
