using XREngine.Rendering;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.RenderGraph.Diagnostics;
using XREngine.Rendering.Resources;

namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// The values that decide when each part of a <see cref="RenderPipelineWorkbenchModel"/> rebuilds.
/// </summary>
/// <param name="Pipeline">The pipeline asset, or null in tests.</param>
/// <param name="Instance">The selected pipeline instance, or null.</param>
/// <param name="Root">The root command container.</param>
/// <param name="CommandGeneration">The command generation of the pipeline.</param>
/// <param name="Passes">The pass metadata that the views show.</param>
/// <param name="PassRevision">The mutation stamp of <paramref name="Passes"/>.</param>
/// <param name="Generation">The active resource generation of the instance, or null.</param>
/// <param name="Structure">A prebuilt structure snapshot for the same inputs, or null to build one.</param>
public readonly record struct RenderPipelineWorkbenchInputs(
    RenderPipeline? Pipeline,
    XRRenderPipelineInstance? Instance,
    ViewportRenderCommandContainer? Root,
    ulong CommandGeneration,
    IReadOnlyCollection<RenderPassMetadata> Passes,
    int PassRevision,
    RenderResourceGeneration? Generation,
    RenderPipelineStructureSnapshot? Structure = null)
{
    /// <summary>
    /// Reads the inputs from a pipeline and an optional instance. The instance's active generation owns
    /// the pass metadata for its resource profile, so the views prefer it over the asset-level metadata.
    /// </summary>
    public static RenderPipelineWorkbenchInputs From(RenderPipeline pipeline, XRRenderPipelineInstance? instance)
    {
        RenderResourceGeneration? generation = instance?.ActiveGeneration;
        IReadOnlyCollection<RenderPassMetadata> passes = pipeline.PassMetadata;
        RenderPipelineStructureSnapshot? structure = null;
        if (generation is { PassMetadata.Count: > 0 })
            passes = generation.PassMetadata;
        else
            structure = pipeline.StructureSnapshot;

        int revision = passes is RenderPassMetadataSnapshot snapshot ? snapshot.RevisionStamp : 0;
        return new RenderPipelineWorkbenchInputs(
            pipeline,
            instance,
            pipeline.CommandChain,
            pipeline.CommandGeneration,
            passes,
            revision,
            generation,
            structure);
    }
}
