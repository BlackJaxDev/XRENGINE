using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.PostProcessing;
using XREngine.Rendering.Resources;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering;

/// <summary>
/// Cold, output-specific declarations used by publication and runtime admission. Describing
/// requirements must not execute commands, invoke resource factories, or allocate API objects.
/// </summary>
public sealed class RenderPipelineRequirements
{
    private readonly HashSet<ViewportRenderCommandContainer> _containers = new(ReferenceEqualityComparer.Instance);

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
