using XREngine.Rendering.Commands;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.RenderGraph;

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
        set => SetField(ref _commands, value);
    }

    private Dictionary<int, IComparer<RenderCommand>?>? _renderPasses = [];
    public Dictionary<int, IComparer<RenderCommand>?>? RenderPasses
    {
        get => _renderPasses;
        set => SetField(ref _renderPasses, value);
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
    protected override Dictionary<int, IComparer<RenderCommand>?> GetPassIndicesAndSorters()
        => _renderPasses ?? [];
}
