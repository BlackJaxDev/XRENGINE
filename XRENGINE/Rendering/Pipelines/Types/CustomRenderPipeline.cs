using XREngine.Rendering.Commands;
using XREngine.Rendering.Pipelines.Commands;

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

    protected override ViewportRenderCommandContainer GenerateCommandChain()
        => _commands ?? [];
    protected override Dictionary<int, IComparer<RenderCommand>?> GetPassIndicesAndSorters()
        => _renderPasses ?? [];
}
