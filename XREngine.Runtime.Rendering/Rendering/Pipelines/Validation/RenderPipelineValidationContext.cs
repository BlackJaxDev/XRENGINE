using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.PostProcessing;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.RenderGraph.Diagnostics;
using XREngine.Rendering.Resources;
using XREngine.Rendering.Resources.Diagnostics;

namespace XREngine.Rendering.Pipelines.Validation;

/// <summary>
/// The read-only inputs of one validator run. Tools build a context from a live pipeline, from cached Workbench data, or
/// from a draft. Rules read the context and never change the pipeline or its resource generation.
/// </summary>
public sealed class RenderPipelineValidationContext
{
    private Dictionary<string, List<ViewportRenderCommand>>? _stageConsumers;

    /// <summary>Initializes a context from explicit data.</summary>
    public RenderPipelineValidationContext(
        RenderPipeline? pipeline,
        XRRenderPipelineInstance? instance,
        IReadOnlyCollection<RenderPassMetadata> passes,
        RenderPipelineStructureSnapshot structure,
        RenderPipelineResourceSnapshot resources,
        RenderPipelinePostProcessSchema schema,
        RendererBackendId backend,
        RenderPipelineResourceProfile profile,
        IReadOnlyCollection<int>? registeredRenderPasses,
        IReadOnlyList<RenderPipelineProblem>? sourceProblems = null)
    {
        Pipeline = pipeline;
        Instance = instance;
        Passes = passes;
        Structure = structure;
        Resources = resources;
        Schema = schema;
        Backend = backend;
        Profile = profile;
        RegisteredRenderPasses = registeredRenderPasses;
        SourceProblems = sourceProblems ?? [];
    }

    /// <summary>Gets the pipeline, or null for a context that tests build from data.</summary>
    public RenderPipeline? Pipeline { get; }

    /// <summary>Gets the selected instance, or null.</summary>
    public XRRenderPipelineInstance? Instance { get; }

    /// <summary>Gets the pass metadata.</summary>
    public IReadOnlyCollection<RenderPassMetadata> Passes { get; }

    /// <summary>Gets the command-to-pass map.</summary>
    public RenderPipelineStructureSnapshot Structure { get; }

    /// <summary>Gets the resource table.</summary>
    public RenderPipelineResourceSnapshot Resources { get; }

    /// <summary>Gets the post-process schema.</summary>
    public RenderPipelinePostProcessSchema Schema { get; }

    /// <summary>Gets the backend that rules check support for.</summary>
    public RendererBackendId Backend { get; }

    /// <summary>Gets the resource profile of the layout.</summary>
    public RenderPipelineResourceProfile Profile { get; }

    /// <summary>Gets the scene render passes that the pipeline registers, or null when unknown.</summary>
    public IReadOnlyCollection<int>? RegisteredRenderPasses { get; }

    /// <summary>
    /// Gets the problems that sources outside the rules reported, such as requirement diagnostics, layout build errors,
    /// and generation failures.
    /// </summary>
    public IReadOnlyList<RenderPipelineProblem> SourceProblems { get; }

    /// <summary>Returns the commands that read each schema stage key. The map is built on the first call.</summary>
    public IReadOnlyDictionary<string, List<ViewportRenderCommand>> GetStageConsumers()
    {
        if (_stageConsumers is not null)
            return _stageConsumers;

        Dictionary<string, List<ViewportRenderCommand>> consumers = new(StringComparer.Ordinal);
        List<string> scratch = [];
        for (int i = 0; i < Structure.Commands.Count; i++)
        {
            ViewportRenderCommand command = Structure.Commands[i].Command;
            scratch.Clear();
            RenderCommandInfo.CollectConsumedStages(command, scratch);
            for (int s = 0; s < scratch.Count; s++)
            {
                if (!consumers.TryGetValue(scratch[s], out List<ViewportRenderCommand>? list))
                {
                    list = [];
                    consumers.Add(scratch[s], list);
                }

                list.Add(command);
            }
        }

        return _stageConsumers = consumers;
    }

    /// <summary>
    /// Builds a context from a live pipeline. The context uses the active generation of <paramref name="instance"/> when
    /// it has one. Otherwise it builds a new layout for a default profile, so the live generation does not change.
    /// </summary>
    public static RenderPipelineValidationContext FromPipeline(RenderPipeline pipeline, XRRenderPipelineInstance? instance, RendererBackendId backend)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        List<RenderPipelineProblem> sources = [];
        RenderResourceGeneration? generation = instance?.ActiveGeneration;
        IReadOnlyCollection<RenderPassMetadata> passes = generation is { PassMetadata.Count: > 0 } ? generation.PassMetadata : pipeline.PassMetadata;
        RenderPipelineStructureSnapshot structure = RenderPipelineStructureSnapshot.Build(pipeline.CommandGeneration, pipeline.CommandChain, passes);

