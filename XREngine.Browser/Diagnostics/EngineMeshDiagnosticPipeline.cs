using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Browser.Diagnostics;

/// <summary>Explicit diagnostic engine pipeline for validating cooked mesh depth without post processing.</summary>
internal sealed class EngineMeshDiagnosticPipeline : RenderPipeline
{
    private readonly XRMaterial _diagnosticMaterial;
    private readonly NearToFarRenderCommandSorter _sorter = new();

    public EngineMeshDiagnosticPipeline(XRMaterial diagnosticMaterial) : base(true)
    {
        _diagnosticMaterial = diagnosticMaterial;
        InitializeCommandChain();
    }

    public override string DebugName => "EngineMeshDepthDiagnostic";
    protected override Lazy<XRMaterial> InvalidMaterialFactory => new(() => _diagnosticMaterial);

    protected override ViewportRenderCommandContainer GenerateCommandChain()
    {
        ViewportRenderCommandContainer commands = new(this);
        commands.Add<VPRC_DepthTest>().Enable = true;
        commands.Add<VPRC_DepthWrite>().Allow = true;
        commands.Add<VPRC_DepthFunc>().Comp = EComparison.Less;
        using (commands.AddUsing<VPRC_PushViewportRenderArea>(options => options.UseInternalResolution = false))
        using (commands.AddUsing<VPRC_BindOutputFBO>(options => options.SetOptions(
            write: true, clearColor: true, clearDepth: true, clearStencil: false)))
            commands.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.OpaqueForward, EMeshSubmissionStrategy.CpuDirect);
        return commands;
    }

    protected override Dictionary<int, IComparer<RenderCommand>?> GetPassIndicesAndSorters()
        => new()
        {
            [(int)EDefaultRenderPass.PreRender] = null,
            [(int)EDefaultRenderPass.OpaqueForward] = _sorter,
            [(int)EDefaultRenderPass.PostRender] = null,
        };
}
