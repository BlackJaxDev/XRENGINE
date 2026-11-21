using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering.Pipelines.Overlays;

/// <summary>
/// The result of the last overlay apply on a pipeline: one result for each patch, the commands that the patches made
/// or changed, and the base items that they removed. The Workbench reads it for the layered display, the Patches tab,
/// and the rebase report.
/// </summary>
public sealed class RenderPipelineOverlayReport
{
    /// <summary>An empty report for a pipeline with no overlays.</summary>
    public static RenderPipelineOverlayReport Empty { get; } = new([], [], [], true, true, true, new(ReferenceEqualityComparer.Instance), []);

    internal RenderPipelineOverlayReport(
        IReadOnlyList<RenderPipelinePatchResult> commandResults,
        IReadOnlyList<RenderPipelinePatchResult> resourceResults,
        IReadOnlyList<RenderPipelinePatchResult> schemaResults,
        bool commandsApplied,
        bool resourcesApplied,
        bool schemaApplied,
        Dictionary<ViewportRenderCommand, ERenderCommandPatchKind> markers,
        IReadOnlyList<string> removedItems)
    {
        CommandResults = commandResults;
        ResourceResults = resourceResults;
        SchemaResults = schemaResults;
        CommandsApplied = commandsApplied;
        ResourcesApplied = resourcesApplied;
        SchemaApplied = schemaApplied;
        Markers = markers;
        RemovedItems = removedItems;
    }

    /// <summary>Gets the results of the command patches of the last chain build.</summary>
    public IReadOnlyList<RenderPipelinePatchResult> CommandResults { get; }

    /// <summary>Gets the results of the resource patches of the last layout build.</summary>
    public IReadOnlyList<RenderPipelinePatchResult> ResourceResults { get; }

    /// <summary>Gets the results of the schema patches of the last schema build.</summary>
    public IReadOnlyList<RenderPipelinePatchResult> SchemaResults { get; }

    /// <summary>Gets whether the command patches applied. When false, the chain is the base chain.</summary>
    public bool CommandsApplied { get; }

    /// <summary>Gets whether the resource patches applied. When false, the layout is the base layout.</summary>
    public bool ResourcesApplied { get; }

    /// <summary>Gets whether the schema patches applied. When false, the schema is the base schema.</summary>
    public bool SchemaApplied { get; }

    /// <summary>Gets the patch kind that made or changed each command in the current chain.</summary>
    public IReadOnlyDictionary<ViewportRenderCommand, ERenderCommandPatchKind> Markers { get; }

    /// <summary>Gets a text for each base command that a patch removed or replaced.</summary>
    public IReadOnlyList<string> RemovedItems { get; }

    /// <summary>Gets whether a patch failed, so that an overlay part did not apply.</summary>
    public bool HasErrors => !CommandsApplied || !ResourcesApplied || !SchemaApplied;

    internal RenderPipelineOverlayReport WithCommands(
        IReadOnlyList<RenderPipelinePatchResult> results,
        bool applied,
        Dictionary<ViewportRenderCommand, ERenderCommandPatchKind> markers,
        IReadOnlyList<string> removedItems)
        => new(results, ResourceResults, SchemaResults, applied, ResourcesApplied, SchemaApplied, markers, removedItems);

    internal RenderPipelineOverlayReport WithResources(IReadOnlyList<RenderPipelinePatchResult> results, bool applied)
        => new(CommandResults, results, SchemaResults, CommandsApplied, applied, SchemaApplied, (Dictionary<ViewportRenderCommand, ERenderCommandPatchKind>)Markers, RemovedItems);

    internal RenderPipelineOverlayReport WithSchema(IReadOnlyList<RenderPipelinePatchResult> results, bool applied)
        => new(CommandResults, ResourceResults, results, CommandsApplied, ResourcesApplied, applied, (Dictionary<ViewportRenderCommand, ERenderCommandPatchKind>)Markers, RemovedItems);
}
