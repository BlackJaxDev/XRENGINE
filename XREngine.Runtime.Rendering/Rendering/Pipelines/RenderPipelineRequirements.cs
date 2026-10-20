using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.PostProcessing;
using XREngine.Rendering.Resources;
using XREngine.Rendering.Shaders.Compilation;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>
/// Cold, output-specific declarations used by publication and runtime admission. Describing
/// requirements must not execute commands, invoke resource factories, or allocate API objects.
/// </summary>
public sealed class RenderPipelineRequirements
{
    private readonly HashSet<ViewportRenderCommandContainer> _containers = new(ReferenceEqualityComparer.Instance);
    private readonly List<(RenderPipelineRequirementsDeclaration Declaration, uint State)> _authoredDecalDeclarations = [];
    private readonly List<(VPRC_Switch Command, Dictionary<int, ViewportRenderCommandContainer>? Source,
        Dictionary<int, ViewportRenderCommandContainer> Snapshot)> _switchCases = [];

    internal void ObserveSwitchCases(VPRC_Switch command)
        => _switchCases.Add((command, command.Cases, command.Cases is { } cases ? new(cases) : []));

    internal void ObserveAuthoredDecalDeclaration(RenderPipelineRequirementsDeclaration declaration)
        => _authoredDecalDeclarations.Add((declaration, declaration.AuthoredDecalConsumerState));

    internal bool CommandTopologyUnchanged
    {
        get
        {
            for (int index = 0; index < _switchCases.Count; index++)
            {
                var observed = _switchCases[index];
                Dictionary<int, ViewportRenderCommandContainer>? current = observed.Command.Cases;
                bool unchanged = ReferenceEquals(current, observed.Source) && (current?.Count ?? 0) == observed.Snapshot.Count;
                if (unchanged && current is not null)
                    foreach (var entry in current)
                        if (!observed.Snapshot.TryGetValue(entry.Key, out var container) || !ReferenceEquals(container, entry.Value))
                        { unchanged = false; break; }
                if (unchanged) continue;
                observed.Command.AttachCaseContainers();
                // Dictionary mutation has no setter event. Record the observed
                // topology before notifying, so reentrant discovery cannot repeat it.
                observed.Snapshot.Clear();
                if (current is not null)
                    foreach (var entry in current) observed.Snapshot.Add(entry.Key, entry.Value);
                _switchCases[index] = (observed.Command, current, observed.Snapshot);
                observed.Command.ParentPipeline?.NotifyCommandChainStructureChanged();
                return false;
            }
            return true;
        }
    }

    internal bool AuthoredDecalDeclarationsUnchanged
    {
        get
        {
            if (!CommandTopologyUnchanged) return false;
            for (int index = 0; index < _authoredDecalDeclarations.Count; index++)
            {
                var observed = _authoredDecalDeclarations[index];
                if (observed.State != observed.Declaration.AuthoredDecalConsumerState) return false;
            }
            return true;
        }
    }

    /// <summary>Preserves executable decal ownership in profiles with an optimized program closure.</summary>
    internal void IncludeAuthoredDecalConsumers(ViewportRenderCommandContainer commands)
    {
        RenderPipelineRequirements graph = new(Backend, PostProcessState, OutputProfile);
        graph.Include(commands);
        if (graph.Operations.Contains("native-authored-decals")) RequireOperation("native-authored-decals");
        if (graph.RasterScenePasses.Contains((int)EDefaultRenderPass.DeferredDecals))
            RequireRasterScenePass((int)EDefaultRenderPass.DeferredDecals);
        _authoredDecalDeclarations.AddRange(graph._authoredDecalDeclarations);
        _switchCases.AddRange(graph._switchCases);
    }

    public RenderPipelineRequirements(RendererBackendId backend, PipelinePostProcessState postProcessState)
        : this(backend, postProcessState, RenderPipelineResourceProfile.Empty) { }

    public RenderPipelineRequirements(RendererBackendId backend, PipelinePostProcessState postProcessState, in RenderPipelineResourceProfile outputProfile)
    {
        Backend = backend;
        PostProcessState = postProcessState;
        OutputProfile = outputProfile;
    }