        RenderPipelineResourceProfile profile = generation?.Layout.Profile ?? DefaultProfile;
        RenderPipelineResourceSnapshot resources;
        if (generation is not null)
        {
            resources = RenderPipelineResourceSnapshot.Build(generation.Layout, passes, generation);
        }
        else
        {
            try
            {
                resources = RenderPipelineResourceSnapshot.Build(pipeline.BuildResourceLayout(profile), passes, null);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                resources = RenderPipelineResourceSnapshot.Empty;
                sources.Add(new RenderPipelineProblem("source.layout", ERenderPipelineProblemSeverity.Error, string.Concat("The resource layout failed to build: ", ex.Message)));
            }
        }

        return FromSnapshots(pipeline, instance, passes, structure, resources, backend, sources);
    }

    /// <summary>
    /// Builds a context from snapshots that the caller already has, such as the cached model of an editor view. The
    /// method adds the problems that the pipeline sources report: requirement diagnostics, the last resource
    /// generation failure, and the last render decline of <paramref name="instance"/>.
    /// </summary>
    /// <param name="pipeline">The pipeline to check.</param>
    /// <param name="instance">The instance whose failures to include, or null.</param>
    /// <param name="passes">The pass metadata that <paramref name="structure"/> and <paramref name="resources"/> use.</param>
    /// <param name="structure">The command structure of the pipeline.</param>
    /// <param name="resources">The resource snapshot. Its layout profile becomes the validation profile.</param>
    /// <param name="backend">The backend to check against.</param>
    /// <param name="sources">Problems that the caller found before, or null.</param>
    public static RenderPipelineValidationContext FromSnapshots(
        RenderPipeline pipeline,
        XRRenderPipelineInstance? instance,
        IReadOnlyCollection<RenderPassMetadata> passes,
        RenderPipelineStructureSnapshot structure,
        RenderPipelineResourceSnapshot resources,
        RendererBackendId backend,
        List<RenderPipelineProblem>? sources = null)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        sources ??= [];
        CollectRequirementDiagnostics(pipeline, backend, sources);
        if (instance?.LastResourceGenerationFailure is { Length: > 0 } failure)
            sources.Add(new RenderPipelineProblem("source.generation", ERenderPipelineProblemSeverity.Error, string.Concat("The resource generation failed to commit: ", failure)));
        if (instance?.LastRenderDeclineReason is { Length: > 0 } decline)
            sources.Add(new RenderPipelineProblem("source.admission", ERenderPipelineProblemSeverity.Warning, string.Concat("The instance declined to render: ", decline)));

        RenderPipelineResourceProfile profile = ReferenceEquals(resources.Layout, RenderPipelineResourceLayout.Empty) ? DefaultProfile : resources.Layout.Profile;
        IReadOnlyCollection<int>? registered = pipeline.PassIndicesAndSorters?.Keys;
        return new RenderPipelineValidationContext(
            pipeline,
            instance,
            passes,
            structure,
            resources,
            pipeline.PostProcessSchema,
            backend,
            profile,
            registered,
            sources);
    }

    /// <summary>Gets the profile that validation uses when no generation exists: 1920x1080, mono, SDR.</summary>
    public static RenderPipelineResourceProfile DefaultProfile { get; } = RenderPipelineResourceProfile.Empty with
    {
        DisplayWidth = 1920u,
        DisplayHeight = 1080u,
        InternalWidth = 1920u,
        InternalHeight = 1080u,
    };

    private static void CollectRequirementDiagnostics(RenderPipeline pipeline, RendererBackendId backend, List<RenderPipelineProblem> sources)
    {
        RenderPipelineRequirements requirements;
        try
        {
            requirements = pipeline.CreateRequirements(backend);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
        {
            sources.Add(new RenderPipelineProblem("source.requirements", ERenderPipelineProblemSeverity.Error, string.Concat("The pipeline requirements failed: ", ex.Message)));
            return;
        }

        for (int i = 0; i < requirements.Diagnostics.Count; i++)
            sources.Add(new RenderPipelineProblem("source.requirements", ERenderPipelineProblemSeverity.Warning, requirements.Diagnostics[i]));
    }
}
