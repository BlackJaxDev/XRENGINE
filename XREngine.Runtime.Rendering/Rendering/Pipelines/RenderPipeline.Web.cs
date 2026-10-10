using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using XREngine.Rendering.PostProcessing;
using XREngine.Rendering.Resources;
using XREngine.Rendering.Shaders.Compilation;
using YamlDotNet.Serialization;

namespace XREngine.Rendering;

public abstract partial class RenderPipeline
{
    private WebPipelineArtifactCatalog? _webPipelineArtifacts;
    private bool _webOutputPrepared;
    private RenderPipelineRequirementsDeclaration? _declaredRequirements;

    /// <summary>Authored dependencies of resource factories and other opaque pipeline behavior.</summary>
    public RenderPipelineRequirementsDeclaration? DeclaredRequirements
    {
        get => _declaredRequirements;
        set => SetField(ref _declaredRequirements, value);
    }

    /// <summary>Immutable, verified package programs available to this authored pipeline.</summary>
    [Browsable(false), YamlIgnore]
    public WebPipelineArtifactCatalog? WebPipelineArtifacts
    {
        get => _webPipelineArtifacts;
        init => BindWebPipelineArtifacts(value);
    }

    /// <summary>Binds package programs without replacing the source asset or changing settings.</summary>
    public void BindWebPipelineArtifacts(WebPipelineArtifactCatalog? artifacts)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        if (_webPipelineArtifacts is { } installed)
        {
            if (!ReferenceEquals(installed, artifacts) && !installed.HasSameIdentities(artifacts))
                throw new InvalidOperationException("WebGPU.Pipeline.ArtifactIdentitiesChanged: replace the pipeline asset to install different pass programs.");
            return;
        }
        OnBindingWebPipelineArtifacts(artifacts);
        SetField(ref _webPipelineArtifacts, artifacts, nameof(WebPipelineArtifacts));
    }

    protected virtual void OnBindingWebPipelineArtifacts(WebPipelineArtifactCatalog artifacts) { }

    public virtual bool TryGetWebPipelineArtifact(string pass, [NotNullWhen(true)] out ShaderProgramArtifact? artifact)
    {
        if (_webPipelineArtifacts is { } artifacts)
            return artifacts.TryResolve(pass, out artifact);
        artifact = null;
        return false;
    }

    public ShaderProgramArtifact GetRequiredWebPipelineArtifact(string pass)
        => TryGetWebPipelineArtifact(pass, out ShaderProgramArtifact? artifact) ? artifact
            : throw new NotSupportedException($"WebGPU.Pipeline.ArtifactMissing: selected pass '{pass}' requires its exact cooked package program.");

    /// <summary>
    /// Regenerates the shared command chain once after authored properties have hydrated and
    /// before the browser attaches the pipeline to its physical output. No resources are built.
    /// </summary>
    [Browsable(false), YamlIgnore]
    public bool IsWebOutputPrepared => _webOutputPrepared;

    public void PrepareForWebOutput(PipelinePostProcessState? authored = null, IShaderProgramArtifactResolver? resolver = null)
        => PrepareForWebOutput(RenderPipelineResourceProfile.Empty, authored, resolver);

    public void PrepareForWebOutput(in RenderPipelineResourceProfile outputProfile, PipelinePostProcessState? authored = null, IShaderProgramArtifactResolver? resolver = null)
    {
        if (_webOutputPrepared)
            return;
        PassIndicesAndSorters = GetPassIndicesAndSorters();
        InitializeCommandChain();
        WebGpuPipelineAdmission.Validate(CreateRequirements(RendererBackendId.WebGPU, outputProfile, authored), this, resolver);
        SetField(ref _webOutputPrepared, true, publishNotifications: false);
    }

    public RenderPipelineRequirements CreateRequirements(RendererBackendId backend, PipelinePostProcessState? authored = null)
        => CreateRequirements(backend, RenderPipelineResourceProfile.Empty, authored);

    public RenderPipelineRequirements CreateRequirements(RendererBackendId backend, in RenderPipelineResourceProfile outputProfile, PipelinePostProcessState? authored = null)
    {
        RenderPipelineRequirements requirements = new(backend, CreatePostProcessAdmissionState(backend, authored), outputProfile);
        DescribeRequirements(requirements);
        DeclaredRequirements?.ApplyTo(requirements);
        return requirements;
    }

    /// <summary>Declares target operations and dependencies from the existing shared command graph.</summary>
    public virtual void DescribeRequirements(RenderPipelineRequirements requirements)
        => requirements.Include(CommandChain);

    /// <summary>Copies settings into this pipeline's schema, never a different pipeline's defaults.</summary>
    protected virtual PipelinePostProcessState CreatePostProcessAdmissionState(RendererBackendId backend, PipelinePostProcessState? authored)
        => CopyPostProcessAdmissionState(BuildPostProcessSchema(), authored);

    protected static PipelinePostProcessState CopyPostProcessAdmissionState(RenderPipelinePostProcessSchema schema, PipelinePostProcessState? authored)
    {
        PipelinePostProcessState state = new();
        state.BindToSchema(schema);
        if (authored is not null)
            foreach ((string key, PostProcessStageState source) in authored.Stages)
                if (state.GetStage(key) is { } destination)
                    foreach ((string parameter, object? value) in source.Values)
                        destination.SetValue(parameter, value);
        return state;
    }
}
