using XREngine.Rendering.Commands;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Resources;
using XREngine.Rendering.Resources.Authoring;

namespace XREngine.Rendering;

public class CustomRenderPipeline : RenderPipeline
{
    private Lazy<XRMaterial>? _invalidMaterialFactory;
    protected override Lazy<XRMaterial> InvalidMaterialFactory
        => _invalidMaterialFactory ??= new Lazy<XRMaterial>(() => CustomInvalidMaterial is not null ? CustomInvalidMaterial : XRMaterial.CreateUnlitColorMaterialForward());

    private XRMaterial? _customInvalidMaterial;
    public XRMaterial? CustomInvalidMaterial
    {
        get => _customInvalidMaterial;
        set => SetField(ref _customInvalidMaterial, value);
    }

    private ViewportRenderCommandContainer? _commands;
    public ViewportRenderCommandContainer? Commands
    {
        get => _commands;
        set
        {
            if (SetField(ref _commands, value))
                RefreshAuthoredCommands();
        }
    }

    private Dictionary<int, IComparer<RenderCommand>?>? _renderPasses = [];
    public Dictionary<int, IComparer<RenderCommand>?>? RenderPasses
    {
        get => _renderPasses;
        set
        {
            if (SetField(ref _renderPasses, value))
                RefreshAuthoredCommands();
        }
    }

    private List<AuthoredRenderResource> _authoredResources = [];
    private ulong _authoredResourceHash;

    /// <summary>
    /// Gets or sets the resources that this pipeline declares as data. The layout lowers them through
    /// <see cref="AuthoredRenderResourceFactory"/>. Set a new list, or call <see cref="NotifyAuthoredResourcesChanged"/>
    /// after an edit in place, to build a new resource generation.
    /// </summary>
    public List<AuthoredRenderResource> AuthoredResources
    {
        get => _authoredResources;
        set
        {
            if (SetField(ref _authoredResources, value ?? []))
                NotifyAuthoredResourcesChanged();
        }
    }

    /// <summary>
    /// Builds a new resource generation for every instance when a layout-affecting field of
    /// <see cref="AuthoredResources"/> changed.
    /// </summary>
    public void NotifyAuthoredResourcesChanged()
    {
        ulong hash = AuthoredRenderResourceFactory.ComputeLayoutHash(_authoredResources);
        if (hash == _authoredResourceHash)
            return;

        _authoredResourceHash = hash;
        InvalidateOwnedInstancePhysicalResources("AuthoredResourcesChanged");
    }

    /// <inheritdoc />
    protected override void DescribeResources(RenderPipelineResourceLayoutBuilder builder)
    {
        base.DescribeResources(builder);
        AuthoredRenderResourceFactory.Declare(builder, _authoredResources, ResolveAuthoredAttachment);
    }

    /// <inheritdoc />
    internal override ulong BuildResourceFeatureMaskForGenerationKey(XRRenderPipelineInstance instance, XRViewport? viewport)
        => base.BuildResourceFeatureMaskForGenerationKey(instance, viewport) ^ _authoredResourceHash;

    private static IFrameBufferAttachement? ResolveAuthoredAttachment(string name)
        => GetTexture<XRTexture>(name) as IFrameBufferAttachement ?? TryCurrentPipeline?.GetRenderBuffer(name);

    private void RefreshAuthoredCommands()
    {
        PassIndicesAndSorters = GetPassIndicesAndSorters();
        if (Instances.Count == 0)
            InitializeCommandChain();
        else
            RebuildCommandChain();
    }

    /// <summary>
    /// Initializes a compiled script command chain before this pipeline is assigned to a viewport.
    /// The pipeline has no instances during bootstrap, so its command and pass state can be published as one coherent initial generation.
    /// </summary>
    public void InitializeCompiledCommandsForBootstrap(ViewportRenderCommandContainer commands)
    {
        ArgumentNullException.ThrowIfNull(commands);

        if (Instances.Count != 0)
            throw new InvalidOperationException("A compiled script command chain can only be initialized before the pipeline has viewport instances.");

        // Derive the pass map before assigning the chain. CommandChain's setter immediately
        // publishes metadata, and consumers must never observe script metadata with an empty map.
        RenderPassMetadataCollection metadata = new();
        commands.BuildRenderPassMetadata(metadata);
        Dictionary<int, IComparer<RenderCommand>?> passes = GetPassIndicesAndSorters();
        foreach (RenderPassMetadata pass in metadata.Build())
            passes.TryAdd(pass.PassIndex, null);
        PassIndicesAndSorters = passes;

        Commands = commands;
        InitializeCommandChain();
    }

    protected override ViewportRenderCommandContainer GenerateCommandChain()
        => _commands ?? [];

    public override void DescribeRequirements(RenderPipelineRequirements requirements)
    {
        // Serialized authoring properties can arrive after the base constructor built
        // an empty chain. Publication inspects the authored graph without rebinding it.
        requirements.Include(Commands ?? CommandChain);
        if (CustomInvalidMaterial is { } material)
            requirements.RequireMaterial(material);
    }
    protected override Dictionary<int, IComparer<RenderCommand>?> GetPassIndicesAndSorters()
        => _renderPasses ?? [];
}