    public RendererBackendId Backend { get; }
    public PipelinePostProcessState PostProcessState { get; }
    public RenderPipelineResourceProfile OutputProfile { get; }
    public HashSet<EAntiAliasingMode> SupportedAntiAliasingModes { get; } = [EAntiAliasingMode.None];
    public HashSet<string> Operations { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string?> Programs { get; } = new(StringComparer.Ordinal);
    public HashSet<string> RasterPrograms { get; } = new(StringComparer.Ordinal);
    public HashSet<string> ComputePrograms { get; } = new(StringComparer.Ordinal);
    public HashSet<int> ScenePasses { get; } = [];
    public HashSet<int> RasterScenePasses { get; } = [];
    /// <summary>Source passes replayed into depth/normal targets and requiring geometry-equivalent material variants.</summary>
    public HashSet<int> DepthNormalScenePasses { get; } = [];
    public bool HasConflictingAuthoredDecalConsumers
        => RasterScenePasses.Contains((int)EDefaultRenderPass.DeferredDecals) && Operations.Contains("native-authored-decals");
    /// <summary>Native geometry routes with optional explicitly authored strategy; null uses the packaged startup policy.</summary>
    public Dictionary<int, EMeshSubmissionStrategy?> NativeScenePasses { get; } = [];
    /// <summary>A selected native scene consumer requires explicit routes; logical stage markers do not.</summary>
    public bool RequiresNativeScenePasses { get; set; }
    public bool NativeProbeIbl { get; set; }
    public HashSet<XRMaterial> Materials { get; } = new(ReferenceEqualityComparer.Instance);
    public HashSet<XRRenderProgram> RenderPrograms { get; } = new(ReferenceEqualityComparer.Instance);
    public HashSet<XRRenderProgram> ComputeRenderPrograms { get; } = new(ReferenceEqualityComparer.Instance);
    public HashSet<string> ProgramIdentities { get; } = new(StringComparer.Ordinal);
    public List<string> Diagnostics { get; } = [];

    /// <summary>Declares an operation rather than a pipeline or command implementation type.</summary>
    public void RequireOperation(string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        Operations.Add(operation);
        if (operation == "native-authored-decals")
        {
            ScenePasses.Add((int)EDefaultRenderPass.DeferredDecals);
            if (RasterScenePasses.Contains((int)EDefaultRenderPass.DeferredDecals))
                Diagnostics.Add("The selected graph applies DeferredDecals through both a native authored-decal operation and a raster pass.");
        }
    }

    public void RequireRasterScenePass(int pass)
    {
        ScenePasses.Add(pass);
        RasterScenePasses.Add(pass);
        if (pass == (int)EDefaultRenderPass.DeferredDecals && Operations.Contains("native-authored-decals"))
            Diagnostics.Add("The selected graph applies DeferredDecals through both a native authored-decal operation and a raster pass.");
    }

    public void RequireDepthNormalScenePass(int pass)
    {
        ScenePasses.Add(pass);
        DepthNormalScenePasses.Add(pass);
    }

    /// <summary>Declares a package program, optionally pinned to an authored descriptor identity.</summary>
    public void RequireProgram(string pass, string? descriptorIdentity = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pass);
        descriptorIdentity = ShaderProgramArtifactCatalog.ValidateIdentity(descriptorIdentity);
        if (Programs.TryGetValue(pass, out string? existing) && existing is not null &&
            descriptorIdentity is not null && existing != descriptorIdentity)
            throw new InvalidOperationException($"RenderPipeline.ProgramIdentityConflict: '{pass}'.");
        Programs[pass] = descriptorIdentity ?? existing;
    }

    public void RequireMaterial(XRMaterial material)
    {
        ArgumentNullException.ThrowIfNull(material);
        Materials.Add(material);
    }

    /// <summary>Declares a native route without applying its restrictions to unrelated late or raster scene passes.</summary>
    public void RequireNativeScenePass(int pass, EMeshSubmissionStrategy? strategy = null)
    {
        RequireOperation("advanced-stage-execution");
        RequiresNativeScenePasses = true;
        ScenePasses.Add(pass);
        if (!NativeScenePasses.TryGetValue(pass, out EMeshSubmissionStrategy? existing) || existing is null)
            NativeScenePasses[pass] = strategy;
        else if (strategy is not null && strategy != existing)
            Diagnostics.Add($"Native scene pass '{pass}' declares conflicting submission strategies '{existing}' and '{strategy}'.");
    }

    public void RequireRasterProgram(string pass, string? descriptorIdentity = null)
    {
        if (ComputePrograms.Contains(pass))
            throw new InvalidOperationException($"RenderPipeline.ProgramShapeConflict: '{pass}'.");
        RequireProgram(pass, descriptorIdentity);
        RasterPrograms.Add(pass);
    }

    public void RequireComputeProgram(string pass, string? descriptorIdentity = null)
    {
        if (RasterPrograms.Contains(pass))
            throw new InvalidOperationException($"RenderPipeline.ProgramShapeConflict: '{pass}'.");
        RequireProgram(pass, descriptorIdentity);
        ComputePrograms.Add(pass);
    }

    public void RequireComputeProgram(XRRenderProgram program)
    {
        RequireProgram(program);
        ComputeRenderPrograms.Add(program);
    }

    public void RequireProgram(XRRenderProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        RenderPrograms.Add(program);
    }

    public void RequireProgramIdentity(string identity)
        => ProgramIdentities.Add(ShaderProgramArtifactCatalog.ValidateIdentity(identity)
            ?? throw new ArgumentException("A cooked program identity is required.", nameof(identity)));

    /// <summary>Visits every declared branch without executing its selection callbacks.</summary>
    public void Include(ViewportRenderCommandContainer? commands)
    {
        if (commands is null || !_containers.Add(commands))
            return;
        if (_containers.Count > 1024)
            throw new InvalidOperationException("RenderPipeline.CommandDeclarationBudgetExceeded.");
        for (int index = 0; index < commands.Count; index++)
        {
            commands[index].DescribeRequirements(this);
            commands[index].DeclaredRequirements?.ApplyTo(this);
        }
    }
}
