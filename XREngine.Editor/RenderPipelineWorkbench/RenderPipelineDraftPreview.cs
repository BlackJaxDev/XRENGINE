using XREngine.Rendering;
using XREngine.Rendering.Pipelines.Validation;
using XREngine.Rendering.PostProcessing;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.RenderGraph.Diagnostics;
using XREngine.Rendering.Resources;
using XREngine.Rendering.Resources.Diagnostics;
using XREngine.Rendering.Resources.Authoring;

namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// Pass metadata, structure, resource layout, and schema that a draft state describes, built without a live
/// generation. The Workbench views show it while a draft is open, and the draft validator checks it.
/// </summary>
public sealed class RenderPipelineDraftPreview
{
    private RenderPipelineDraftPreview(
        int revision,
        IReadOnlyCollection<RenderPassMetadata> passes,
        RenderPipelineStructureSnapshot structure,
        RenderPipelineResourceLayout layout,
        RenderPipelineResourceSnapshot resources,
        RenderPipelinePostProcessSchema schema,
        int[] registeredPasses,
        List<RenderPipelineProblem> buildProblems)
    {
        Revision = revision;
        Passes = passes;
        Structure = structure;
        Layout = layout;
        Resources = resources;
        Schema = schema;
        RegisteredPasses = registeredPasses;
        BuildProblems = buildProblems;
    }

    /// <summary>Gets the draft revision that this preview describes.</summary>
    public int Revision { get; }

    /// <summary>Gets the pass metadata of the draft commands.</summary>
    public IReadOnlyCollection<RenderPassMetadata> Passes { get; }

    /// <summary>Gets the command structure of the draft.</summary>
    public RenderPipelineStructureSnapshot Structure { get; }

    /// <summary>Gets the layout of the draft resource declarations.</summary>
    public RenderPipelineResourceLayout Layout { get; }

    /// <summary>Gets the resource snapshot of the draft.</summary>
    public RenderPipelineResourceSnapshot Resources { get; }

    /// <summary>Gets the schema of the draft stage declarations.</summary>
    public RenderPipelinePostProcessSchema Schema { get; }

    /// <summary>Gets the scene pass indices of the draft.</summary>
    public IReadOnlyCollection<int> RegisteredPasses { get; }

    /// <summary>Gets the problems that occurred while the preview was built, such as a layout that failed.</summary>
    public IReadOnlyList<RenderPipelineProblem> BuildProblems { get; }

    /// <summary>
    /// Builds a preview of <paramref name="state"/> for <paramref name="profile"/>. The method reads the state and does
    /// not change it. A part that fails becomes an error problem and an empty part.
    /// </summary>
    public static RenderPipelineDraftPreview Build(RenderPipelineDraftState state, int revision, RenderPipelineResourceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(state);
        List<RenderPipelineProblem> problems = [];

        IReadOnlyCollection<RenderPassMetadata> passes;
        try
        {
            RenderPassMetadataCollection collection = new();
            state.Commands.BuildRenderPassMetadata(collection);
            AuthoredPassDependency.Apply(collection, state.PassDependencies);
            passes = collection.Build();
        }
        catch (Exception ex)
        {
            passes = [];
            problems.Add(new RenderPipelineProblem("draft.passes", ERenderPipelineProblemSeverity.Error, string.Concat("The draft pass metadata failed to build: ", ex.Message)));
        }

        RenderPipelineStructureSnapshot structure = RenderPipelineStructureSnapshot.Build(ToCommandGeneration(revision), state.Commands, passes);

        RenderPipelineResourceLayout layout;
        try
        {
            layout = AuthoredRenderResourceFactory.BuildPreviewLayout(profile, state.Resources);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            layout = RenderPipelineResourceLayout.Empty;
            problems.Add(new RenderPipelineProblem("draft.layout", ERenderPipelineProblemSeverity.Error, string.Concat("The draft resource layout failed to build: ", ex.Message)));
        }

        RenderPipelinePostProcessSchema schema;
        try
        {
            schema = AuthoredPostProcessSchema.BuildPreview(state.Stages);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            schema = RenderPipelinePostProcessSchema.Empty;
            problems.Add(new RenderPipelineProblem("draft.schema", ERenderPipelineProblemSeverity.Error, string.Concat("The draft schema failed to build: ", ex.Message)));
        }

        RenderPipelineResourceSnapshot resources = RenderPipelineResourceSnapshot.Build(layout, passes, null);
        return new RenderPipelineDraftPreview(revision, passes, structure, layout, resources, schema, [.. state.RenderPasses.Keys], problems);
    }

    /// <summary>
    /// Returns the command generation that the views use for a draft revision. Draft generations use the high bit,
    /// so a draft generation never equals a live generation.
    /// </summary>
    public static ulong ToCommandGeneration(int revision)
        => (1UL << 63) | (uint)revision;

    /// <summary>Runs the validator on this preview.</summary>
    /// <param name="pipeline">The pipeline that the draft edits. Rules read its branch settings.</param>
    /// <param name="backend">The backend to check against.</param>
    /// <param name="profile">The resource profile of the preview.</param>
    public RenderPipelineValidationReport Validate(RenderPipeline? pipeline, RendererBackendId backend, RenderPipelineResourceProfile profile)
    {
        RenderPipelineValidationContext context = new(
            pipeline,
            null,
            Passes,
            Structure,
            Resources,
            Schema,
            backend,
            profile,
            RegisteredPasses,
            BuildProblems);
        return RenderPipelineValidator.Validate(context);
    }
}
