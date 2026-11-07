using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.UnitTests.Rendering;

/// <summary>A minimal pipeline that registers branch settings for condition tests.</summary>
internal sealed class BranchSettingTestPipeline : RenderPipeline
{
    /// <summary>Gets or sets the bool setting that the pipeline registers as <c>UseBloom</c>.</summary>
    public bool UseBloom { get; set; }

    /// <summary>Gets or sets the integer setting that the pipeline registers as <c>Quality</c>.</summary>
    public int Quality { get; set; }

    protected override Lazy<XRMaterial> InvalidMaterialFactory => new(() => new XRMaterial());

    protected override ViewportRenderCommandContainer GenerateCommandChain()
        => new(this);

    protected override Dictionary<int, IComparer<RenderCommand>?> GetPassIndicesAndSorters()
        => [];

    protected override void DescribeBranchSettings(RenderPipelineBranchSettings settings)
        => settings
            .Add(nameof(UseBloom), () => UseBloom)
            .Add(nameof(Quality), () => Quality);
}
